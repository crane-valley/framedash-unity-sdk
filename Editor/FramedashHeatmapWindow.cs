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

        [SerializeField] private bool _overlayEnabled;

        private FramedashHeatmapSettings _settings;
        private FramedashEditorHttpClient _httpClient;
        private FramedashHeatmapOverlay _overlay;
        private List<FramedashEditorLogic.MapInfo> _maps =
            new List<FramedashEditorLogic.MapInfo>();
        private int _selectedMapIndex = -1;
        private int _queryRevision;
        private bool _busy;
        private bool _isLive;
        private bool _hasEnvironmentReadApiKey;
        private string _status = "Configure a read key and project, then refresh maps.";
        private Vector2 _scrollPosition;

        [MenuItem("Window/Framedash Heatmap")]
        private static void OpenWindow()
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
                _overlay = new FramedashHeatmapOverlay();
                _overlay.SetEnabled(_overlayEnabled);
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
                _overlay?.Shutdown();
                _overlay = null;
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
            EditorGUILayout.LabelField("Cloud Heatmap", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();
            string readApiKey = EditorGUILayout.PasswordField("Read API Key", _settings.ReadApiKey ?? "");
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
            string apiBaseUrl = EditorGUILayout.TextField("API Base URL", _settings.ApiBaseUrl ?? "");
            if (EditorGUI.EndChangeCheck())
            {
                _settings.ApiBaseUrl = apiBaseUrl;
                _settings.Persist();
                HandleConnectionSettingsChanged();
            }

            EditorGUI.BeginChangeCheck();
            string projectId = EditorGUILayout.TextField("Project ID", _settings.ProjectId ?? "");
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
                HandleQuerySettingsChanged("Days changed. Fetch heatmap data for the new selection.");
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
                HandleQuerySettingsChanged("Cell size changed. Fetch heatmap data for the new selection.");
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

            EditorGUI.BeginChangeCheck();
            bool overlayEnabled = EditorGUILayout.ToggleLeft(
                "Overlay Enabled",
                _overlayEnabled,
                GUILayout.Width(140));
            if (EditorGUI.EndChangeCheck())
            {
                _overlayEnabled = overlayEnabled;
                _overlay?.SetEnabled(_overlayEnabled);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            float opacity = EditorGUILayout.Slider("Opacity", _settings.OverlayOpacity, 0, 1);
            if (EditorGUI.EndChangeCheck())
            {
                _settings.OverlayOpacity = opacity;
                _settings.Persist();
                _overlay?.RefreshColors();
            }

            EditorGUI.BeginChangeCheck();
            float zOffset = EditorGUILayout.FloatField("Z Offset", _settings.ZOffset);
            if (EditorGUI.EndChangeCheck())
            {
                _settings.ZOffset = zOffset;
                _settings.Persist();
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(_status ?? "", MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private void DrawMapSelector()
        {
            EditorGUILayout.BeginHorizontal();
            string[] mapNames = new string[_maps.Count + 1];
            mapNames[0] = "Select a map";
            for (int i = 0; i < _maps.Count; i++)
            {
                mapNames[i + 1] = _maps[i].Name;
            }

            EditorGUI.BeginDisabledGroup(_busy);
            EditorGUI.BeginChangeCheck();
            int popupIndex = EditorGUILayout.Popup("Map", _selectedMapIndex + 1, mapNames);
            if (EditorGUI.EndChangeCheck())
            {
                _selectedMapIndex = popupIndex - 1;
                _overlay?.ClearData();
                _status = _selectedMapIndex >= 0
                    ? "Map changed. Fetch heatmap data for the new selection."
                    : "Select a map.";
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(_busy);
            if (GUILayout.Button("Refresh Maps", GUILayout.Width(110)))
            {
                RefreshMaps();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }

        private void HandleConnectionSettingsChanged()
        {
            _queryRevision++;
            _maps.Clear();
            _selectedMapIndex = -1;
            _overlay?.ClearData();
            _status = "Connection settings changed. Refresh maps for the new project.";
        }

        private void HandleQuerySettingsChanged(string status)
        {
            _queryRevision++;
            _overlay?.ClearData();
            _status = status;
        }

        private void RefreshMaps()
        {
            if (_busy || _httpClient == null)
            {
                return;
            }
            _busy = true;
            _maps.Clear();
            _selectedMapIndex = -1;
            _overlay?.ClearData();
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
                    _selectedMapIndex = _maps.Count == 0 ? -1 : 0;
                    _status = _maps.Count == 0
                        ? "No maps were returned for this project."
                        : "Loaded " + _maps.Count + " map(s). Select a map and fetch its heatmap.";
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
            _overlay?.ClearData();
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
                    _overlay?.SetData(selectedMap, loadedCells, cellSize);
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
    }
}
