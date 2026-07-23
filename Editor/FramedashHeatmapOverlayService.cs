using System;
using System.Collections.Generic;
using System.Globalization;
using Framedash.Editor.Logic;
using UnityEditor;
using UnityEngine;

namespace Framedash.Editor
{
    [InitializeOnLoad]
    internal static class FramedashHeatmapOverlayService
    {
        private static readonly FramedashHeatmapOverlay Overlay =
            new FramedashHeatmapOverlay();
        private static string _statsText = "No heatmap data loaded.";
        private static FramedashEditorHttpClient _restoreClient;
        private static int _restoreRevision;
        private static bool _restoreScheduled;
        private static bool _shuttingDown;

        static FramedashHeatmapOverlayService()
        {
            Overlay.SetEnabled(FramedashHeatmapSettings.instance.OverlayEnabled);
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
            ScheduleBackgroundRestore();
        }

        public static event Action StateChanged;

        public static bool IsEnabled => FramedashHeatmapSettings.instance.OverlayEnabled;

        public static bool HasData => Overlay.HasData;

        public static int CellCount => Overlay.CellCount;

        public static double MaxWeight => Overlay.MaxWeight;

        public static string StatsText => _statsText;

        public static void SetEnabled(bool enabled)
        {
            FramedashHeatmapSettings settings = FramedashHeatmapSettings.instance;
            if (settings.OverlayEnabled != enabled)
            {
                settings.OverlayEnabled = enabled;
                settings.Persist();
            }
            Overlay.SetEnabled(enabled);
            if (enabled && !HasData)
            {
                ScheduleBackgroundRestore();
            }
            else if (!enabled)
            {
                CancelBackgroundRestore();
            }
            NotifyStateChanged();
        }

        public static void CancelBackgroundRestore()
        {
            if (_restoreScheduled)
            {
                EditorApplication.delayCall -= RestorePersistedDataIfNeeded;
                _restoreScheduled = false;
            }
            _restoreRevision++;
            _restoreClient?.Shutdown();
            _restoreClient = null;
        }

        public static void SetData(
            FramedashEditorLogic.MapInfo map,
            List<FramedashEditorLogic.HeatmapCell> cells,
            double cellSize)
        {
            Overlay.SetData(map, cells, cellSize);
            _statsText = "Cells: " + Overlay.CellCount.ToString(CultureInfo.InvariantCulture)
                + "  |  Max weight: " + Overlay.MaxWeight.ToString("0.##", CultureInfo.InvariantCulture);
            NotifyStateChanged();
        }

        public static void ClearData()
        {
            Overlay.ClearData();
            _statsText = "No heatmap data loaded.";
            NotifyStateChanged();
        }

        public static void RefreshColors()
        {
            Overlay.RefreshColors();
            NotifyStateChanged();
        }

        public static bool FrameHeatmap(SceneView sceneView = null)
        {
            if (!Overlay.TryGetWorldBounds(out Bounds bounds))
            {
                return false;
            }

            Vector3 size = bounds.size;
            bounds.Expand(new Vector3(
                Mathf.Max(1f, size.x * 0.1f),
                Mathf.Max(1f, size.y * 0.1f),
                Mathf.Max(1f, size.z * 0.1f)));
            SceneView target = sceneView != null ? sceneView : SceneView.lastActiveSceneView;
            if (target == null)
            {
                return false;
            }
            target.Frame(bounds, false);
            target.Repaint();
            return true;
        }

        private static void NotifyStateChanged()
        {
            StateChanged?.Invoke();
        }

        private static void ScheduleBackgroundRestore()
        {
            if (_shuttingDown || _restoreScheduled)
            {
                return;
            }
            _restoreScheduled = true;
            EditorApplication.delayCall += RestorePersistedDataIfNeeded;
        }

        private static void RestorePersistedDataIfNeeded()
        {
            _restoreScheduled = false;
            if (_shuttingDown
                || HasData
                || EditorWindow.HasOpenInstances<FramedashHeatmapWindow>())
            {
                return;
            }

            FramedashHeatmapSettings settings = FramedashHeatmapSettings.instance;
            string readApiKey = FramedashEditorLogic.ResolveReadApiKey(
                settings.ReadApiKey,
                Environment.GetEnvironmentVariable("FRAMEDASH_ANALYTICS_API_KEY"));
            if (!FramedashEditorLogic.ShouldRestoreOverlayData(
                    settings.OverlayEnabled,
                    settings.ProjectId,
                    settings.SelectedMapId,
                    readApiKey))
            {
                return;
            }

            CancelBackgroundRestore();
            var client = new FramedashEditorHttpClient();
            _restoreClient = client;
            int revision = _restoreRevision;
            client.FetchMaps(settings, (success, maps, error) =>
            {
                if (!IsCurrentRestore(client, revision))
                {
                    return;
                }
                try
                {
                    if (!success)
                    {
                        CompleteBackgroundRestore(client, revision);
                        LogRestoreFailure(error ?? "Unable to load maps.");
                        return;
                    }

                    List<FramedashEditorLogic.MapInfo> loadedMaps =
                        maps ?? new List<FramedashEditorLogic.MapInfo>();
                    int selectedIndex = FramedashEditorLogic.ResolveMapSelectionIndex(
                        loadedMaps,
                        settings.SelectedMapId,
                        allowFirstMapFallback: false);
                    if (selectedIndex < 0)
                    {
                        settings.SelectedMapId = "";
                        settings.Persist();
                        ClearData();
                        CompleteBackgroundRestore(client, revision);
                        LogRestoreFailure("The previously selected map is no longer available.");
                        return;
                    }

                    FramedashEditorLogic.MapInfo selectedMap = loadedMaps[selectedIndex];
                    int cellSize = settings.CellSize;
                    client.FetchHeatmap(
                        settings,
                        selectedMap.MapId,
                        (heatmapSuccess, cells, heatmapError) =>
                        {
                            if (!IsCurrentRestore(client, revision))
                            {
                                return;
                            }
                            try
                            {
                                if (heatmapSuccess)
                                {
                                    SetData(
                                        selectedMap,
                                        cells ?? new List<FramedashEditorLogic.HeatmapCell>(),
                                        cellSize);
                                }
                                else
                                {
                                    LogRestoreFailure(
                                        heatmapError ?? "Unable to load heatmap data.");
                                }
                            }
                            finally
                            {
                                CompleteBackgroundRestore(client, revision);
                            }
                        });
                }
                catch (Exception exception)
                {
                    CompleteBackgroundRestore(client, revision);
                    Debug.LogError("[Framedash] Heatmap background restore failed: " + exception);
                }
            });
        }

        private static bool IsCurrentRestore(FramedashEditorHttpClient client, int revision)
        {
            return !_shuttingDown
                && ReferenceEquals(_restoreClient, client)
                && revision == _restoreRevision;
        }

        private static void CompleteBackgroundRestore(
            FramedashEditorHttpClient client,
            int revision)
        {
            if (!IsCurrentRestore(client, revision))
            {
                return;
            }
            _restoreClient = null;
        }

        private static void LogRestoreFailure(string message)
        {
            Debug.LogWarning("[Framedash] Unable to restore the editor heatmap: " + message);
        }

        private static void Shutdown()
        {
            if (_shuttingDown)
            {
                return;
            }
            _shuttingDown = true;
            CancelBackgroundRestore();
            Overlay.Shutdown();
        }
    }
}
