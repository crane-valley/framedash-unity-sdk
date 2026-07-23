using System;
using System.Collections.Generic;
using Framedash.Editor.Logic;
using UnityEditor;
using UnityEngine;

namespace Framedash.Editor
{
    internal sealed class FramedashHeatmapWindow : EditorWindow
    {
        private static readonly int[] AllowedDays = { 1, 7, 14, 30 };
        private static readonly int[] AllowedCellSizes = { 5, 10, 25, 50 };
        // Cached label arrays keep OnGUI free of per-frame Array.ConvertAll allocations.
        private static readonly string[] AllowedDaysLabels = { "1", "7", "14", "30" };
        private static readonly string[] AllowedCellSizesLabels = { "5", "10", "25", "50" };

        private FramedashHeatmapSettings _settings;
        private FramedashEditorHttpClient _httpClient;
        private List<FramedashEditorLogic.MapInfo> _maps =
            new List<FramedashEditorLogic.MapInfo>();
        private string[] _mapNames = FramedashEditorLogic.BuildMapNames(null);
        private int _selectedMapIndex = -1;
        private int _queryRevision;
        private bool _busy;
        private bool _isLive;
        private bool _hasEnvironmentReadApiKey;
        private string _status = "Configure a read key and project, then refresh maps.";
        private Vector2 _scrollPosition;

        [MenuItem("Window/Framedash Heatmap")]
        internal static void ShowWindow()
        {
            GetWindow<FramedashHeatmapWindow>("Framedash Heatmap");
        }

        private void OnEnable()
        {
            try
            {
                _isLive = true;
                _hasEnvironmentReadApiKey = !string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("FRAMEDASH_ANALYTICS_API_KEY"));
                _settings = FramedashHeatmapSettings.instance;
                _httpClient = new FramedashEditorHttpClient();
                FramedashHeatmapOverlayService.StateChanged += OnOverlayStateChanged;
                FramedashHeatmapOverlayService.CancelBackgroundRestore();
                if (ShouldRestoreMapSelection())
                {
                    bool hasLoadedOverlay = FramedashHeatmapOverlayService.HasData;
                    RefreshMaps(
                        restoreHeatmap: !hasLoadedOverlay && _settings.OverlayEnabled,
                        preserveOverlay: hasLoadedOverlay,
                        allowFirstMapFallback: false);
                }
            }
            catch (Exception exception)
            {
                _status = "An unexpected Framedash editor error occurred.";
                Debug.LogError("[Framedash] Heatmap window initialization failed: " + exception);
            }
        }

        private void OnDisable()
        {
            try
            {
                _isLive = false;
                _queryRevision++;
                _busy = false;
                FramedashHeatmapOverlayService.StateChanged -= OnOverlayStateChanged;
                _httpClient?.Shutdown();
                _httpClient = null;
            }
            catch (Exception exception)
            {
                Debug.LogError("[Framedash] Heatmap window shutdown failed: " + exception);
            }
        }

        private void OnGUI()
        {
            try
            {
                DrawGui();
            }
            catch (Exception exception)
            {
                _status = "An unexpected Framedash editor error occurred.";
                Debug.LogError("[Framedash] Heatmap window drawing failed: " + exception);
            }
        }

