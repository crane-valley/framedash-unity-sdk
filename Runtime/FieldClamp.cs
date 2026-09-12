#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Framedash
{
    /// <summary>
    /// Engine-independent client-side clamps for per-event fields. The ingest
    /// validator (packages/ingest-core/src/validation.ts) rejects the WHOLE batch
    /// if any single event field violates a server limit (after the server already
    /// returned 202), so one over-length attribute or one NaN coordinate would
    /// silently drop every event in that flush. These helpers clamp each field to
    /// the ingest caps (packages/ingest-core/src/config.ts) before the event is
    /// buffered. Ported verbatim from the Godot SDK so both engines behave
    /// identically. No UnityEngine references -- pure logic, NextUnit-tested.
    /// </summary>
    public static class FieldClamp
    {
        public const int MaxEventNameLength = 128;
        public const int MaxMapIdLength = 128;
        public const int MaxBuildIdLength = 128;
        public const int MaxPlatformLength = 64;
        public const int MaxEngineVersionLength = 64;
        public const int MaxAttributes = 50;
        public const int MaxMetrics = 50;
        public const int MaxAttributeKeyLength = 64;
        public const int MaxAttributeValueLength = 512;
        public const float PositionAbsMax = 1e9f;
        public const float FpsMax = 1000f;
        public const float TimingMsMax = 10000f;
        public const long MaxMemoryUsedBytes = 64L * 1024L * 1024L * 1024L;

        /// <summary>
        /// Truncate an event name to the ingest cap. Over-cap names are rejected by
        /// ingest validation (dropping the whole batch), so clamp client-side.
        /// </summary>
        public static string TruncateEventName(string eventName)
        {
            return Truncate(eventName, MaxEventNameLength);
        }

        /// <summary>
        /// Truncate a string field to <paramref name="maxLength"/> UTF-16 code units
        /// (matching the server's JS string-length semantics). If the boundary would
        /// split a surrogate pair, drop the dangling high surrogate so the result is
        /// always valid UTF-16 rather than a lone surrogate (which would serialize to
        /// a replacement char on the wire).
        /// </summary>
        // NotNullIfNotNull rather than a plain non-nullable return: a null input is
        // passed through as null (the Unity SDK's long-standing behavior, which the
        // Godot copy deliberately does not share), so only a non-null input can
        // promise a non-null result. Without this, every caller feeding a non-null
        // string would have to null-forgive a result that cannot be null.
        [return: NotNullIfNotNull("value")]
        public static string? Truncate(string? value, int maxLength)
        {
            if (maxLength <= 0) return "";
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            int len = maxLength;
            if (char.IsHighSurrogate(value[len - 1])) len--;
            return value.Substring(0, len);
        }

        /// <summary>
        /// Map a position coordinate to a safe finite value. NaN/Infinity or
        /// |coordinate| > 1e9 are rejected by ingest validation (dropping the whole
        /// batch), so one bad physics frame does not lose unrelated telemetry.
        /// </summary>
        public static float SanitizeCoord(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0f;
            if (v > PositionAbsMax) return PositionAbsMax;
            if (v < -PositionAbsMax) return -PositionAbsMax;
            return v;
        }

        public static float ClampTimingMs(float v)
        {
            if (float.IsNaN(v) || v < 0f) return 0f;
            return Math.Min(v, TimingMsMax);
        }

        /// <summary>
        /// Derive FPS from a frame time in milliseconds, clamped to the ingest FPS
        /// ceiling (1000). A sub-1ms frame (uncapped/headless) would otherwise report
        /// fps > 1000 and the batch is rejected. Returns 0 for a non-positive frame.
        /// </summary>
        public static float FpsFromFrameTimeMs(float frameTimeMs)
        {
            return frameTimeMs > 0f ? Math.Min(FpsMax, 1000f / frameTimeMs) : 0f;
        }

        /// <summary>
        /// Clamp a memory-used value to the ingest range [0, 64 GiB]. Ingest rejects
        /// a value outside this range (dropping the whole batch), so floor a negative
        /// or garbage reading at 0 and cap an oversized one at the ceiling.
        /// </summary>
        public static long ClampMemory(long v)
        {
            if (v < 0L) return 0L;
            return Math.Min(v, MaxMemoryUsedBytes);
        }

        /// <summary>
        /// A null dictionary maps to a null list -- callers rely on this "no attributes ->
        /// null" semantics. Entries with a null/empty key are skipped; the count is capped at
        /// 50. Callers holding a dictionary whose VALUES are nullable should call
        /// `ClampNullableValueAttributes` instead.
        /// </summary>
        [return: NotNullIfNotNull("attrs")]
        public static List<StringPair>? ClampAttributes(Dictionary<string, string>? attrs)
            // Null-forgiving on the argument rather than an explicit cast: the two
            // dictionary types are the SAME runtime type (value nullability is
            // annotation-only metadata), a cast reports CS8619 instead of silencing
            // anything, and the callee only READS values -- so widening the value
            // annotation across this one call cannot be violated.
            => ClampNullableValueAttributes(attrs!);

        /// <summary>
        /// <see cref="ClampAttributes"/> for callers whose dictionary VALUES are nullable
        /// (deserialized JSON, a save file, an interop boundary). Identical clamps and
        /// identical result; it is a separately named method rather than an overload
        /// because C# cannot overload on the nullability of a type argument -- the two
        /// signatures erase to the same parameter type (CS0111).
        ///
        /// Null-value semantics are unchanged from <see cref="ClampAttributes"/>: a null
        /// VALUE becomes the empty string. The wire contract
        /// (packages/proto/framedash/v1/telemetry.proto, map&lt;string, string&gt;) has no
        /// null, and keeping the key with an empty value preserves the fact that the
        /// game set that attribute, which dropping the entry would hide. A null or empty
        /// KEY still skips the entry -- an attribute with no name cannot be queried.
        /// </summary>
        [return: NotNullIfNotNull("attrs")]
        public static List<StringPair>? ClampNullableValueAttributes(Dictionary<string, string?>? attrs)
        {
            if (attrs == null) return null;
            var list = new List<StringPair>(Math.Min(attrs.Count, MaxAttributes));
            foreach (var kvp in attrs)
            {
                if (list.Count >= MaxAttributes) break;
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                list.Add(new StringPair(
                    Truncate(kvp.Key, MaxAttributeKeyLength),
                    Truncate(kvp.Value ?? "", MaxAttributeValueLength)));
            }
            return list;
        }

        [return: NotNullIfNotNull("metrics")]
        public static List<FloatPair>? ClampMetrics(Dictionary<string, float>? metrics)
        {
            if (metrics == null) return null;
            var list = new List<FloatPair>(Math.Min(metrics.Count, MaxMetrics));
            foreach (var kvp in metrics)
            {
                if (list.Count >= MaxMetrics) break;
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                // Non-finite metric values are rejected by ingest -> drop them.
                if (float.IsNaN(kvp.Value) || float.IsInfinity(kvp.Value)) continue;
                list.Add(new FloatPair(Truncate(kvp.Key, MaxAttributeKeyLength), kvp.Value));
            }
            return list;
        }
    }
}
