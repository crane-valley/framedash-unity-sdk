using UnityEditor;

namespace Framedash.Editor
{
    // UserSettings is per-project, matching UE EditorPerProjectUserSettings. EditorPrefs
    // spans every project on the machine and could expose one project's analytics:read
    // key to an unrelated editor session. Unity's default project gitignore excludes
    // UserSettings, and this package creates no asset until a developer enters settings.
    [FilePath(
        "UserSettings/FramedashHeatmap.asset",
        FilePathAttribute.Location.ProjectFolder)]
    internal sealed class FramedashHeatmapSettings : ScriptableSingleton<FramedashHeatmapSettings>
    {
        public string ReadApiKey = "";
        public string ApiBaseUrl = "https://app.framedash.dev";
        public string ProjectId = "";
        public int Days = 7;
        public int CellSize = 25;
        public string EventNameFilter = "";
        public float OverlayOpacity = 0.6f;
        public float ZOffset = 0f;

        public void Persist()
        {
            Save(true);
        }
    }
}
