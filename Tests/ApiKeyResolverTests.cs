using System;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class ApiKeyResolverTests
    {
        [Test]
        public void Resolve_ConfiguredKeySet_EnvUnset_UsesConfigured()
        {
            string result = ApiKeyResolver.Resolve("fd_configured", () => null);
            Assert.That(result, Is.EqualTo("fd_configured"));
        }

        [Test]
        public void Resolve_ConfiguredEmpty_EnvSet_FallsBackToEnv()
        {
            // F32: the CI fallback -- no configured key, so FRAMEDASH_API_KEY is adopted.
            Assert.That(ApiKeyResolver.Resolve("", () => "fd_env"), Is.EqualTo("fd_env"));
            Assert.That(ApiKeyResolver.Resolve(null, () => "fd_env"), Is.EqualTo("fd_env"));
        }

        [Test]
        public void Resolve_BothSet_ConfiguredWins()
        {
            // Explicit config always wins over the environment, matching the CLI's
            // --api-key vs FRAMEDASH_API_KEY precedence: a stray CI variable never
            // silently overrides a developer's configured key.
            string result = ApiKeyResolver.Resolve("fd_configured", () => "fd_env");
            Assert.That(result, Is.EqualTo("fd_configured"));
        }

        [Test]
        public void Resolve_NeitherSet_ReturnsEmptyOrNull()
        {
            // Neither source supplies a key: return the (empty/null) configured value so
            // the caller's "key required" validation still fires.
            Assert.That(string.IsNullOrEmpty(ApiKeyResolver.Resolve("", () => "")), Is.True);
            Assert.That(string.IsNullOrEmpty(ApiKeyResolver.Resolve(null, () => null)), Is.True);
        }

        [Test]
        public void Resolve_ConfiguredKeySet_DoesNotInvokeEnvAccessor()
        {
            // P2-2: the env lookup is lazy -- when an explicit key exists the accessor is
            // never called, so a restricted runtime pays no environment-access cost/risk.
            bool invoked = false;
            string result = ApiKeyResolver.Resolve("fd_configured", () =>
            {
                invoked = true;
                return "fd_env";
            });
            Assert.That(result, Is.EqualTo("fd_configured"));
            Assert.That(invoked, Is.False, "env accessor must not be invoked when a configured key exists");
        }

        [Test]
        public void Resolve_ConfiguredEmpty_InvokesEnvAccessorOnce()
        {
            int calls = 0;
            string result = ApiKeyResolver.Resolve("", () =>
            {
                calls++;
                return "fd_env";
            });
            Assert.That(result, Is.EqualTo("fd_env"));
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void Resolve_ThrowingEnvAccessor_DegradesToConfigured()
        {
            // P2-2: a sandboxed/restricted runtime may throw on env access; the resolver
            // swallows it and degrades to the (empty) configured key rather than letting
            // the exception escape Start()/InitializeInternal().
            Assert.DoesNotThrow(() =>
            {
                string result = ApiKeyResolver.Resolve(
                    "", () => throw new InvalidOperationException("env access denied"));
                Assert.That(string.IsNullOrEmpty(result), Is.True);
            });
        }

        [Test]
        public void Resolve_ThrowingEnvAccessor_NotInvokedWhenConfiguredKeyExists()
        {
            // The throwing accessor is never reached when a configured key is present, so
            // an explicit-key init is unaffected by a hostile environment.
            Assert.DoesNotThrow(() =>
            {
                string result = ApiKeyResolver.Resolve(
                    "fd_configured", () => throw new InvalidOperationException("env access denied"));
                Assert.That(result, Is.EqualTo("fd_configured"));
            });
        }

        [Test]
        public void Resolve_NullEnvAccessor_TreatedAsNoEnvKey()
        {
            string result = ApiKeyResolver.Resolve("", null);
            Assert.That(string.IsNullOrEmpty(result), Is.True);
        }

        [Test]
        public void Resolve_ReRunWithChangedEnv_PicksUpNewValue()
        {
            // P2-3: the resolver holds no state and re-reads the accessor on every call,
            // so an env-only re-initialization (Shutdown + Initialize) after the process
            // changes FRAMEDASH_API_KEY resolves to the NEW value. This works only because
            // the caller resolves into a local each init rather than promoting the first
            // env value into the configured key.
            string current = "fd_env_1";
            Func<string> accessor = () => current;

            Assert.That(ApiKeyResolver.Resolve("", accessor), Is.EqualTo("fd_env_1"));
            current = "fd_env_2";
            Assert.That(ApiKeyResolver.Resolve("", accessor), Is.EqualTo("fd_env_2"));
        }
    }
}
