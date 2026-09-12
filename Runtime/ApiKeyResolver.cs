#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;

namespace Framedash
{
    /// <summary>
    /// Pure API-key precedence resolution (F32), extracted so the rule is
    /// unit-testable without UnityEngine or a live process environment.
    ///
    /// Precedence (highest first):
    ///   1. An explicitly configured key (Inspector field or
    ///      <c>TelemetrySDK.Initialize(apiKey)</c>).
    ///   2. The <c>FRAMEDASH_API_KEY</c> environment variable (the CI fallback,
    ///      consistent with the Framedash CLI's <c>--api-key</c> vs
    ///      <c>FRAMEDASH_API_KEY</c> contract: an explicit value always wins over
    ///      the environment).
    ///
    /// An explicit key is honored even when a (different) env var is also set, so a
    /// developer's configured key is never silently overridden by a stray CI
    /// variable. When no key is configured, the env var lets a CI build authenticate
    /// without hardcoding a secret in the project.
    /// </summary>
    public static class ApiKeyResolver
    {
        /// <summary>
        /// Returns the configured key when it is non-empty; otherwise the env value when it is
        /// non-empty; otherwise the (empty/null) configured value unchanged, so the caller's
        /// existing "key required" validation still fires when neither source supplies one.
        ///
        /// The env value is supplied via a `Func{TResult}` so it is read LAZILY -- only when no
        /// configured key exists. This avoids an environment lookup on every initialization
        /// when an explicit key is present, and, because a restricted/sandboxed runtime can
        /// throw on environment access, the accessor is wrapped in a try/catch that degrades to
        /// "no env key" rather than letting the exception escape (fail-safe: an SDK fault must
        /// never disrupt the game).
        ///
        /// The Inspector/Initialize key, or null/empty if unset.
        ///
        /// Invoked only when `configuredKey` is empty. A null accessor, or one that throws, is
        /// treated as "no env key".
        ///
        /// A non-null configuredKey is returned on every fallback path (empty accessor,
        /// throwing accessor, or no env value), so it can never yield null.
        /// </summary>
        [return: NotNullIfNotNull("configuredKey")]
        public static string? Resolve(string? configuredKey, Func<string?>? envAccessor)
        {
            if (!string.IsNullOrEmpty(configuredKey)) return configuredKey;
            if (envAccessor == null) return configuredKey;

            string? envKey;
            try
            {
                envKey = envAccessor();
            }
            catch (Exception)
            {
                // Fail-safe: a sandboxed/restricted runtime may throw (e.g.
                // SecurityException) on environment access. Degrade to no env key so the
                // exception never escapes Start()/InitializeInternal().
                return configuredKey;
            }

            if (!string.IsNullOrEmpty(envKey)) return envKey;
            return configuredKey;
        }
    }
}
