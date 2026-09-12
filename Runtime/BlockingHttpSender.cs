#nullable enable

// No sockets on WebGL (the browser sandbox only exposes fetch/XHR), and its single
// thread cannot be blocked, so FlushBlocking is a no-op there (see TelemetrySDK).
// Every other Unity target ships System.Net.Sockets / System.Net.Security through
// .NET Standard 2.1 under both Mono and IL2CPP.
#if !UNITY_WEBGL

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;

namespace Framedash
{
    /// <summary>
    /// Synchronous, budget-bounded HTTP POST for <see cref="TelemetrySDK.FlushBlocking"/>.
    /// The blocking flush runs on a main thread that cannot pump Unity's coroutine /
    /// UnityWebRequest path, so the request is driven straight through System.Net
    /// sockets, modeled on the Godot SDK's proven synchronous teardown drain
    /// (TransportLayer.PostBlocking) and the direct-socket fallback here
    /// (<see cref="DirectSocketSender"/>).
    ///
    /// Bounding: a single linked <see cref="CancellationTokenSource"/> is armed with
    /// <c>CancelAfter(remainingBudget)</c> and CLOSES the socket when it fires, which
    /// faults whichever blocking step is in progress (DNS resolve, connect, TLS
    /// handshake, write, or status read) into the catch. Send/ReceiveTimeout are
    /// belt-and-braces per-op caps. So every step is bounded and the total wall time
    /// never exceeds the remaining flush budget, even though each individual
    /// System.Net call has no timeout parameter.
    ///
    /// Security: connect is by HOSTNAME (HttpClient/OS resolves), so TLS runs the FULL
    /// standard certificate validation (chain, expiry, hostname match) against the real
    /// FQDN -- no IP-literal pinning is needed on this one-shot path (the async
    /// fallback's IPv4-preference is a per-flush latency optimization, and the budget
    /// already caps a slow resolve). Endpoint transport-security (HTTPS, or HTTP only
    /// for loopback) is enforced by the caller before this runs.
    ///
    /// BCL-only but excluded from the NextUnit assembly like DirectSocketSender, because
    /// exercising it requires a live TLS endpoint; the testable pieces (budget /
    /// leading-count state machine, request-head formatting) live in
    /// <see cref="BlockingFlush"/> and <see cref="RawHttpMessage"/>.
    /// </summary>
    internal sealed class BlockingHttpSender
    {
        private const int StatusReadBufferBytes = 1024;

        private readonly string _endpointUrl;
        private readonly string _apiKey;
        private readonly string _sdkVersion;

        public BlockingHttpSender(string endpointUrl, string apiKey, string sdkVersion)
        {
            _endpointUrl = endpointUrl;
            _apiKey = apiKey;
            _sdkVersion = sdkVersion;
        }

        /// <summary>
        /// POST <paramref name="payload"/> synchronously, bounded to
        /// <paramref name="remainingBudgetMs"/> of wall time. Returns the HTTP status
        /// code, or 0 for any transport-level failure / budget exhaustion. Never throws.
        /// </summary>
        public long Post(byte[] payload, long remainingBudgetMs)
        {
            if (remainingBudgetMs <= 0) return 0;
            if (!Uri.TryCreate(_endpointUrl, UriKind.Absolute, out var uri)) return 0;

            bool useTls;
            if (uri.Scheme == Uri.UriSchemeHttps) useTls = true;
            else if (uri.Scheme == Uri.UriSchemeHttp) useTls = false;
            else return 0;

            string host = uri.DnsSafeHost;
            string requestTarget = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            // Explicit Host header (FQDN, "host" or "host:port") so Cloudflare routes by
            // hostname; RawHttpMessage sanitizes it and the developer-supplied values.
            string hostHeader = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
            byte[] head = RawHttpMessage.BuildPostHead(
                requestTarget, hostHeader, _apiKey, _sdkVersion, payload.Length);

            // Resolve to concrete IP candidates under the budget FIRST: TcpClient.Connect(
            // host, port) does a SYNCHRONOUS DNS lookup that closing the socket (the CTS
            // callback below) cannot interrupt, so a stalled resolver would hang the main
            // thread past the budget and the 30s clamp. Connecting to an IPAddress instead is
            // interruptible (Close faults the blocking connect), matching DirectSocketSender.
            var overall = Stopwatch.StartNew();
            if (!TryResolveWithinBudget(host, remainingBudgetMs, out IPAddress[] candidates))
                return 0;

            // Attempt candidates in order (IPv4 first, then IPv6) under the SHARED deadline,
            // so a dual-stack host whose IPv4 is unreachable but IPv6 works still delivers
            // within the budget -- parity with the async transport's IPv4->IPv6 fallback. The
            // CONNECT phase of each candidate is capped at a reserved fair share
            // (BlockingFlush.ConnectBudgetMs) so a blackholing preferred address cannot consume
            // the whole budget and starve a reachable later candidate; the connected
            // candidate's request/response phase may still use the full remaining budget. Stop
            // at the first HTTP response (status != 0, the server was reached); only a
            // transport-level failure (status 0) falls through to the next family.
            for (int i = 0; i < candidates.Length; i++)
            {
                long remaining = remainingBudgetMs - overall.ElapsedMilliseconds;
                if (remaining <= 0) return 0;
                long connectBudgetMs = BlockingFlush.ConnectBudgetMs(remaining, candidates.Length, i);
                long status = PostToAddress(candidates[i], uri.Port, useTls, host, head, payload, connectBudgetMs, remaining);
                if (status != 0) return status;
            }
            return 0;
        }

