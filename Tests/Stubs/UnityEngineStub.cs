// Minimal UnityEngine stubs for testing engine-independent SDK classes
// outside the Unity Editor. Only the APIs actually referenced by the
// production code under test are stubbed here.

namespace UnityEngine
{
    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
    }

    public static class Mathf
    {
        public static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }
    }

    // FilePersistence.DefaultQueueFilePath references this; tests use the
    // path-injecting constructor instead, so the value only needs to compile.
    public static class Application
    {
        public static string persistentDataPath => System.IO.Path.GetTempPath();
    }
}
