using System;
using System.Collections.Generic;
using Framedash.Editor.Logic;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class FramedashEditorLogicTests
    {
        private const string CompleteMap =
            "{\"id\":\"row-1\",\"name\":\"Arena\",\"mapId\":\"arena\","
            + "\"imageUrl\":\"https://example.com/arena.png\",\"worldMinX\":-100,"
            + "\"worldMinY\":-50,\"worldMaxX\":100,\"worldMaxY\":50,"
            + "\"worldMinZ\":2,\"worldMaxZ\":12,\"imageWidth\":2048,"
            + "\"imageHeight\":1024,\"createdAt\":\"2026-07-01T00:00:00Z\","
            + "\"updatedAt\":\"2026-07-02T00:00:00Z\"}";

        private const string CompleteCell =
            "{\"x\":12.5,\"y\":25,\"weight\":10,\"event_count\":20,"
            + "\"avg_fps\":59.5,\"avg_frame_time\":16.8,\"avg_memory\":512,"
            + "\"avg_gpu_time\":4.25,\"avg_mem_vram\":256}";

        [TestCase("configured-key", "environment-key", "configured-key")]
        [TestCase("  configured-key  ", "environment-key", "configured-key")]
        [TestCase("", "environment-key", "environment-key")]
        [TestCase("   ", "  environment-key  ", "environment-key")]
        [TestCase(null, null, "")]
        public void ResolveReadApiKey_ConfiguredValueWinsWithoutPersistingEnvironmentValue(
            string configured,
            string environment,
            string expected)
        {
            Assert.That(
                FramedashEditorLogic.ResolveReadApiKey(configured, environment),
                Is.EqualTo(expected));
        }

        [Test]
        public void ParseMapsResponse_MultipleMapsWithZBounds_ReturnsMaps()
        {
            string second = CompleteMap
                .Replace("row-1", "row-2")
                .Replace("Arena", "Cavern")
                .Replace("arena", "cavern");

            bool parsed = FramedashEditorLogic.ParseMapsResponse(
                "{\"success\":true,\"data\":[" + CompleteMap + "," + second + "]}",
                out List<FramedashEditorLogic.MapInfo> maps,
                out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(maps, Has.Count.EqualTo(2));
            Assert.That(maps[0].Id, Is.EqualTo("row-1"));
            Assert.That(maps[0].Name, Is.EqualTo("Arena"));
            Assert.That(maps[0].MapId, Is.EqualTo("arena"));
            Assert.That(maps[0].WorldMinZ, Is.EqualTo(2d));
            Assert.That(maps[0].WorldMaxZ, Is.EqualTo(12d));
            Assert.That(maps[1].Name, Is.EqualTo("Cavern"));
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void ParseMapsResponse_AbsentZBounds_ReturnsNulls()
        {
            string withoutZ = CompleteMap
                .Replace("\"worldMinZ\":2,", "")
                .Replace("\"worldMaxZ\":12,", "");

            bool parsed = FramedashEditorLogic.ParseMapsResponse(
                "{\"success\":true,\"data\":[" + withoutZ + "]}",
                out List<FramedashEditorLogic.MapInfo> maps,
                out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(maps[0].WorldMinZ, Is.Null);
            Assert.That(maps[0].WorldMaxZ, Is.Null);
        }

        [TestCase("not json")]
        [TestCase("{\"success\":false,\"data\":[]}")]
        [TestCase("{\"success\":true}")]
        public void ParseMapsResponse_MalformedEnvelope_ReturnsFalse(string json)
        {
            Assert.That(
                FramedashEditorLogic.ParseMapsResponse(json, out var maps, out string error),
                Is.False);
            Assert.That(maps, Is.Empty);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void BuildMapNames_IncludesPlaceholderAndMapNames()
        {
            var maps = new List<FramedashEditorLogic.MapInfo>
            {
                new FramedashEditorLogic.MapInfo { Name = "Arena", MapId = "arena" },
                new FramedashEditorLogic.MapInfo { Name = "Cavern", MapId = "cavern" }
            };

            Assert.That(
                FramedashEditorLogic.BuildMapNames(maps),
                Is.EqualTo(new[] { "Select a map", "Arena", "Cavern" }));
        }

        [Test]
        public void BuildMapNames_NullMaps_ReturnsPlaceholderOnly()
        {
            Assert.That(
                FramedashEditorLogic.BuildMapNames(null),
                Is.EqualTo(new[] { "Select a map" }));
        }

        [Test]
        public void FindMapIndexById_UsesStableMapIdInsteadOfListPosition()
        {
            var maps = new List<FramedashEditorLogic.MapInfo>
            {
                new FramedashEditorLogic.MapInfo { Name = "Cavern", MapId = "cavern" },
                new FramedashEditorLogic.MapInfo { Name = "Arena", MapId = "arena" }
            };

            Assert.That(FramedashEditorLogic.FindMapIndexById(maps, "arena"), Is.EqualTo(1));
            Assert.That(FramedashEditorLogic.FindMapIndexById(maps, "missing"), Is.EqualTo(-1));
            Assert.That(FramedashEditorLogic.FindMapIndexById(maps, ""), Is.EqualTo(-1));
            Assert.That(FramedashEditorLogic.FindMapIndexById(null, "arena"), Is.EqualTo(-1));
        }

        [Test]
        public void ResolveMapSelectionIndex_AutomaticRestoreDoesNotSelectUnrelatedMap()
        {
            var maps = new List<FramedashEditorLogic.MapInfo>
            {
                new FramedashEditorLogic.MapInfo { Name = "Cavern", MapId = "cavern" },
                new FramedashEditorLogic.MapInfo { Name = "Arena", MapId = "arena" }
            };

            Assert.That(
                FramedashEditorLogic.ResolveMapSelectionIndex(
                    maps,
                    "deleted-map",
                    allowFirstMapFallback: false),
                Is.EqualTo(-1));
            Assert.That(
                FramedashEditorLogic.ResolveMapSelectionIndex(
                    maps,
                    "arena",
                    allowFirstMapFallback: false),
                Is.EqualTo(1));
        }

        [Test]
        public void ResolveMapSelectionIndex_ManualRefreshCanSelectFirstMap()
        {
            var maps = new List<FramedashEditorLogic.MapInfo>
            {
                new FramedashEditorLogic.MapInfo { Name = "Cavern", MapId = "cavern" }
            };

            Assert.That(
                FramedashEditorLogic.ResolveMapSelectionIndex(
                    maps,
                    "deleted-map",
                    allowFirstMapFallback: true),
                Is.EqualTo(0));
            Assert.That(
                FramedashEditorLogic.ResolveMapSelectionIndex(
                    null,
                    "deleted-map",
                    allowFirstMapFallback: true),
                Is.EqualTo(-1));
        }

        [TestCase(true, "project-1", "arena", "read-key", true)]
        [TestCase(false, "project-1", "arena", "read-key", false)]
        [TestCase(true, "", "arena", "read-key", false)]
        [TestCase(true, "project-1", "", "read-key", false)]
        [TestCase(true, "project-1", "arena", "", false)]
        public void ShouldRestoreOverlayData_RequiresEnabledCompletePersistedSelection(
            bool overlayEnabled,
            string projectId,
            string selectedMapId,
            string readApiKey,
            bool expected)
        {
            Assert.That(
                FramedashEditorLogic.ShouldRestoreOverlayData(
                    overlayEnabled,
                    projectId,
                    selectedMapId,
                    readApiKey),
                Is.EqualTo(expected));
        }

        [Test]
        public void ParseMapsResponse_MissingRequiredMapField_ReturnsFalse()
        {
            string missingName = CompleteMap.Replace("\"name\":\"Arena\",", "");

            Assert.That(
                FramedashEditorLogic.ParseMapsResponse(
                    "{\"success\":true,\"data\":[" + missingName + "]}",
                    out var maps,
                    out string error),
                Is.False);
            Assert.That(maps, Is.Empty);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void ParseHeatmapResponse_NumericCountsAndOptionalMetrics_ReturnsCells()
        {
            bool parsed = FramedashEditorLogic.ParseHeatmapResponse(
                "{\"success\":true,\"data\":[" + CompleteCell + "]}",
                out List<FramedashEditorLogic.HeatmapCell> cells,
                out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(cells, Has.Count.EqualTo(1));
            Assert.That(cells[0].Weight, Is.EqualTo(10d));
            Assert.That(cells[0].EventCount, Is.EqualTo(20d));
            Assert.That(cells[0].AverageGpuTime, Is.EqualTo(4.25d));
            Assert.That(cells[0].AverageVramMemory, Is.EqualTo(256d));
        }

        [Test]
        public void ParseHeatmapResponse_MeasuredZ_ReturnsVoxelCenter()
        {
            string cellWithZ = CompleteCell.Replace("\"y\":25", "\"y\":25,\"z\":62.5");

            bool parsed = FramedashEditorLogic.ParseHeatmapResponse(
                "{\"success\":true,\"data\":[" + cellWithZ + "]}",
                out List<FramedashEditorLogic.HeatmapCell> cells,
                out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(cells[0].Z, Is.EqualTo(62.5d));
        }

        [Test]
        public void ParseHeatmapResponse_AbsentZ_PreservesFlatFallback()
        {
            bool parsed = FramedashEditorLogic.ParseHeatmapResponse(
                "{\"success\":true,\"data\":[" + CompleteCell + "]}",
                out List<FramedashEditorLogic.HeatmapCell> cells,
                out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(cells[0].Z, Is.Null);
        }

        [Test]
        public void ParseHeatmapResponse_NonFiniteZ_ReturnsFalse()
        {
            string invalid = CompleteCell.Replace("\"y\":25", "\"y\":25,\"z\":1e999");

            bool parsed = FramedashEditorLogic.ParseHeatmapResponse(
                "{\"success\":true,\"data\":[" + invalid + "]}",
                out List<FramedashEditorLogic.HeatmapCell> cells,
                out string error);

            Assert.That(parsed, Is.False);
            Assert.That(cells, Is.Empty);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void ParseHeatmapResponse_StringCounts_ReturnsNumericValues()
        {
            string stringCounts = CompleteCell
                .Replace("\"weight\":10", "\"weight\":\"123\"")
                .Replace("\"event_count\":20", "\"event_count\":\"456\"");

            Assert.That(
                FramedashEditorLogic.ParseHeatmapResponse(
                    "{\"success\":true,\"data\":[" + stringCounts + "]}",
                    out var cells,
                    out string error),
                Is.True,
                error);
            Assert.That(cells[0].Weight, Is.EqualTo(123d));
            Assert.That(cells[0].EventCount, Is.EqualTo(456d));
        }

        [TestCase("", null, null)]
        [TestCase("\"avg_gpu_time\":null,\"avg_mem_vram\":null,", null, null)]
        [TestCase("\"avg_gpu_time\":3.5,", 3.5d, null)]
        [TestCase("\"avg_mem_vram\":128,", null, 128d)]
        public void ParseHeatmapResponse_OptionalMetrics_AcceptsAbsentNullOrOnePresent(
            string optionalFields,
            double? expectedGpu,
            double? expectedVram)
        {
            string optionalSuffix = optionalFields.Length == 0
                ? string.Empty
                : "," + optionalFields.TrimEnd(',');
            string cell =
                "{\"x\":1,\"y\":2,\"weight\":3,\"event_count\":4,"
                + "\"avg_fps\":5,\"avg_frame_time\":6,\"avg_memory\":7"
                + optionalSuffix + "}";

            Assert.That(
                FramedashEditorLogic.ParseHeatmapResponse(
                    "{\"success\":true,\"data\":[" + cell + "]}",
                    out var cells,
                    out string error),
                Is.True,
                error);
            Assert.That(cells[0].AverageGpuTime, Is.EqualTo(expectedGpu));
            Assert.That(cells[0].AverageVramMemory, Is.EqualTo(expectedVram));
        }

        [Test]
        public void ParseHeatmapResponse_NonNumericWeightString_ReturnsFalse()
        {
            string invalid = CompleteCell.Replace("\"weight\":10", "\"weight\":\"abc\"");

            Assert.That(
                FramedashEditorLogic.ParseHeatmapResponse(
                    "{\"success\":true,\"data\":[" + invalid + "]}",
                    out var cells,
                    out string error),
                Is.False);
            Assert.That(cells, Is.Empty);
            Assert.That(error, Is.Not.Empty);
        }

        [TestCase("1e999")]
        [TestCase("NaN")]
        public void ParseHeatmapResponse_NonFiniteRequiredNumber_ReturnsFalse(string value)
        {
            string invalid = CompleteCell.Replace("\"avg_fps\":59.5", "\"avg_fps\":" + value);

            Assert.That(
                FramedashEditorLogic.ParseHeatmapResponse(
                    "{\"success\":true,\"data\":[" + invalid + "]}",
                    out var cells,
                    out string error),
                Is.False);
            Assert.That(cells, Is.Empty);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void ParseProblemMessage_DetailPresent_UsesDetail()
        {
            Assert.That(
                FramedashEditorLogic.ParseProblemMessage(
                    "{\"detail\":\"Specific failure\",\"title\":\"General failure\"}",
                    "Fallback"),
                Is.EqualTo("Specific failure"));
        }

        [Test]
        public void ParseProblemMessage_TitleOnly_UsesTitle()
        {
            Assert.That(
                FramedashEditorLogic.ParseProblemMessage("{\"title\":\"General failure\"}", "Fallback"),
                Is.EqualTo("General failure"));
        }

        [TestCase("{}")]
        [TestCase("not json")]
        public void ParseProblemMessage_NoUsableMessage_UsesFallback(string json)
        {
            Assert.That(
                FramedashEditorLogic.ParseProblemMessage(json, "Fallback"),
                Is.EqualTo("Fallback"));
        }

        [Test]
        public void BuildCellRect_InteriorCell_UsesFloorBins()
        {
            var map = new FramedashEditorLogic.MapInfo
            {
                WorldMinX = -100,
                WorldMinY = -50,
                WorldMaxX = 100,
                WorldMaxY = 100
            };
            var cell = new FramedashEditorLogic.HeatmapCell { X = -62, Y = 14 };

            FramedashEditorLogic.CellRect rect = FramedashEditorLogic.BuildCellRect(cell, map, 25);

            Assert.That(rect.MinX, Is.EqualTo(-75d));
            Assert.That(rect.MaxX, Is.EqualTo(-50d));
            Assert.That(rect.MinY, Is.EqualTo(0d));
            Assert.That(rect.MaxY, Is.EqualTo(25d));
        }

        [Test]
        public void BuildCellRect_EdgeCell_ClampsMaximumToMapBounds()
        {
            var map = new FramedashEditorLogic.MapInfo
            {
                WorldMinX = 0,
                WorldMinY = 0,
                WorldMaxX = 93,
                WorldMaxY = 88
            };
            var cell = new FramedashEditorLogic.HeatmapCell { X = 90, Y = 80 };

            FramedashEditorLogic.CellRect rect = FramedashEditorLogic.BuildCellRect(cell, map, 25);

            Assert.That(rect.MinX, Is.EqualTo(75d));
            Assert.That(rect.MaxX, Is.EqualTo(93d));
            Assert.That(rect.MinY, Is.EqualTo(75d));
            Assert.That(rect.MaxY, Is.EqualTo(88d));
        }

        [TestCase(0d)]
        [TestCase(-1d)]
        [TestCase(double.NaN)]
        public void BuildCellRect_InvalidCellSize_ReturnsZeroedRect(double cellSize)
        {
            var rect = FramedashEditorLogic.BuildCellRect(
                new FramedashEditorLogic.HeatmapCell { X = 1, Y = 2 },
                new FramedashEditorLogic.MapInfo { WorldMaxX = 10, WorldMaxY = 10 },
                cellSize);

            Assert.That(rect.MinX, Is.Zero);
            Assert.That(rect.MinY, Is.Zero);
            Assert.That(rect.MaxX, Is.Zero);
            Assert.That(rect.MaxY, Is.Zero);
        }

        [Test]
        public void FindMaxWeight_EmptyList_ReturnsZero()
        {
            Assert.That(
                FramedashEditorLogic.FindMaxWeight(new List<FramedashEditorLogic.HeatmapCell>()),
                Is.Zero);
        }

        [Test]
        public void FindMaxWeight_NormalList_ReturnsMaximum()
        {
            var cells = new List<FramedashEditorLogic.HeatmapCell>
            {
                new FramedashEditorLogic.HeatmapCell { Weight = 2 },
                new FramedashEditorLogic.HeatmapCell { Weight = 9 },
                new FramedashEditorLogic.HeatmapCell { Weight = 4 }
            };

            Assert.That(FramedashEditorLogic.FindMaxWeight(cells), Is.EqualTo(9d));
        }

        [Test]
        public void NormalizeWeight_NormalCase_ClampsRatio()
        {
            Assert.That(FramedashEditorLogic.NormalizeWeight(5, 10), Is.EqualTo(0.5d));
            Assert.That(FramedashEditorLogic.NormalizeWeight(15, 10), Is.EqualTo(1d));
        }

        [Test]
        public void NormalizeWeight_InvalidInput_ReturnsZero()
        {
            Assert.That(FramedashEditorLogic.NormalizeWeight(5, 0), Is.Zero);
            Assert.That(FramedashEditorLogic.NormalizeWeight(5, -1), Is.Zero);
            Assert.That(FramedashEditorLogic.NormalizeWeight(double.NaN, 10), Is.Zero);
        }

        [Test]
        public void HeatmapColor_UsesFiveStopPromotionalPalette()
        {
            AssertColor(FramedashEditorLogic.HeatmapColor(0, 0.6f), 0, 0.1f, 1, 0.6f);
            AssertColor(FramedashEditorLogic.HeatmapColor(0.25, 0.6f), 0, 1, 1, 0.6f);
            AssertColor(FramedashEditorLogic.HeatmapColor(0.5, 0.6f), 0, 1, 0.2f, 0.6f);
            AssertColor(FramedashEditorLogic.HeatmapColor(0.75, 0.6f), 1, 1, 0, 0.6f);
            AssertColor(FramedashEditorLogic.HeatmapColor(1, 0.6f), 1, 0.05f, 0, 0.6f);
        }

        [Test]
        public void HeatmapColor_OpacityOutOfRange_IsClamped()
        {
            FramedashEditorLogic.HeatmapRgba transparent =
                FramedashEditorLogic.HeatmapColor(0.25, -0.5f);
            FramedashEditorLogic.HeatmapRgba opaque =
                FramedashEditorLogic.HeatmapColor(0.75, 1.5f);

            Assert.That(transparent.A, Is.Zero);
            Assert.That(opaque.A, Is.EqualTo(1f));
        }

        [Test]
        public void BuildHeatmapRenderCell_MeasuredZ_BuildsVoxelAtRecordedHeight()
        {
            var map = new FramedashEditorLogic.MapInfo
            {
                WorldMinX = 0,
                WorldMinY = 0,
                WorldMaxX = 100,
                WorldMaxY = 100,
                WorldMinZ = 10
            };
            var cell = new FramedashEditorLogic.HeatmapCell
            {
                X = 12.5,
                Y = 37.5,
                Z = 62.5
            };

            FramedashEditorLogic.HeatmapRenderCell renderCell =
                FramedashEditorLogic.BuildHeatmapRenderCell(cell, map, 25, 0.75);

            Assert.That(renderCell.CenterZ, Is.EqualTo(62.5d));
            Assert.That(renderCell.VoxelHeight, Is.EqualTo(25d));
            Assert.That(renderCell.IsVolumetric, Is.True);
        }

        [Test]
        public void BuildHeatmapRenderCell_AbsentZ_StaysFlatAtMapFloor()
        {
            var map = new FramedashEditorLogic.MapInfo
            {
                WorldMinX = 0,
                WorldMinY = 0,
                WorldMaxX = 100,
                WorldMaxY = 100,
                WorldMinZ = 10
            };
            var cell = new FramedashEditorLogic.HeatmapCell { X = 12.5, Y = 37.5 };

            FramedashEditorLogic.HeatmapRenderCell renderCell =
                FramedashEditorLogic.BuildHeatmapRenderCell(cell, map, 25, 0.25);

            Assert.That(renderCell.CenterZ, Is.EqualTo(10d));
            Assert.That(renderCell.VoxelHeight, Is.Zero);
            Assert.That(renderCell.IsVolumetric, Is.False);
        }

        [Test]
        public void BuildHeatmapGeometry_MixedCells_UsesVoxelAndFlatTopology()
        {
            var renderCells = new List<FramedashEditorLogic.HeatmapRenderCell>
            {
                new FramedashEditorLogic.HeatmapRenderCell(
                    new FramedashEditorLogic.CellRect(0, 0, 10, 20),
                    5,
                    0,
                    0.25),
                new FramedashEditorLogic.HeatmapRenderCell(
                    new FramedashEditorLogic.CellRect(10, 0, 20, 20),
                    30,
                    10,
                    0.75)
            };

            FramedashEditorLogic.HeatmapGeometryData geometry =
                FramedashEditorLogic.BuildHeatmapGeometry(renderCells, 0.4f);

            Assert.That(geometry.X, Has.Length.EqualTo(12));
            Assert.That(geometry.Y, Has.Length.EqualTo(12));
            Assert.That(geometry.Z, Has.Length.EqualTo(12));
            Assert.That(geometry.TriangleIndices, Has.Length.EqualTo(42));
            Assert.That(geometry.Colors, Has.Length.EqualTo(12));

            Assert.That(geometry.Z[0], Is.EqualTo(5d));
            Assert.That(geometry.Z[3], Is.EqualTo(5d));
            Assert.That(geometry.Z[4], Is.EqualTo(25.5d));
            Assert.That(geometry.Z[11], Is.EqualTo(34.5d));

            FramedashEditorLogic.HeatmapRgba expectedColor =
                FramedashEditorLogic.HeatmapColor(0.25, 0.4f);
            for (int vertex = 0; vertex < 4; vertex++)
            {
                Assert.That(geometry.Colors[vertex].R, Is.EqualTo(expectedColor.R));
                Assert.That(geometry.Colors[vertex].G, Is.EqualTo(expectedColor.G));
                Assert.That(geometry.Colors[vertex].B, Is.EqualTo(expectedColor.B));
                Assert.That(geometry.Colors[vertex].A, Is.EqualTo(expectedColor.A));
            }

            FramedashEditorLogic.HeatmapRgba voxelColor =
                FramedashEditorLogic.HeatmapColor(0.75, 0.4f);
            Assert.That(geometry.Colors[4].R, Is.EqualTo(voxelColor.R));
            Assert.That(geometry.Colors[11].G, Is.EqualTo(voxelColor.G));
        }

        [Test]
        public void BuildHeatmapQueryPath_OptsIntoZAggregation()
        {
            string path = FramedashEditorLogic.BuildHeatmapQueryPath(
                "project id",
                "arena/west",
                25,
                7,
                "player death");

            Assert.That(
                path,
                Is.EqualTo(
                    "/api/v1/projects/project%20id/heatmap?mapId=arena%2Fwest"
                    + "&cellSize=25&days=7&includeZ=true&eventName=player%20death"));
        }

        [Test]
        public void BuildHeatmapGeometry_EmptyCells_ReturnsEmptyArrays()
        {
            FramedashEditorLogic.HeatmapGeometryData geometry =
                FramedashEditorLogic.BuildHeatmapGeometry(
                    new List<FramedashEditorLogic.HeatmapRenderCell>(),
                    0.6f);

            Assert.That(geometry.X, Is.Empty);
            Assert.That(geometry.Y, Is.Empty);
            Assert.That(geometry.Z, Is.Empty);
            Assert.That(geometry.TriangleIndices, Is.Empty);
            Assert.That(geometry.Colors, Is.Empty);
        }

        [Test]
        public void TryBuildHeatmapBounds_MixedCells_IncludesVoxelHeightAndOffset()
        {
            var renderCells = new List<FramedashEditorLogic.HeatmapRenderCell>
            {
                new FramedashEditorLogic.HeatmapRenderCell(
                    new FramedashEditorLogic.CellRect(0, 0, 10, 20),
                    5,
                    0,
                    0.25),
                new FramedashEditorLogic.HeatmapRenderCell(
                    new FramedashEditorLogic.CellRect(10, -5, 30, 15),
                    30,
                    10,
                    0.75)
            };

            Assert.That(
                FramedashEditorLogic.TryBuildHeatmapBounds(
                    renderCells,
                    2,
                    out FramedashEditorLogic.HeatmapBoundsData bounds),
                Is.True);
            Assert.That(bounds.MinX, Is.EqualTo(0));
            Assert.That(bounds.MinY, Is.EqualTo(-5));
            Assert.That(bounds.MinZ, Is.EqualTo(7));
            Assert.That(bounds.MaxX, Is.EqualTo(30));
            Assert.That(bounds.MaxY, Is.EqualTo(20));
            Assert.That(bounds.MaxZ, Is.EqualTo(37));
        }

        [Test]
        public void TryBuildHeatmapBounds_EmptyOrInvalidInput_ReturnsFalse()
        {
            Assert.That(
                FramedashEditorLogic.TryBuildHeatmapBounds(
                    new List<FramedashEditorLogic.HeatmapRenderCell>(),
                    0,
                    out _),
                Is.False);
            Assert.That(
                FramedashEditorLogic.TryBuildHeatmapBounds(null, 0, out _),
                Is.False);
            Assert.That(
                FramedashEditorLogic.TryBuildHeatmapBounds(
                    new List<FramedashEditorLogic.HeatmapRenderCell>
                    {
                        new FramedashEditorLogic.HeatmapRenderCell(
                            new FramedashEditorLogic.CellRect(0, 0, 10, 10),
                            5,
                            0,
                            0.5)
                    },
                    double.NaN,
                    out _),
                Is.False);
        }

        [Test]
        public void NullInputs_ReturnEmptyResultsInsteadOfThrowing()
        {
            var cell = new FramedashEditorLogic.HeatmapCell();
            FramedashEditorLogic.CellRect rect =
                FramedashEditorLogic.BuildCellRect(cell, null, 25);
            Assert.That(rect.MinX, Is.EqualTo(0));
            Assert.That(rect.MaxX, Is.EqualTo(0));

            Assert.That(FramedashEditorLogic.FindMaxWeight(null), Is.EqualTo(0));

            FramedashEditorLogic.HeatmapGeometryData geometry =
                FramedashEditorLogic.BuildHeatmapGeometry(null, 0.5f);
            Assert.That(geometry.X, Is.Empty);
            Assert.That(geometry.Z, Is.Empty);
            Assert.That(geometry.TriangleIndices, Is.Empty);
            Assert.That(geometry.Colors, Is.Empty);

            Assert.That(FramedashEditorLogic.BuildHeatmapColors(null, 0.5f), Is.Empty);
        }

        [Test]
        public void AllowedQueryValues_MatchApiContract()
        {
            Assert.That(FramedashEditorLogic.IsAllowedDays(1), Is.True);
            Assert.That(FramedashEditorLogic.IsAllowedDays(7), Is.True);
            Assert.That(FramedashEditorLogic.IsAllowedDays(14), Is.True);
            Assert.That(FramedashEditorLogic.IsAllowedDays(30), Is.True);
            Assert.That(FramedashEditorLogic.IsAllowedDays(2), Is.False);
            Assert.That(FramedashEditorLogic.IsAllowedCellSize(5), Is.True);
            Assert.That(FramedashEditorLogic.IsAllowedCellSize(10), Is.True);
            Assert.That(FramedashEditorLogic.IsAllowedCellSize(25), Is.True);
            Assert.That(FramedashEditorLogic.IsAllowedCellSize(50), Is.True);
            Assert.That(FramedashEditorLogic.IsAllowedCellSize(20), Is.False);
        }

        private static void AssertColor(
            FramedashEditorLogic.HeatmapRgba actual,
            float red,
            float green,
            float blue,
            float alpha)
        {
            Assert.That(actual.R, Is.EqualTo(red).Within(0.001f));
            Assert.That(actual.G, Is.EqualTo(green).Within(0.001f));
            Assert.That(actual.B, Is.EqualTo(blue).Within(0.001f));
            Assert.That(actual.A, Is.EqualTo(alpha).Within(0.001f));
        }
    }
}
