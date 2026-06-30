using System.Collections.Generic;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class FieldClampTests
    {
        // -- Truncate --

        [Test]
        public void Truncate_OverLimit_Truncated()
        {
            string value = new string('a', 100);
            Assert.That(FieldClamp.Truncate(value, 64), Has.Length.EqualTo(64));
        }

        [Test]
        public void Truncate_UnderLimit_Unchanged()
        {
            Assert.That(FieldClamp.Truncate("short", 64), Is.EqualTo("short"));
        }

        [Test]
        public void Truncate_AtLimit_Unchanged()
        {
            string value = new string('a', 64);
            Assert.That(FieldClamp.Truncate(value, 64), Is.EqualTo(value));
        }

        [Test]
        public void Truncate_NullOrEmpty_Passthrough()
        {
            Assert.That(FieldClamp.Truncate(null, 64), Is.Null);
            Assert.That(FieldClamp.Truncate("", 64), Is.EqualTo(""));
        }

        [Test]
        public void Truncate_NonPositiveMaxLength_ReturnsEmpty()
        {
            Assert.That(FieldClamp.Truncate("abc", 0), Is.EqualTo(""));
            Assert.That(FieldClamp.Truncate("abc", -5), Is.EqualTo(""));
        }

        [Test]
        public void Truncate_SplitSurrogatePair_DropsDanglingHighSurrogate()
        {
            // 63 ASCII chars + a 2-code-unit astral character (U+1F600), written as
            // its UTF-16 surrogate pair so this source stays ASCII. Truncating to 64
            // lands between the pair; the dangling high surrogate is dropped so the
            // result is valid UTF-16 (63 chars, no lone surrogate).
            string value = new string('a', 63) + char.ConvertFromUtf32(0x1F600);
            string result = FieldClamp.Truncate(value, 64);
            Assert.That(result.Length, Is.EqualTo(63));
            Assert.That(char.IsHighSurrogate(result[result.Length - 1]), Is.False);
        }

        // -- TruncateEventName --

        [Test]
        public void TruncateEventName_OverLimit_TruncatedTo128()
        {
            string name = new string('e', 200);
            Assert.That(FieldClamp.TruncateEventName(name), Has.Length.EqualTo(128));
        }

        [Test]
        public void TruncateEventName_UnderLimit_Unchanged()
        {
            Assert.That(FieldClamp.TruncateEventName("player_death"), Is.EqualTo("player_death"));
        }

        // -- SanitizeCoord --

        [Test]
        public void SanitizeCoord_NaN_ReturnsZero()
        {
            Assert.That(FieldClamp.SanitizeCoord(float.NaN), Is.EqualTo(0f));
        }

        [Test]
        public void SanitizeCoord_PositiveInfinity_ReturnsZero()
        {
            Assert.That(FieldClamp.SanitizeCoord(float.PositiveInfinity), Is.EqualTo(0f));
        }

        [Test]
        public void SanitizeCoord_NegativeInfinity_ReturnsZero()
        {
            Assert.That(FieldClamp.SanitizeCoord(float.NegativeInfinity), Is.EqualTo(0f));
        }

        [Test]
        public void SanitizeCoord_AtPositiveBoundary_Unchanged()
        {
            Assert.That(FieldClamp.SanitizeCoord(1e9f), Is.EqualTo(1e9f));
        }

        [Test]
        public void SanitizeCoord_AboveMax_ClampedToMax()
        {
            Assert.That(FieldClamp.SanitizeCoord(2e9f), Is.EqualTo(FieldClamp.PositionAbsMax));
        }

        [Test]
        public void SanitizeCoord_BelowMin_ClampedToNegativeMax()
        {
            Assert.That(FieldClamp.SanitizeCoord(-2e9f), Is.EqualTo(-FieldClamp.PositionAbsMax));
        }

        [Test]
        public void SanitizeCoord_NormalValue_Passthrough()
        {
            Assert.That(FieldClamp.SanitizeCoord(123.5f), Is.EqualTo(123.5f));
        }

        // -- ClampTimingMs --

        [Test]
        public void ClampTimingMs_NaN_ReturnsZero()
        {
            Assert.That(FieldClamp.ClampTimingMs(float.NaN), Is.EqualTo(0f));
        }

        [Test]
        public void ClampTimingMs_Negative_ReturnsZero()
        {
            Assert.That(FieldClamp.ClampTimingMs(-5f), Is.EqualTo(0f));
        }

        [Test]
        public void ClampTimingMs_AboveMax_CappedAt10000()
        {
            Assert.That(FieldClamp.ClampTimingMs(20000f), Is.EqualTo(10000f));
        }

        [Test]
        public void ClampTimingMs_NormalValue_Passthrough()
        {
            Assert.That(FieldClamp.ClampTimingMs(16.7f), Is.EqualTo(16.7f));
        }

        // -- FpsFromFrameTimeMs --

        [Test]
        public void FpsFromFrameTimeMs_ZeroFrame_ReturnsZero()
        {
            Assert.That(FieldClamp.FpsFromFrameTimeMs(0f), Is.EqualTo(0f));
        }

        [Test]
        public void FpsFromFrameTimeMs_SubMillisecondFrame_CappedAt1000()
        {
            // 0.1ms frame -> 10000fps uncapped; must be capped to the ingest ceiling.
            Assert.That(FieldClamp.FpsFromFrameTimeMs(0.1f), Is.EqualTo(1000f));
        }

        [Test]
        public void FpsFromFrameTimeMs_NormalFrame_ComputesFps()
        {
            // 1000/16.666... ~= 60fps.
            Assert.That(FieldClamp.FpsFromFrameTimeMs(1000f / 60f), Is.EqualTo(60f).Within(0.01f));
        }

        // -- ClampMemory --

        [Test]
        public void ClampMemory_Negative_ReturnsZero()
        {
            Assert.That(FieldClamp.ClampMemory(-1L), Is.EqualTo(0L));
        }

        [Test]
        public void ClampMemory_NonNegative_Passthrough()
        {
            Assert.That(FieldClamp.ClampMemory(123456L), Is.EqualTo(123456L));
            Assert.That(FieldClamp.ClampMemory(0L), Is.EqualTo(0L));
        }

        [Test]
        public void ClampMemory_AboveMax_ClampedTo64GiB()
        {
            long overLimit = 65L * 1024L * 1024L * 1024L;
            Assert.That(FieldClamp.ClampMemory(overLimit), Is.EqualTo(FieldClamp.MaxMemoryUsedBytes));
        }

        // -- ClampAttributes --

        [Test]
        public void ClampAttributes_Null_ReturnsNull()
        {
            Assert.That(FieldClamp.ClampAttributes(null), Is.Null);
        }

        [Test]
        public void ClampAttributes_CapsAt50()
        {
            var attrs = new Dictionary<string, string>();
            for (int i = 0; i < 60; i++) attrs["key" + i] = "value" + i;
            Assert.That(FieldClamp.ClampAttributes(attrs), Has.Count.EqualTo(50));
        }

        [Test]
        public void ClampAttributes_SkipsEmptyAndNullKey()
        {
            var attrs = new Dictionary<string, string>
            {
                { "", "empty-key" },
                { "valid", "ok" },
            };
            var result = FieldClamp.ClampAttributes(attrs);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Key, Is.EqualTo("valid"));
            Assert.That(result[0].Value, Is.EqualTo("ok"));
        }

        [Test]
        public void ClampAttributes_TruncatesKeyAndValue()
        {
            var attrs = new Dictionary<string, string>
            {
                { new string('k', 100), new string('v', 600) },
            };
            var result = FieldClamp.ClampAttributes(attrs);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Key, Has.Length.EqualTo(64));
            Assert.That(result[0].Value, Has.Length.EqualTo(512));
        }

        [Test]
        public void ClampAttributes_NullValue_BecomesEmptyString()
        {
            var attrs = new Dictionary<string, string> { { "key", null } };
            var result = FieldClamp.ClampAttributes(attrs);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Value, Is.EqualTo(""));
        }

        // -- ClampMetrics --

        [Test]
        public void ClampMetrics_Null_ReturnsNull()
        {
            Assert.That(FieldClamp.ClampMetrics(null), Is.Null);
        }

        [Test]
        public void ClampMetrics_CapsAt50()
        {
            var metrics = new Dictionary<string, float>();
            for (int i = 0; i < 60; i++) metrics["m" + i] = i;
            Assert.That(FieldClamp.ClampMetrics(metrics), Has.Count.EqualTo(50));
        }

        [Test]
        public void ClampMetrics_SkipsEmptyKey()
        {
            var metrics = new Dictionary<string, float>
            {
                { "", 1f },
                { "valid", 2f },
            };
            var result = FieldClamp.ClampMetrics(metrics);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Key, Is.EqualTo("valid"));
        }

        [Test]
        public void ClampMetrics_DropsNaNAndInfinityValues()
        {
            var metrics = new Dictionary<string, float>
            {
                { "nan", float.NaN },
                { "posinf", float.PositiveInfinity },
                { "neginf", float.NegativeInfinity },
                { "finite", 42f },
            };
            var result = FieldClamp.ClampMetrics(metrics);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Key, Is.EqualTo("finite"));
            Assert.That(result[0].Value, Is.EqualTo(42f));
        }

        [Test]
        public void ClampMetrics_TruncatesKey()
        {
            var metrics = new Dictionary<string, float> { { new string('k', 100), 1f } };
            var result = FieldClamp.ClampMetrics(metrics);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Key, Has.Length.EqualTo(64));
        }
    }
}
