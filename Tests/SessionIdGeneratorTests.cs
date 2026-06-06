using System;
using System.Globalization;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class SessionIdGeneratorTests
    {
        // -- PackUuidV7 bit layout ------------------------------------

        [Test]
        public void VersionNibbleIsSeven()
        {
            var u = SessionIdGenerator.PackUuidV7(0, 0, 0);
            Assert.That(u.B & 0xF000u, Is.EqualTo(0x7000u));
        }

        [Test]
        public void VariantBitsAreOneZero()
        {
            var u = SessionIdGenerator.PackUuidV7(0, 0, 0);
            Assert.That(u.C & 0xC0000000u, Is.EqualTo(0x80000000u));
        }

        [Test]
        public void VersionAndVariantHoldForArbitraryRandomInputs()
        {
            var u = SessionIdGenerator.PackUuidV7(
                0x1234567890ABUL,
                0xFFFFFFFFFFFFFFFFUL,
                0xFFFFFFFFFFFFFFFFUL);
            Assert.That(u.B & 0xF000u, Is.EqualTo(0x7000u));
            Assert.That(u.C & 0xC0000000u, Is.EqualTo(0x80000000u));
        }

        // -- Timestamp ------------------------------------------------

        [Test]
        public void TimestampZeroProducesAllZeroPrefix()
        {
            var u = SessionIdGenerator.PackUuidV7(0, 0, 0);
            Assert.That(u.A, Is.EqualTo(0u));
            Assert.That(u.B >> 16, Is.EqualTo(0u));
        }

        [Test]
        public void TimestampRoundTrips48Bits()
        {
            const ulong ts = 0x0123456789ABUL;
            var u = SessionIdGenerator.PackUuidV7(ts, 0, 0);
            Assert.That(Reconstruct48BitTimestamp(u), Is.EqualTo(ts));
        }

        [Test]
        public void TimestampMaxValueRoundTrips()
        {
            const ulong ts = 0x0000FFFFFFFFFFFFUL;
            var u = SessionIdGenerator.PackUuidV7(ts, 0, 0);
            Assert.That(Reconstruct48BitTimestamp(u), Is.EqualTo(ts));
        }

        [Test]
        public void TimestampAbove48BitsIsTruncated()
        {
            const ulong tsRaw = 0xABCD0123456789ABUL;
            const ulong tsTruncated = tsRaw & 0x0000FFFFFFFFFFFFUL;
            var u = SessionIdGenerator.PackUuidV7(tsRaw, 0, 0);
            Assert.That(Reconstruct48BitTimestamp(u), Is.EqualTo(tsTruncated));
        }

        [Test]
        public void EarlierTimestampSortsBeforeLater()
        {
            var earlier = SessionIdGenerator.PackUuidV7(1000, 0, 0);
            var later   = SessionIdGenerator.PackUuidV7(2000, 0, 0);
            Assert.That(Reconstruct48BitTimestamp(earlier),
                Is.LessThan(Reconstruct48BitTimestamp(later)));
        }

        // -- Random material placement --------------------------------

        [Test]
        public void RandAComesFromR1Low12Bits()
        {
            // R1 = 0xABC -> rand_a = 0xABC. R2 = 0 keeps rand_b deterministic.
            var u = SessionIdGenerator.PackUuidV7(0, 0xABCUL, 0);
            Assert.That(u.B & 0x0FFFu, Is.EqualTo(0xABCu));
        }

        [Test]
        public void RandBHiComesFromR1Bits12Through25()
        {
            // 14 bits at offset 12: 0x3FFF << 12 = 0x3FFF000.
            var u = SessionIdGenerator.PackUuidV7(0, 0x3FFF000UL, 0);
            Assert.That((u.C >> 16) & 0x3FFFu, Is.EqualTo(0x3FFFu));
            Assert.That(u.B & 0x0FFFu, Is.EqualTo(0u));
        }

        [Test]
        public void RandBMdComesFromR2Bits32Through47()
        {
            // R2 = 0xCAFE_0000_0000_0000 places 0xCAFE at bits 32..47.
            var u = SessionIdGenerator.PackUuidV7(0, 0, 0xCAFE00000000UL);
            Assert.That(u.C & 0xFFFFu, Is.EqualTo(0xCAFEu));
        }

        [Test]
        public void RandBLoComesFromR2Low32Bits()
        {
            var u = SessionIdGenerator.PackUuidV7(0, 0, 0xDEADBEEFUL);
            Assert.That(u.D, Is.EqualTo(0xDEADBEEFu));
        }

        [Test]
        public void RandomFieldsIndependentOfTimestamp()
        {
            var a = SessionIdGenerator.PackUuidV7(1000, 0xABCUL, 0xDEADBEEFUL);
            var b = SessionIdGenerator.PackUuidV7(9999, 0xABCUL, 0xDEADBEEFUL);
            Assert.That(a.B & 0x0FFFu, Is.EqualTo(b.B & 0x0FFFu));
            Assert.That(a.C, Is.EqualTo(b.C));
            Assert.That(a.D, Is.EqualTo(b.D));
        }

        // -- RFC 9562 Section 6.10 example vector ---------------------

        [Test]
        public void PackUuidV7MatchesRfc9562ExampleVector()
        {
            // RFC 9562 example v7 UUID:
            //   017f22e2-79b0-7cc3-98c4-dc0c0c07398f
            // Decomposed: unix_ts_ms = 0x017F22E279B0,
            //   ver=7, rand_a=0xCC3, var=10b,
            //   rand_b high 14 bits = 0x18C4 (group 4 low 14 bits),
            //   rand_b mid 16 bits  = 0xDC0C,
            //   rand_b low 32 bits  = 0x0C07398F.
            const ulong unixTsMs = 0x017F22E279B0UL;
            ulong r1 = (0x18C4UL << 12) | 0xCC3UL;
            ulong r2 = (0xDC0CUL << 32) | 0x0C07398FUL;

            string id = SessionIdGenerator
                .PackUuidV7(unixTsMs, r1, r2)
                .ToGuid()
                .ToString("D");

            Assert.That(id, Is.EqualTo("017f22e2-79b0-7cc3-98c4-dc0c0c07398f"));
        }

        // -- ToGuid string layout -------------------------------------

        [Test]
        public void ToGuidStringMatchesRfc9562Layout()
        {
            // Hand-pick fields so each hex group is unique and easy to
            // spot in the string output. Values comply with the v7
            // version + variant invariants so this also doubles as an
            // end-to-end sanity check.
            const uint a = 0x12345678u;
            const uint b = 0xABCD7DEFu; // group2=ABCD, group3=7DEF (ver=7)
            const uint c = 0x89ABCDEFu; // group4=89AB (variant 10, since 8 = 1000), group5 first 4=CDEF
            const uint d = 0x01234567u;

            string s = new SessionIdGenerator.UuidV7Fields(a, b, c, d)
                .ToGuid().ToString("D");

            Assert.That(s, Is.EqualTo("12345678-abcd-7def-89ab-cdef01234567"));
        }

        [Test]
        public void NewSessionIdV7ProducesParseableV7Guid()
        {
            string s = SessionIdGenerator.NewSessionIdV7();
            Assert.That(Guid.TryParse(s, out _), Is.True, $"Not a parseable Guid: {s}");

            // Version nibble lives at index 14 (group3's first hex char).
            Assert.That(s[14], Is.EqualTo('7'),
                $"Expected version 7 at index 14, got: {s}");

            // Variant nibble lives at index 19 (group4's first hex char)
            // and must be one of 8/9/a/b (binary 10xx).
            Assert.That("89ab".IndexOf(s[19]), Is.GreaterThanOrEqualTo(0),
                $"Expected variant 10xx at index 19, got: {s}");
        }

        [Test]
        public void ClampNonNegativeUnixMs_NegativeReturnsZero()
        {
            // Direct unit test for the clamp helper: NewSessionIdV7 reads
            // DateTimeOffset.UtcNow inline and the harness has no way to
            // force a pre-epoch system clock, so the clamp is exercised
            // here on its testable surface instead.
            Assert.That(SessionIdGenerator.ClampNonNegativeUnixMs(-1L), Is.EqualTo(0UL));
            Assert.That(SessionIdGenerator.ClampNonNegativeUnixMs(long.MinValue), Is.EqualTo(0UL));
        }

        [Test]
        public void ClampNonNegativeUnixMs_NonNegativePassesThrough()
        {
            Assert.That(SessionIdGenerator.ClampNonNegativeUnixMs(0L), Is.EqualTo(0UL));
            Assert.That(SessionIdGenerator.ClampNonNegativeUnixMs(1L), Is.EqualTo(1UL));
            Assert.That(SessionIdGenerator.ClampNonNegativeUnixMs(0x017F22E279B0L),
                Is.EqualTo(0x017F22E279B0UL));
            Assert.That(SessionIdGenerator.ClampNonNegativeUnixMs(long.MaxValue),
                Is.EqualTo((ulong)long.MaxValue));
        }

        [Test]
        public void NewSessionIdV7TimestampIsRecent()
        {
            long beforeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string s = SessionIdGenerator.NewSessionIdV7();
            long afterMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Decode the 48-bit unix_ts_ms prefix from the canonical
            // 8-4-4-4-12 form (groups 1 + 2 are the timestamp).
            string hexTs = s.Substring(0, 8) + s.Substring(9, 4);
            long tsMs = long.Parse(hexTs, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            Assert.That(tsMs, Is.GreaterThanOrEqualTo(beforeMs));
            Assert.That(tsMs, Is.LessThanOrEqualTo(afterMs));
        }

        [Test]
        public void NewSessionIdV7IsUniquePerCall()
        {
            string a = SessionIdGenerator.NewSessionIdV7();
            string b = SessionIdGenerator.NewSessionIdV7();
            Assert.That(a, Is.Not.EqualTo(b));
        }

        // -- Xoshiro256++ ---------------------------------------------

        [Test]
        public void Xoshiro_DefaultConstructedZeroSeedYieldsZeroStream()
        {
            var rng = new Xoshiro256PlusPlus();
            Assert.That(rng.Next(), Is.EqualTo(0UL));
            Assert.That(rng.Next(), Is.EqualTo(0UL));
        }

        [Test]
        public void Xoshiro_NonZeroSeedYieldsNonZeroOutput()
        {
            var rng = new Xoshiro256PlusPlus();
            rng.Seed(1, 2, 3, 4);
            ulong first = rng.Next();
            ulong second = rng.Next();
            Assert.That(first, Is.Not.EqualTo(0UL));
            Assert.That(second, Is.Not.EqualTo(0UL));
            Assert.That(first, Is.Not.EqualTo(second));
        }

        [Test]
        public void Xoshiro_SameSeedProducesSameSequence()
        {
            var a = new Xoshiro256PlusPlus();
            a.Seed(0x9E3779B97F4A7C15UL, 0xBF58476D1CE4E5B9UL,
                0x94D049BB133111EBUL, 0x6A09E667F3BCC908UL);

            var b = new Xoshiro256PlusPlus();
            b.Seed(0x9E3779B97F4A7C15UL, 0xBF58476D1CE4E5B9UL,
                0x94D049BB133111EBUL, 0x6A09E667F3BCC908UL);

            for (int i = 0; i < 32; i++)
            {
                Assert.That(a.Next(), Is.EqualTo(b.Next()),
                    $"diverged at iteration {i}");
            }
        }

        [Test]
        public void Xoshiro_DifferentSeedsProduceDifferentSequences()
        {
            var a = new Xoshiro256PlusPlus();
            a.Seed(1, 2, 3, 4);

            var b = new Xoshiro256PlusPlus();
            b.Seed(5, 6, 7, 8);

            bool anyDifferent = false;
            for (int i = 0; i < 16; i++)
            {
                if (a.Next() != b.Next())
                {
                    anyDifferent = true;
                    break;
                }
            }
            Assert.That(anyDifferent, Is.True);
        }

        // Known-answer vector: lock the algorithm against accidental
        // rotation / XOR-cascade reorderings. Expected first value is
        // hand-derived from the canonical Blackman/Vigna reference for
        // seed (1, 2, 3, 4): result = rotl(s0 + s3, 23) + s0
        // = rotl(5, 23) + 1 = 0x2800000 + 1.
        // Locked-in lockstep with the UE5 GoogleTest counterpart at
        // sdks/ue5/Tests/UuidTests.cpp.
        [Test]
        public void Xoshiro_KnownAnswerVectorForSeed_1_2_3_4()
        {
            var rng = new Xoshiro256PlusPlus();
            rng.Seed(1, 2, 3, 4);
            Assert.That(rng.Next(), Is.EqualTo(0x0000000002800001UL));
        }

        [Test]
        public void Xoshiro_NextSafeFromCheckedCallSite()
        {
            // Documents the unchecked { } block inside Next(): even when
            // the call site sits in a `checked` context with a seed that
            // forces 64-bit wraparound on the very first add (s0 + s3),
            // Next() must not throw OverflowException. We can't recompile
            // SessionIdGenerator.cs under /checked here, but a checked
            // call site exercises the contract from the caller's side.
            var rng = new Xoshiro256PlusPlus();
            rng.Seed(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue);
            checked
            {
                Assert.DoesNotThrow(() => rng.Next());
            }
        }

        // -- Helpers --------------------------------------------------

        private static ulong Reconstruct48BitTimestamp(SessionIdGenerator.UuidV7Fields u)
        {
            return ((ulong)u.A << 16) | (ulong)(u.B >> 16);
        }
    }
}
