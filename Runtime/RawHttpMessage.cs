#nullable enable

using System;
using System.Text;

namespace Framedash
{
    /// <summary>
    /// Pure, engine-independent HTTP/1.1 message helpers for the direct-socket
    /// IPv4/IPv6 fallback transport (see DirectSocketSender). Builds the request head
    /// for a POST and parses the response status line; nothing else of HTTP/1.1 is
    /// needed because the transport only feeds the status code into RetryPolicy
    /// (the normal UnityWebRequest path uses responseCode the same way) and always
    /// sends "Connection: close". No UnityEngine or socket types so it is
    /// NUnit-testable.
    /// </summary>
    public static class RawHttpMessage
    {
        /// <summary>
        /// Build the ASCII request head (request line + headers + blank line) for the
        /// telemetry POST. The caller writes this followed by the gzip payload bytes.
        /// Headers mirror the UnityWebRequest path exactly (Content-Type,
        /// Content-Encoding, X-API-Key, X-SDK-Version) plus the framing headers the
        /// raw path must supply itself: Host (the FQDN, so Cloudflare routes by
        /// hostname despite the IP-literal connect), Content-Length, and
        /// "Connection: close" (one request per connection; the parser never needs
        /// keep-alive framing).
        /// </summary>
        public static byte[] BuildPostHead(
            string requestTarget, string hostHeader, string apiKey, string sdkVersion, int contentLength)
        {
            var sb = new StringBuilder(256);
            sb.Append("POST ").Append(SanitizeRequestTarget(requestTarget)).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(SanitizeHeaderValue(hostHeader)).Append("\r\n");
            sb.Append("Content-Type: application/x-protobuf\r\n");
            sb.Append("Content-Encoding: gzip\r\n");
            sb.Append("X-API-Key: ").Append(SanitizeHeaderValue(apiKey)).Append("\r\n");
            sb.Append("X-SDK-Version: ").Append(SanitizeHeaderValue(sdkVersion)).Append("\r\n");
            sb.Append("Content-Length: ").Append(contentLength).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        /// <summary>
        /// Sanitize an origin-form request target for the request line. Stricter
        /// than <see cref="SanitizeHeaderValue"/>: the request line is
        /// space-delimited ("POST target HTTP/1.1"), so a SPACE in the target would
        /// split the line (request-smuggling shape), not just look odd -- strip all
        /// ASCII control characters AND spaces, then force the origin-form leading
        /// "/" (RFC 9112 3.2.1). Internal callers always pass Uri.PathAndQuery
        /// (which is escaped and starts with "/"), but the class is public, so the
        /// invariant is enforced here rather than assumed.
        /// </summary>
        public static string SanitizeRequestTarget(string? requestTarget)
        {
            if (string.IsNullOrEmpty(requestTarget)) return "/";
            StringBuilder? sb = null;
            for (int i = 0; i < requestTarget!.Length; i++)
            {
                char c = requestTarget[i];
                // Reject SP (0x20) too, unlike the header-value rule.
                bool ok = c > 0x20 && c != (char)0x7F;
                if (!ok && sb == null)
                {
                    sb = new StringBuilder(requestTarget.Length);
                    sb.Append(requestTarget, 0, i);
                }
                else if (ok && sb != null)
                {
                    sb.Append(c);
                }
            }
            string clean = sb == null ? requestTarget : sb.ToString();
            if (clean.Length == 0) return "/";
            return clean[0] == '/' ? clean : "/" + clean;
        }

        /// <summary>
        /// Strip CR/LF and other ASCII control characters from a header value so a
        /// malformed developer-supplied value (API key, SDK version) can never split
        /// the request into extra header lines (request-smuggling hygiene). The
        /// UnityWebRequest path performs the equivalent validation internally.
        /// </summary>
        public static string SanitizeHeaderValue(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            StringBuilder? sb = null;
            for (int i = 0; i < value!.Length; i++)
            {
                char c = value[i];
                bool ok = c >= 0x20 && c != (char)0x7F;
                if (!ok && sb == null)
                {
                    sb = new StringBuilder(value.Length);
                    sb.Append(value, 0, i);
                }
                else if (ok && sb != null)
                {
                    sb.Append(c);
                }
            }
            return sb == null ? value : sb.ToString();
        }

        /// <summary>
        /// Parse the HTTP status code out of a partially-read response buffer. Returns
        /// true once a COMPLETE status line ("HTTP/1.1 202 Accepted\r\n") is present in
        /// the first <paramref name="count"/> bytes and carries a 3-digit code in
        /// 100..999; false while the line is still incomplete OR the line is not HTTP
        /// (the caller treats never-true as a transport-level failure, status 0).
        /// </summary>
        public static bool TryParseStatusCode(byte[] buffer, int count, out long statusCode)
        {
            statusCode = 0;
            if (buffer == null) return false;
            if (count > buffer.Length) count = buffer.Length;

            // The status line ends at the first LF; without one the line may still be
            // arriving, so report "not yet" and let the caller read more bytes.
            int lineEnd = -1;
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == (byte)'\n') { lineEnd = i; break; }
            }
            if (lineEnd < 0) return false;

            string line = Encoding.ASCII.GetString(buffer, 0, lineEnd).TrimEnd('\r');

            // "HTTP/<version> <code> [reason]" -- accept any version token so an
            // HTTP/1.0 status line from an intermediary still parses.
            if (!line.StartsWith("HTTP/", StringComparison.Ordinal)) return false;
            int firstSpace = line.IndexOf(' ');
            if (firstSpace < 0 || firstSpace + 1 >= line.Length) return false;

            int codeEnd = line.IndexOf(' ', firstSpace + 1);
            string codeToken = codeEnd < 0
                ? line.Substring(firstSpace + 1)
                : line.Substring(firstSpace + 1, codeEnd - firstSpace - 1);

            if (codeToken.Length != 3) return false;
            if (!long.TryParse(codeToken, out long code)) return false;
            if (code < 100 || code > 999) return false;

            statusCode = code;
            return true;
        }
    }
}
