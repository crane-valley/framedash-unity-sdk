using System;
using System.Collections.Generic;
using Framedash.Editor.Logic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Framedash.Editor
{
    internal sealed class FramedashHeatmapOverlay
    {
        private readonly List<FramedashEditorLogic.HeatmapRenderCell> _renderCells =
            new List<FramedashEditorLogic.HeatmapRenderCell>();
        private Mesh _mesh;
        private Material _material;
        private bool _enabled;
        private bool _subscribed;
        private bool _shuttingDown;

        public int CellCount => _renderCells.Count;

        public double MaxWeight { get; private set; }

        public bool HasData => _renderCells.Count > 0;

        public void SetData(
            FramedashEditorLogic.MapInfo map,
            List<FramedashEditorLogic.HeatmapCell> cells,
            double cellSize)
        {
            ReleaseMesh();
            _renderCells.Clear();
            MaxWeight = 0;
            if (map == null || cells == null)
            {
                SceneView.RepaintAll();
                return;
            }
            double maxWeight = FramedashEditorLogic.FindMaxWeight(cells);
            MaxWeight = maxWeight;
            for (int i = 0; i < cells.Count; i++)
            {
                FramedashEditorLogic.HeatmapCell cell = cells[i];
                _renderCells.Add(FramedashEditorLogic.BuildHeatmapRenderCell(
                    cell,
                    map,
                    cellSize,
                    FramedashEditorLogic.NormalizeWeight(cell.Weight, maxWeight)));
            }
            if (_renderCells.Count > 0)
            {
                BuildMesh(FramedashHeatmapSettings.instance.OverlayOpacity);
            }
            SceneView.RepaintAll();
        }

        public void ClearData()
        {
            _renderCells.Clear();
            MaxWeight = 0;
            ReleaseMesh();
            SceneView.RepaintAll();
        }

        public bool TryGetWorldBounds(out Bounds bounds)
        {
            bounds = default;
            if (!FramedashEditorLogic.TryBuildHeatmapBounds(
                    _renderCells,
                    FramedashHeatmapSettings.instance.ZOffset,
                    out FramedashEditorLogic.HeatmapBoundsData data))
            {
                return false;
            }

            var minimum = new Vector3((float)data.MinX, (float)data.MinY, (float)data.MinZ);
            var maximum = new Vector3((float)data.MaxX, (float)data.MaxY, (float)data.MaxZ);
            bounds.SetMinMax(minimum, maximum);
            return true;
        }

        public void RefreshColors()
        {
            if (_mesh != null && _renderCells.Count > 0)
            {
                FramedashEditorLogic.HeatmapRgba[] rgba =
                    FramedashEditorLogic.BuildHeatmapColors(
                        _renderCells,
                        FramedashHeatmapSettings.instance.OverlayOpacity);
                _mesh.colors = ConvertColors(rgba);
            }
            SceneView.RepaintAll();
        }

        public void SetEnabled(bool enabled)
        {
            if (_shuttingDown)
            {
                return;
            }
            _enabled = enabled;
            if (_enabled && !_subscribed)
            {
                SceneView.duringSceneGui += OnSceneGui;
                _subscribed = true;
            }
            else if (!_enabled && _subscribed)
            {
                SceneView.duringSceneGui -= OnSceneGui;
                _subscribed = false;
            }
            SceneView.RepaintAll();
        }

        public void Shutdown()
        {
            if (_shuttingDown)
            {
                return;
            }
            _shuttingDown = true;
            _enabled = false;
            SceneView.duringSceneGui -= OnSceneGui;
            _subscribed = false;
            _renderCells.Clear();
            MaxWeight = 0;
            ReleaseMesh();
            ReleaseMaterial();
            SceneView.RepaintAll();
        }

        private void BuildMesh(float opacity)
        {
            FramedashEditorLogic.HeatmapGeometryData geometry =
                FramedashEditorLogic.BuildHeatmapGeometry(_renderCells, opacity);
            var vertices = new Vector3[geometry.X.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                // TelemetrySDK.Track assigns position.x/y/z directly to PositionX/Y/Z,
                // so an XZ ground-plane remap would render different coordinates than recorded.
                vertices[i] = new Vector3(
                    (float)geometry.X[i],
                    (float)geometry.Y[i],
                    (float)geometry.Z[i]);
            }

            _mesh = new Mesh
            {
                name = "Framedash Heatmap Overlay",
                hideFlags = HideFlags.HideAndDontSave,
                // UInt32 leaves headroom if the API grows beyond the UInt16 ~16k-quad ceiling.
                indexFormat = IndexFormat.UInt32
            };
            _mesh.vertices = vertices;
            _mesh.colors = ConvertColors(geometry.Colors);
            _mesh.triangles = geometry.TriangleIndices;
            _mesh.RecalculateBounds();
        }

        private bool EnsureMaterial()
        {
            if (_material != null)
            {
                return true;
            }
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                throw new InvalidOperationException("Unity's internal colored shader is unavailable.");
            }

            _material = new Material(shader)
            {
                name = "Framedash Heatmap Overlay",
                hideFlags = HideFlags.HideAndDontSave
            };
            _material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _material.SetInt("_Cull", (int)CullMode.Off);
            _material.SetInt("_ZWrite", 0);
            _material.SetColor("_Color", Color.white);
            return true;
        }

        private void OnSceneGui(SceneView view)
        {
            try
            {
                if (!_enabled || _mesh == null || EditorApplication.isPlaying)
                {
                    return;
                }

                // duringSceneGui also fires for Layout/MouseMove events where no
                // valid scene render pass is bound; immediate-mode drawing there
                // wastes work and can throw, which would disable the overlay.
                if (Event.current == null || Event.current.type != EventType.Repaint)
                {
                    return;
                }

                FramedashHeatmapSettings settings = FramedashHeatmapSettings.instance;
                if (!EnsureMaterial() || !_material.SetPass(0))
                {
                    throw new InvalidOperationException("Unity rejected the heatmap material pass.");
                }
                Matrix4x4 matrix = Matrix4x4.Translate(
                    new Vector3(0, 0, settings.ZOffset));
                Graphics.DrawMeshNow(_mesh, matrix);
            }
            catch (Exception exception)
            {
                Debug.LogError("[Framedash] Heatmap overlay drawing failed: " + exception);
                _enabled = false;
                SceneView.duringSceneGui -= OnSceneGui;
                _subscribed = false;
            }
        }

        private static Color[] ConvertColors(FramedashEditorLogic.HeatmapRgba[] rgba)
        {
            var colors = new Color[rgba.Length];
            for (int i = 0; i < rgba.Length; i++)
            {
                colors[i] = new Color(rgba[i].R, rgba[i].G, rgba[i].B, rgba[i].A);
            }
            return colors;
        }

        private void ReleaseMesh()
        {
            if (_mesh == null)
            {
                return;
            }
            UnityEngine.Object.DestroyImmediate(_mesh);
            _mesh = null;
        }

        private void ReleaseMaterial()
        {
            if (_material == null)
            {
                return;
            }
            UnityEngine.Object.DestroyImmediate(_material);
            _material = null;
        }
    }
}
