namespace Framedash
{
    /// <summary>
    /// Pure camera-orientation conversions to the Framedash wire convention:
    /// yaw in [0, 360), pitch in [-90, 90] where +90 = looking up.
    /// Engine-independent so it can be unit-tested without Unity.
    /// </summary>
    public static class CameraMath
    {
        /// <summary>Normalize a yaw angle (degrees) to [0, 360).</summary>
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
        /// Convert a Unity Transform.eulerAngles.x (degrees, [0,360), where
        /// POSITIVE = pitching DOWN) to the wire pitch convention [-90, 90]
        /// where +90 = looking up. Folds to (-180,180], negates, then clamps.
        /// </summary>
        public static float PitchFromEulerX(float eulerXDegrees)
        {
            float p = eulerXDegrees % 360f;
            if (p < 0f) p += 360f;
            if (p > 180f) p -= 360f; // (-180, 180], positive = down
            p = -p;                  // wire: positive = up
            if (p > 90f) p = 90f;
            if (p < -90f) p = -90f;
            return p;
        }

        // A yaw quantum value outside the valid [0, 36000) range, used as the
        // "no camera this frame" sentinel in the high half of a packed snapshot.
        private const long AbsentYawQuantum = 0xFFFFFFFFL;

        /// <summary>The packed value meaning "no camera captured this frame".</summary>
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

        /// <summary>
        /// Unpack a value produced by <see cref="PackCamera"/>. Returns false (with
        /// yaw=pitch=0) when the snapshot is the <see cref="CameraAbsent"/> sentinel.
        /// </summary>
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