        // One connect+TLS+send+read attempt to a specific resolved address. The connect +
        // handshake is bounded to <paramref name="connectBudgetMs"/> (a reserved fair share
        // so a later candidate is never starved), and the whole attempt (including the
        // request/response) is bounded to <paramref name="overallRemainingMs"/>. Returns the
        // HTTP status code, or 0 for any transport-level failure (so the caller tries the
        // next address family). Never throws.
        private long PostToAddress(
            IPAddress address, int port, bool useTls, string tlsHost,
            byte[] head, byte[] payload, long connectBudgetMs, long overallRemainingMs)
        {
            if (connectBudgetMs <= 0 || overallRemainingMs <= 0) return 0;
            TcpClient? client = null;
            var attempt = Stopwatch.StartNew();
            try
            {
                // CancelAfter closes the socket when its deadline fires, faulting whichever
                // blocking call is in progress -- the sole mechanism that bounds the
                // otherwise-unbounded connect / handshake / read steps. Armed first at the
                // (short) connect cap, then extended to the full remaining budget once the
                // connection is established.
                using (var cts = new CancellationTokenSource())
                {
                    cts.CancelAfter(TimeSpan.FromMilliseconds(connectBudgetMs));
                    int perOpMs = overallRemainingMs > int.MaxValue ? int.MaxValue : (int)overallRemainingMs;
                    client = new TcpClient(address.AddressFamily) { SendTimeout = perOpMs, ReceiveTimeout = perOpMs };
                    using (cts.Token.Register(() => SafeClose(client)))
                    {
                        client.Connect(address, port);

                        Stream stream = client.GetStream();
                        SslStream? ssl = null;
                        try
                        {
                            if (useTls)
                            {
                                ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                                ssl.AuthenticateAsClient(tlsHost);
                                stream = ssl;
                            }

                            // Connected + handshaked: extend the deadline to the remaining
                            // overall budget for the request/response phase (still bounded by
                            // the shared budget -- total attempt time <= overallRemainingMs).
                            long requestRemainingMs = overallRemainingMs - attempt.ElapsedMilliseconds;
                            if (requestRemainingMs <= 0) return 0;
                            cts.CancelAfter(TimeSpan.FromMilliseconds(requestRemainingMs));

                            stream.Write(head, 0, head.Length);
                            stream.Write(payload, 0, payload.Length);
                            stream.Flush();
                            return ReadStatusCode(stream);
                        }
                        finally
                        {
                            ssl?.Dispose();
                        }
                    }
                }
            }
            catch
            {
                // Any failure (connect, TLS validation, budget-triggered close, reset) is a
                // transport-level failure: status 0, exactly like a failed UnityWebRequest.
                // Never throw out of the SDK.
                return 0;
            }
            finally
            {
                SafeClose(client);
            }
        }

        // An IP-literal host is used directly (no DNS). The async resolve runs on the thread
        // pool and is ABANDONED on timeout (bounded to one leaked worker on the rare
        // FlushBlocking path) rather than blocking the main thread past the budget -- a stalled
        // synchronous DNS lookup is exactly the hang this guards against. Never throws; returns
        // false on timeout, failure, or empty result.
        private static bool TryResolveWithinBudget(string host, long budgetMs, out IPAddress[] candidates)
        {
            candidates = System.Array.Empty<IPAddress>();
            // Uri.DnsSafeHost strips IPv6 brackets, so an IP literal parses here directly.
            if (IPAddress.TryParse(host, out IPAddress? literal) && literal != null)
            {
                candidates = new[] { literal };
                return true;
            }
            if (budgetMs <= 0) return false;
            try
            {
                var task = Dns.GetHostAddressesAsync(host);
                int waitMs = budgetMs > int.MaxValue ? int.MaxValue : (int)budgetMs;
                // Wait bounds the main-thread block; a stalled resolver returns false here
                // and the still-running task is left to the thread pool (not awaited).
                if (!task.Wait(waitMs)) return false;
                IPAddress[] addresses = task.Result;
                if (addresses == null || addresses.Length == 0) return false;
                var ordered = new List<IPAddress>(addresses.Length);
                foreach (var a in addresses)
                    if (a.AddressFamily == AddressFamily.InterNetwork) ordered.Add(a);
                foreach (var a in addresses)
                    if (a.AddressFamily == AddressFamily.InterNetworkV6) ordered.Add(a);
                if (ordered.Count == 0) return false;
                candidates = ordered.ToArray();
                return true;
            }
            catch
            {
                // A faulted resolve (Wait rethrows as AggregateException) or any other error
                // is a transport-level failure; never throw out of the SDK.
                candidates = System.Array.Empty<IPAddress>();
                return false;
            }
        }

        // Read until the status line is complete (first LF) or the buffer/stream is
        // exhausted. "Connection: close" is sent with the request, so not draining the
        // rest of the response is fine -- the server ends the connection.
        private static long ReadStatusCode(Stream stream)
        {
            var buffer = new byte[StatusReadBufferBytes];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0) break;
                total += read;
                if (RawHttpMessage.TryParseStatusCode(buffer, total, out long code))
                    return code;
            }
            return 0;
        }

        private static void SafeClose(TcpClient? client)
        {
            try { client?.Close(); }
            catch {   }
        }
    }
}

#endif
