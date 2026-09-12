namespace Framedash
{
    /// <summary>
    /// Pure camera-orientation conversions to the Framedash wire convention:
    /// yaw in [0, 360), pitch in [-90, 90] where +90 = looking up.
    /// Engine-independent so it can be unit-tested without Unity.
    /// </summary>
    public static class CameraMath
    {
        public static float NormalizeYaw(float yawDegrees)
        {
            float r = yawDegrees % 360f;
            if (r < 0f) r += 360f;
            // A tiny negative r + 360f can round to exactly 360f; the wire range is
            // the half-open [0, 360), so fold 360 back to 0 (same heading).
            if (r >= 360f) r -= 360f;
            return r;
        }

        /// <summary>
        /// Unity's positive Euler X pitches down, while positive wire pitch looks up; the
        /// conversion must invert the sign.
        /// </summary>
        public static float PitchFromEulerX(float eulerXDegrees)
        {
            float p = eulerXDegrees % 360f;
            if (p < 0f) p += 360f;
            if (p > 180f) p -= 360f;
            p = -p;
            if (p > 90f) p = 90f;
            if (p < -90f) p = -90f;
            return p;
        }

        private const long AbsentYawQuantum = 0xFFFFFFFFL;

        public const long CameraAbsent = AbsentYawQuantum << 32;

        /// <summary>
        /// Pack a finite (yaw, pitch) pair into one 64-bit value so the SDK can
        /// publish and read the pair atomically (Interlocked) across threads with
        /// no lock and no allocation. Values are quantized to 0.01 deg -- far finer
        /// than the 45-deg direction bins. High 32 bits = yaw in [0, 36000),
        /// low 32 bits = (pitch + 90) in [0, 18000]. Uses only int/float ops so it
        /// compiles on every Unity runtime (no BitConverter.SingleToInt32Bits).
        /// </summary>
        public static long PackCamera(float yaw, float pitch)
        {
            long y = (long)(yaw * 100f + 0.5f);
            if (y < 0L) y = 0L;
            if (y > 35999L) y = 35999L;
            long p = (long)((pitch + 90f) * 100f + 0.5f);
            if (p < 0L) p = 0L;
            if (p > 18000L) p = 18000L;
            return (y << 32) | p;
        }

        public static bool TryUnpackCamera(long packed, out float yaw, out float pitch)
        {
            long y = (packed >> 32) & 0xFFFFFFFFL;
            if (y == AbsentYawQuantum)
            {
                yaw = 0f;
                pitch = 0f;
                return false;
            }
            long p = packed & 0xFFFFFFFFL;
            yaw = y / 100f;
            pitch = p / 100f - 90f;
            return true;
        }
    }
}
