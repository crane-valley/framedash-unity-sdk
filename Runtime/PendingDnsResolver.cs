#nullable enable

using System;
using System.Net;
using System.Threading.Tasks;

namespace Framedash
{
    internal sealed class PendingDnsResolver
    {
        private readonly Func<string, Task<IPAddress[]>> _resolve;
        private readonly object _sync = new object();
        private Task<IPAddress[]>? _pending;
        private string _pendingHost = "";

        public PendingDnsResolver(Func<string, Task<IPAddress[]>> resolve)
        {
            _resolve = resolve;
        }

        public bool TryResolve(string host, int timeoutMs, out IPAddress[] addresses)
        {
            addresses = Array.Empty<IPAddress>();
            if (timeoutMs <= 0) return false;
            Task<IPAddress[]>? task = null;
            try
            {
                lock (_sync)
                {
                    if (_pending != null && !string.Equals(host, _pendingHost, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_pending.IsCompleted) return false;
                        _ = _pending.Exception;
                        _pending = null;
                    }
                    if (_pending == null)
                    {
                        _pending = _resolve(host);
                        _pendingHost = host;
                    }
                    task = _pending;
                }
                if (!task.Wait(timeoutMs)) return false;
                addresses = task.Result;
                return addresses.Length > 0;
            }
            catch { return false; }
            finally
            {
                lock (_sync)
                {
                    if (task != null && task.IsCompleted && ReferenceEquals(task, _pending))
                        _pending = null;
                }
            }
        }
    }
}