        private void DrawGui()
        {
            if (_settings == null)
            {
                _settings = FramedashHeatmapSettings.instance;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Clamp(position.width * 0.34f, 86f, 132f);
            try
            {
                EditorGUILayout.LabelField("Cloud Heatmap", EditorStyles.boldLabel);
                EditorGUILayout.Space();

                EditorGUI.BeginChangeCheck();
                string readApiKey = EditorGUILayout.PasswordField(
                    "Read API Key",
                    _settings.ReadApiKey ?? "");
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.ReadApiKey = readApiKey;
                    _settings.Persist();
                    HandleConnectionSettingsChanged();
                }
                if (string.IsNullOrWhiteSpace(_settings.ReadApiKey)
                    && _hasEnvironmentReadApiKey)
                {
                    EditorGUILayout.HelpBox(
                        "Using FRAMEDASH_ANALYTICS_API_KEY from the Unity process. The value is not saved.",
                        MessageType.Info);
                }

                EditorGUI.BeginChangeCheck();
                string apiBaseUrl = EditorGUILayout.TextField(
                    "API Base URL",
                    _settings.ApiBaseUrl ?? "");
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.ApiBaseUrl = apiBaseUrl;
                    _settings.Persist();
                    HandleConnectionSettingsChanged();
                }

                EditorGUI.BeginChangeCheck();
                string projectId = EditorGUILayout.TextField(
                    "Project ID",
                    _settings.ProjectId ?? "");
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.ProjectId = projectId;
                    _settings.Persist();
                    HandleConnectionSettingsChanged();
                }

                int daysIndex = Array.IndexOf(AllowedDays, _settings.Days);
                EditorGUI.BeginChangeCheck();
                int newDaysIndex = EditorGUILayout.Popup(
                    "Days",
                    daysIndex,
                    AllowedDaysLabels);
                if (EditorGUI.EndChangeCheck() && newDaysIndex >= 0)
                {
                    _settings.Days = AllowedDays[newDaysIndex];
                    _settings.Persist();
                    HandleQuerySettingsChanged(
                        "Days changed. Fetch heatmap data for the new selection.");
                }

                int cellSizeIndex = Array.IndexOf(AllowedCellSizes, _settings.CellSize);
                EditorGUI.BeginChangeCheck();
                int newCellSizeIndex = EditorGUILayout.Popup(
                    "Cell Size",
                    cellSizeIndex,
                    AllowedCellSizesLabels);
                if (EditorGUI.EndChangeCheck() && newCellSizeIndex >= 0)
                {
                    _settings.CellSize = AllowedCellSizes[newCellSizeIndex];
                    _settings.Persist();
                    HandleQuerySettingsChanged(
                        "Cell size changed. Fetch heatmap data for the new selection.");
                }

