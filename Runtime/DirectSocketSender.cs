#nullable enable

// No sockets on WebGL (the browser sandbox only exposes fetch/XHR, which
// UnityWebRequest already uses), so WebGL keeps the pre-fallback behavior:
// fail-fast 10s timeout + default-on offline persistence. All other Unity
// targets (desktop, mobile, consoles, UWP) ship System.Net.Sockets /
// System.Net.Security through .NET Standard 2.1 under both Mono and IL2CPP,
// so no further platform guard is needed.
#if !UNITY_WEBGL

using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Framedash
{
    /// <summary>
    /// Direct-socket TLS fallback transport for the prefer-IPv4-with-IPv6-fallback
    /// ingest connect. Used ONLY when the primary UnityWebRequest attempt fails at
    /// the transport level (responseCode 0): connects a TcpClient straight to a
    /// resolved IP literal (pinning the address family, which UnityWebRequest cannot
    /// do), then authenticates TLS against the ORIGINAL FQDN via
    /// SslStream.AuthenticateAsClient(commonName).
    ///
    /// Security note -- why SslStream and NOT UnityWebRequest.CertificateHandler:
    /// AuthenticateAsClient(targetHost) sends the FQDN as SNI and runs the FULL
    /// standard certificate validation (chain building, expiry, hostname match)
    /// against that FQDN with the platform trust store, so connecting to an IP
    /// literal loses no validation whatsoever. A CertificateHandler-based design
    /// cannot achieve this: ValidateCertificate receives raw certificate DER with no
    /// validated chain, and returning true bypasses ALL of Unity's validation, so a
    /// safe reimplementation of chain/expiry/name checking is not feasible there.
    /// UnityWebRequest also forbids overriding the Host header (it is on the
    /// disallowed-header list), so the IP-literal-URL + Host-header approach could
    /// not preserve the hostname for Cloudflare Worker routing anyway. Do NOT add a
    /// RemoteCertificateValidationCallback here -- the default validation IS the
    /// design.
    ///
    /// Engine-independent by construction (BCL only), but excluded from the NUnit
    /// test assembly like TransportLayer because exercising it requires a live TLS
    /// endpoint; the testable pieces live in EndpointAddressPlanner and
    /// RawHttpMessage.
    /// </summary>
    internal static class DirectSocketSender
    {
        // Response head is tiny (status line + a few headers); 1 KiB is ample to
        // capture the status line, which is all the retry classification needs.
        private const int StatusReadBufferBytes = 1024;

        /// <summary>
        /// POST <paramref name="payload"/> to <paramref name="ipLiteralUrl"/> on the
        /// thread pool. Returns the HTTP status code, or 0 for ANY transport-level
        /// failure (connect/TLS/write/read error, timeout, or cancellation) -- the
        /// same contract as UnityWebRequest.responseCode, so TransportLayer
        /// classifies both paths identically. Never throws: the returned task always
        /// completes successfully (fail-safe contract; the coroutine polls it
        /// unguarded).
        ///
        /// <paramref name="cancellationToken"/> is the CALLER's abandon signal: the
        /// polling coroutine cancels it when it stops waiting for this task. Without
        /// it, a Task.Run still queued behind a busy thread pool could fire the POST
        /// LATER, after the caller already classified the attempt as status 0 and
        /// retried/persisted the batch -- a duplicate delivery the offline-queue ack
        /// (DeliveredLeadingCount) can not see. The token is checked before any
        /// network side effect; a send already past the request write is the
        /// accepted inherent race (same as any timed-out HTTP request the server did
        /// receive).
        /// </summary>
        public static Task<long> PostAsync(
            string ipLiteralUrl, string commonName, byte[] head, byte[] payload,
            int timeoutSeconds, CancellationToken cancellationToken)
        {
            return Task.Run(
                () => PostBlocking(ipLiteralUrl, commonName, head, payload, timeoutSeconds, cancellationToken));
        }

        // Blocking I/O on a thread-pool thread (never the main thread). One overall
        // timeout covers connect + TLS handshake + request + status read: the linked
        // CTS (internal timeout OR caller abandon) closes the TcpClient, which faults
        // whichever blocking call is in progress into the catch below.
        // Send/ReceiveTimeout are belt-and-braces per-op caps.
        private static long PostBlocking(
            string ipLiteralUrl, string commonName, byte[] head, byte[] payload,
            int timeoutSeconds, CancellationToken cancellationToken)
        {
            TcpClient? client = null;
            try
            {
                // Abandoned before the task even ran: never open a connection.
                if (cancellationToken.IsCancellationRequested) return 0;

                var uri = new Uri(ipLiteralUrl);
                // Uri.Host keeps IPv6 brackets; IPAddress.Parse wants them stripped.
                var address = IPAddress.Parse(TrimBrackets(uri.Host));

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    client = new TcpClient(address.AddressFamily);
                    client.SendTimeout = timeoutSeconds * 1000;
                    client.ReceiveTimeout = timeoutSeconds * 1000;
                    using (cts.Token.Register(() => SafeClose(client)))
                    {
                        client.Connect(address, uri.Port);

                        // Default validation callback + default protocols (OS choice):
                        // full chain/expiry/hostname validation against commonName.
                        using (var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
                        {
                            ssl.AuthenticateAsClient(commonName);
                            // Last no-side-effect gate: past the write below the
                            // request may reach the server, so a later abandon is the
                            // accepted in-flight race, not a duplicate-fire bug.
                            if (cancellationToken.IsCancellationRequested) return 0;
                            ssl.Write(head, 0, head.Length);
                            ssl.Write(payload, 0, payload.Length);
                            ssl.Flush();
                            return ReadStatusCode(ssl);
                        }
                    }
                }
            }
            catch
            {
                // Any failure (parse, connect, TLS validation, timeout-triggered
                // close, reset) is a transport-level failure: status 0, exactly like
                // a failed UnityWebRequest. Never throw out of the SDK.
                return 0;
            }
            finally
            {
                SafeClose(client);
            }
        }

        // Read until the status line is complete (first LF) or the buffer/stream is
        // exhausted. "Connection: close" is sent with the request, so not draining
        // the rest of the response is fine -- the server ends the connection.
        private static long ReadStatusCode(SslStream ssl)
        {
            var buffer = new byte[StatusReadBufferBytes];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = ssl.Read(buffer, total, buffer.Length - total);
                if (read <= 0) break;
                total += read;
                if (RawHttpMessage.TryParseStatusCode(buffer, total, out long code))
                    return code;
            }
            return 0;
        }

        private static string TrimBrackets(string host)
        {
            if (host.Length >= 2 && host[0] == '[' && host[host.Length - 1] == ']')
                return host.Substring(1, host.Length - 2);
            return host;
        }

        private static void SafeClose(TcpClient? client)
        {
            try { client?.Close(); }
            catch { /* best-effort teardown */ }
        }
    }
}

#endif
