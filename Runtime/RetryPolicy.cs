using System;

namespace Framedash
{
    /// <summary>
    /// Pure retry decision logic extracted from TransportLayer.
    /// No Unity dependencies -- testable with NextUnit.
    /// </summary>
    public sealed class RetryPolicy
    {
        public int MaxRetries { get; }
        public float BaseDelaySeconds { get; }

        public RetryPolicy(int maxRetries = 5, float baseDelaySeconds = 1f)
        {
            MaxRetries = maxRetries > 0 ? maxRetries : 5;
            BaseDelaySeconds = baseDelaySeconds > 0f ? baseDelaySeconds : 1f;
        }

        public bool ShouldSplitBatch(long httpStatusCode, int eventCount)
        {
            return httpStatusCode == 413 && eventCount > 1;
        }

        public bool IsNonRetryableError(long httpStatusCode)
        {
            return httpStatusCode >= 400 && httpStatusCode < 500
                && httpStatusCode != 413
                && httpStatusCode != 429;
        }

        internal bool ShouldRetry(long httpStatusCode, int attempt)
        {
            if (attempt >= MaxRetries) return false;

            if (httpStatusCode == 0) return true;

            if (httpStatusCode == 429) return true;

            if (httpStatusCode >= 500) return true;

            return false;
        }

        public float GetRetryDelaySeconds(int attempt)
        {
            if (attempt < 0) attempt = 0;
            return BaseDelaySeconds * (float)Math.Pow(2, attempt);
        }

        public RetryAction Classify(long httpStatusCode, int attempt, int eventCount)
        {
            if (httpStatusCode >= 200 && httpStatusCode < 300)
                return RetryAction.Success;

            if (ShouldSplitBatch(httpStatusCode, eventCount))
                return RetryAction.SplitBatch;

            if (IsNonRetryableError(httpStatusCode))
                return RetryAction.Fail;

            // 413 with unsplittable single event -- can't split, can't retry
            if (httpStatusCode == 413)
                return RetryAction.Fail;

            // 3xx: UnityWebRequest.redirectLimit=0 means redirects are never
            // followed, so a surfaced 3xx indicates a misconfigured or
            // compromised endpoint. Retrying cannot succeed -- fail immediately
            // so the error surfaces rather than consuming the full retry budget
            // on every batch. Mirrors UE5 FRetryPolicy behavior exactly.
            if (httpStatusCode >= 300 && httpStatusCode < 400)
                return RetryAction.Fail;

            if (attempt >= MaxRetries)
                return RetryAction.Fail;

            return RetryAction.Retry;
        }
    }

    public enum RetryAction
    {
        Success,
        Retry,
        SplitBatch,
        Fail,
    }
}
