using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class CameraMathTests
    {
        // NormalizeYaw tests

        [Test]
        public void NormalizeYaw_Zero_ReturnsZero()
        {
            Assert.That(CameraMath.NormalizeYaw(0f), Is.EqualTo(0f));
        }

        [Test]
        public void NormalizeYaw_360_ReturnsZero()
        {
            Assert.That(CameraMath.NormalizeYaw(360f), Is.EqualTo(0f));
        }

        [Test]
        public void NormalizeYaw_370_Returns10()
        {
            Assert.That(CameraMath.NormalizeYaw(370f), Is.EqualTo(10f).Within(0.001f));
        }

        [Test]
        public void NormalizeYaw_Negative10_Returns350()
        {
            Assert.That(CameraMath.NormalizeYaw(-10f), Is.EqualTo(350f).Within(0.001f));
        }

        [Test]
        public void NormalizeYaw_720_ReturnsZero()
        {
            Assert.That(CameraMath.NormalizeYaw(720f), Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void NormalizeYaw_TinyNegative_StaysBelow360()
        {
            // A tiny negative whose +360f rounds to exactly 360f must fold back into [0, 360).
            float r = CameraMath.NormalizeYaw(-1e-7f);
            Assert.That(r, Is.GreaterThanOrEqualTo(0f));
            Assert.That(r, Is.LessThan(360f));
        }

        // PitchFromEulerX tests
        // Unity euler.x convention: positive = looking DOWN (nose dips).
        // Wire convention: positive = looking UP (+90 = straight up).

        [Test]
        public void PitchFromEulerX_Zero_ReturnsZero()
        {
            // Level forward look: euler.x = 0 -> wire pitch = 0
            Assert.That(CameraMath.PitchFromEulerX(0f), Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void PitchFromEulerX_30_ReturnsMinus30()
        {
            // euler.x = 30 means looking DOWN 30 degrees -> wire pitch = -30
            Assert.That(CameraMath.PitchFromEulerX(30f), Is.EqualTo(-30f).Within(0.001f));
        }

        [Test]
        public void PitchFromEulerX_330_Returns30()
        {
            // euler.x = 330 means looking UP 30 degrees (360-330=30 up) -> wire pitch = +30
            Assert.That(CameraMath.PitchFromEulerX(330f), Is.EqualTo(30f).Within(0.001f));
        }

        [Test]
        public void PitchFromEulerX_90_ReturnsMinus90()
        {
            // euler.x = 90 means looking straight DOWN -> wire pitch = -90
            Assert.That(CameraMath.PitchFromEulerX(90f), Is.EqualTo(-90f).Within(0.001f));
        }

        [Test]
        public void PitchFromEulerX_270_Returns90()
        {
            // euler.x = 270 means looking straight UP -> wire pitch = +90
            Assert.That(CameraMath.PitchFromEulerX(270f), Is.EqualTo(90f).Within(0.001f));
        }

        [Test]
        public void PitchFromEulerX_95_ClampsToMinus90()
        {
            // euler.x = 95 (past straight down) -> folds to -95 -> clamped to -90
            Assert.That(CameraMath.PitchFromEulerX(95f), Is.EqualTo(-90f).Within(0.001f));
        }

        // Pack/unpack tests (atomic snapshot encoding)

        [TestCase(0f, 0f)]
        [TestCase(180f, -15f)]
        [TestCase(359.99f, 90f)]
        [TestCase(0f, -90f)]
        [TestCase(45.5f, 12.34f)]
        public void PackUnpack_RoundTripsWithinQuantum(float yaw, float pitch)
        {
            long packed = CameraMath.PackCamera(yaw, pitch);
            bool present = CameraMath.TryUnpackCamera(packed, out float gotYaw, out float gotPitch);

            Assert.That(present, Is.True, "a packed pair must unpack as present");
            // Quantized to 0.01 deg, so allow one quantum of error.
            Assert.That(gotYaw, Is.EqualTo(yaw).Within(0.01f));
            Assert.That(gotPitch, Is.EqualTo(pitch).Within(0.01f));
        }

        [Test]
        public void TryUnpackCamera_AbsentSentinel_ReturnsFalse()
        {
            bool present = CameraMath.TryUnpackCamera(CameraMath.CameraAbsent, out float yaw, out float pitch);

            Assert.That(present, Is.False);
            Assert.That(yaw, Is.EqualTo(0f));
            Assert.That(pitch, Is.EqualTo(0f));
        }

        [Test]
        public void PackCamera_ValidPair_NeverEqualsAbsentSentinel()
        {
            // A captured (finite, in-range) pair must never collide with the sentinel.
            Assert.That(CameraMath.PackCamera(0f, 0f), Is.Not.EqualTo(CameraMath.CameraAbsent));
            Assert.That(CameraMath.PackCamera(359.99f, 90f), Is.Not.EqualTo(CameraMath.CameraAbsent));
            Assert.That(CameraMath.PackCamera(0f, -90f), Is.Not.EqualTo(CameraMath.CameraAbsent));
        }
    }
}