                EditorGUI.BeginChangeCheck();
                string eventName = EditorGUILayout.TextField(
                    "Event Name Filter",
                    _settings.EventNameFilter ?? "");
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.EventNameFilter = eventName;
                    _settings.Persist();
                    HandleQuerySettingsChanged(
                        "Event name changed. Fetch heatmap data for the new selection.");
                }

                EditorGUILayout.Space();
                DrawMapSelector();

                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(_busy || !HasSelectedMap());
                if (GUILayout.Button(_busy ? "Working..." : "Fetch"))
                {
                    FetchHeatmap();
                }
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(!FramedashHeatmapOverlayService.HasData);
                if (GUILayout.Button("Frame Heatmap"))
                {
                    if (!FramedashHeatmapOverlayService.FrameHeatmap())
                    {
                        _status = "Open a Scene view before framing the heatmap.";
                    }
                }
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.EndHorizontal();

                EditorGUI.BeginChangeCheck();
                bool overlayEnabled = EditorGUILayout.ToggleLeft(
                    "Overlay Enabled",
                    FramedashHeatmapOverlayService.IsEnabled);
                if (EditorGUI.EndChangeCheck())
                {
                    FramedashHeatmapOverlayService.SetEnabled(overlayEnabled);
                }

                EditorGUI.BeginChangeCheck();
                float opacity = EditorGUILayout.Slider(
                    "Opacity",
                    _settings.OverlayOpacity,
                    0,
                    1);
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.OverlayOpacity = opacity;
                    _settings.Persist();
                    FramedashHeatmapOverlayService.RefreshColors();
                }

                EditorGUI.BeginChangeCheck();
                float zOffset = EditorGUILayout.FloatField("Z Offset", _settings.ZOffset);
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.ZOffset = zOffset;
                    _settings.Persist();
                    SceneView.RepaintAll();
                }

                DrawLegend();
                EditorGUILayout.LabelField(
                    FramedashHeatmapOverlayService.StatsText,
                    EditorStyles.miniLabel);

                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_status ?? "", MessageType.Info);
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawMapSelector()
        {
            EditorGUILayout.LabelField("Map");
            EditorGUI.BeginDisabledGroup(_busy);
            EditorGUI.BeginChangeCheck();
            int popupIndex = EditorGUILayout.Popup(_selectedMapIndex + 1, _mapNames);
            if (EditorGUI.EndChangeCheck())
            {
                _selectedMapIndex = popupIndex - 1;
                _settings.SelectedMapId = HasSelectedMap()
                    ? _maps[_selectedMapIndex].MapId
                    : "";
                _settings.Persist();
                FramedashHeatmapOverlayService.ClearData();
                _status = _selectedMapIndex >= 0
                    ? "Map changed. Fetch heatmap data for the new selection."
                    : "Select a map.";
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(_busy);
            if (GUILayout.Button("Refresh Maps"))
            {
                RefreshMaps();
            }
            EditorGUI.EndDisabledGroup();
        }

        private void HandleConnectionSettingsChanged()
        {
            _queryRevision++;
            _maps.Clear();
            _mapNames = FramedashEditorLogic.BuildMapNames(null);
            _selectedMapIndex = -1;
            _settings.SelectedMapId = "";
            _settings.Persist();
            FramedashHeatmapOverlayService.ClearData();
            _status = "Connection settings changed. Refresh maps for the new project.";
        }

        private void HandleQuerySettingsChanged(string status)
        {
            _queryRevision++;
            FramedashHeatmapOverlayService.ClearData();
            _status = status;
        }

        private void RefreshMaps(
            bool restoreHeatmap = false,
            bool preserveOverlay = false,
            bool allowFirstMapFallback = true)
        {
            if (_busy || _httpClient == null)
            {
                return;
            }
            _busy = true;
            _maps.Clear();
            _mapNames = FramedashEditorLogic.BuildMapNames(null);
            _selectedMapIndex = -1;
            string preferredMapId = _settings.SelectedMapId;
            if (!preserveOverlay)
            {
                FramedashHeatmapOverlayService.ClearData();
            }
            _status = "Loading maps...";
            int requestRevision = _queryRevision;
            _httpClient.FetchMaps(_settings, (success, maps, error) =>
            {
                if (this == null)
                {
                    return;
                }
                try
                {
                    if (!_isLive)
                    {
                        return;
                    }
                    _busy = false;
                    if (requestRevision != _queryRevision)
                    {
                        _status = "Settings changed while fetching maps. Refresh maps for the current project.";
                        Repaint();
                        return;
                    }
                    if (!success)
                    {
                        _status = error ?? "Unable to load maps.";
                        Repaint();
                        return;
                    }

                    _maps = maps ?? new List<FramedashEditorLogic.MapInfo>();
                    _mapNames = FramedashEditorLogic.BuildMapNames(_maps);
                    _selectedMapIndex = FramedashEditorLogic.ResolveMapSelectionIndex(
                        _maps,
                        _settings.SelectedMapId,
                        allowFirstMapFallback);
                    _settings.SelectedMapId = HasSelectedMap()
                        ? _maps[_selectedMapIndex].MapId
                        : "";
                    _settings.Persist();
                    if (preserveOverlay
                        && !string.Equals(
                            preferredMapId,
                            _settings.SelectedMapId,
                            StringComparison.Ordinal))
                    {
                        FramedashHeatmapOverlayService.ClearData();
                    }
                    _status = _maps.Count == 0
                        ? "No maps were returned for this project."
                        : "Loaded " + _maps.Count + " map(s). Select a map and fetch its heatmap.";
                    if (restoreHeatmap && HasSelectedMap())
                    {
                        FetchHeatmap();
                        return;
                    }
                    Repaint();
                }
                catch (Exception exception)
                {
                    _busy = false;
                    _status = "An unexpected Framedash editor error occurred.";
                    Debug.LogError("[Framedash] Maps response handling failed: " + exception);
                    Repaint();
                }
            });
        }

        private void FetchHeatmap()
        {
            if (_busy || _httpClient == null || !HasSelectedMap())
            {
                return;
            }

            _busy = true;
            _status = "Fetching cloud heatmap cells...";
            FramedashHeatmapOverlayService.ClearData();
            FramedashEditorLogic.MapInfo selectedMap = _maps[_selectedMapIndex];
            int cellSize = _settings.CellSize;
            int requestRevision = _queryRevision;
            // The API resolves the map slug before a row UUID, so sending MapId avoids
            // a UUID collision silently selecting another map's user-defined slug.
            _httpClient.FetchHeatmap(_settings, selectedMap.MapId, (success, cells, error) =>
            {
                if (this == null)
                {
                    return;
                }
                try
                {
                    if (!_isLive)
                    {
                        return;
                    }
                    _busy = false;
                    if (requestRevision != _queryRevision)
                    {
                        _status = "Query settings changed while fetching. Fetch heatmap data for the new selection.";
                        Repaint();
                        return;
                    }
                    if (!success)
                    {
                        _status = error ?? "Unable to load heatmap data.";
                        Repaint();
                        return;
                    }

                    List<FramedashEditorLogic.HeatmapCell> loadedCells =
                        cells ?? new List<FramedashEditorLogic.HeatmapCell>();
                    FramedashHeatmapOverlayService.SetData(selectedMap, loadedCells, cellSize);
                    _status = loadedCells.Count == 10000
                        ? "Loaded 10000 cells. Results may be truncated at the API limit of 10,000 cells."
                        : "Loaded " + loadedCells.Count + " heatmap cell(s).";
                    Repaint();
                }
                catch (Exception exception)
                {
                    _busy = false;
                    _status = "An unexpected Framedash editor error occurred.";
                    Debug.LogError("[Framedash] Heatmap response handling failed: " + exception);
                    Repaint();
                }
            });
        }

        private bool HasSelectedMap()
        {
            return _selectedMapIndex >= 0 && _selectedMapIndex < _maps.Count;
        }

        private bool ShouldRestoreMapSelection()
        {
            if (string.IsNullOrWhiteSpace(_settings.SelectedMapId)
                || string.IsNullOrWhiteSpace(_settings.ProjectId))
            {
                return false;
            }
            string readApiKey = FramedashEditorLogic.ResolveReadApiKey(
                _settings.ReadApiKey,
                Environment.GetEnvironmentVariable("FRAMEDASH_ANALYTICS_API_KEY"));
            return !string.IsNullOrEmpty(readApiKey);
        }

        private void OnOverlayStateChanged()
        {
            if (this != null)
            {
                Repaint();
            }
        }

        private static void DrawLegend()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Intensity", EditorStyles.miniBoldLabel);
            Rect rect = EditorGUILayout.GetControlRect(false, 14);
            float labelWidth = 28;
            Rect gradientRect = new Rect(
                rect.x + labelWidth,
                rect.y + 2,
                Mathf.Max(1, rect.width - labelWidth * 2),
                rect.height - 4);
            for (int i = 0; i < 5; i++)
            {
                FramedashEditorLogic.HeatmapRgba rgba =
                    FramedashEditorLogic.HeatmapColor(i / 4.0, 1);
                float segmentWidth = gradientRect.width / 5;
                EditorGUI.DrawRect(
                    new Rect(gradientRect.x + segmentWidth * i, gradientRect.y, segmentWidth, gradientRect.height),
                    new Color(rgba.R, rgba.G, rgba.B, 1));
            }
            GUI.Label(new Rect(rect.x, rect.y, labelWidth, rect.height), "Low", EditorStyles.miniLabel);
            GUI.Label(
                new Rect(rect.xMax - labelWidth, rect.y, labelWidth, rect.height),
                "High",
                EditorStyles.miniLabel);
        }
    }
}
