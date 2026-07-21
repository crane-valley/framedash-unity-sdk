using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Framedash.Editor.Logic
{
    public static class FramedashEditorLogic
    {
        public static string ResolveReadApiKey(string configuredValue, string environmentValue)
        {
            string configured = (configuredValue ?? "").Trim();
            return configured.Length > 0 ? configured : (environmentValue ?? "").Trim();
        }

        public sealed class MapInfo
        {
            public string Id;
            public string Name;
            public string MapId;
            public string ImageUrl;
            public double WorldMinX;
            public double WorldMinY;
            public double WorldMaxX;
            public double WorldMaxY;
            public double? WorldMinZ;
            public double? WorldMaxZ;
            public double ImageWidth;
            public double ImageHeight;
            public string CreatedAt;
            public string UpdatedAt;
        }

        public sealed class HeatmapCell
        {
            public double X;
            public double Y;
            public double Weight;
            public double EventCount;
            public double AverageFps;
            public double AverageFrameTime;
            public double AverageMemory;
            public double? AverageGpuTime;
            public double? AverageVramMemory;
        }

        public readonly struct CellRect
        {
            public CellRect(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX;
                MinY = minY;
                MaxX = maxX;
                MaxY = maxY;
            }

            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }
        }

        public readonly struct HeatmapRgba
        {
            public HeatmapRgba(float r, float g, float b, float a)
            {
                R = r;
                G = g;
                B = b;
                A = a;
            }

            public float R { get; }
            public float G { get; }
            public float B { get; }
            public float A { get; }
        }

        public readonly struct HeatmapRenderCell
        {
            public HeatmapRenderCell(CellRect rect, double normalizedWeight)
            {
                Rect = rect;
                NormalizedWeight = normalizedWeight;
            }

            public CellRect Rect { get; }
            public double NormalizedWeight { get; }
        }

        public sealed class HeatmapGeometryData
        {
            public HeatmapGeometryData(
                double[] x,
                double[] y,
                int[] triangleIndices,
                HeatmapRgba[] colors)
            {
                X = x;
                Y = y;
                TriangleIndices = triangleIndices;
                Colors = colors;
            }

            public double[] X { get; }
            public double[] Y { get; }
            public int[] TriangleIndices { get; }
            public HeatmapRgba[] Colors { get; }
        }

        public static bool ParseMapsResponse(string json, out List<MapInfo> maps, out string error)
        {
            maps = new List<MapInfo>();
            error = string.Empty;
            if (!TryReadSuccessfulDataArray(json, out List<object> data))
            {
                error = "Malformed maps response.";
                return false;
            }

            foreach (object value in data)
            {
                if (!(value is Dictionary<string, object> entry))
                {
                    maps.Clear();
                    error = "Malformed map entry in maps response.";
                    return false;
                }

                var map = new MapInfo();
                if (!TryReadString(entry, "id", out map.Id)
                    || !TryReadString(entry, "name", out map.Name)
                    || !TryReadString(entry, "mapId", out map.MapId)
                    || !TryReadString(entry, "imageUrl", out map.ImageUrl)
                    || !TryReadNumber(entry, "worldMinX", out map.WorldMinX)
                    || !TryReadNumber(entry, "worldMinY", out map.WorldMinY)
                    || !TryReadNumber(entry, "worldMaxX", out map.WorldMaxX)
                    || !TryReadNumber(entry, "worldMaxY", out map.WorldMaxY)
                    || !TryReadOptionalNumber(entry, "worldMinZ", out map.WorldMinZ)
                    || !TryReadOptionalNumber(entry, "worldMaxZ", out map.WorldMaxZ)
                    || !TryReadNumber(entry, "imageWidth", out map.ImageWidth)
                    || !TryReadNumber(entry, "imageHeight", out map.ImageHeight)
                    || !TryReadString(entry, "createdAt", out map.CreatedAt)
                    || !TryReadString(entry, "updatedAt", out map.UpdatedAt))
                {
                    maps.Clear();
                    error = "Malformed map entry in maps response.";
                    return false;
                }
                maps.Add(map);
            }
            return true;
        }

        public static bool ParseHeatmapResponse(
            string json,
            out List<HeatmapCell> cells,
            out string error)
        {
            cells = new List<HeatmapCell>();
            error = string.Empty;
            if (!TryReadSuccessfulDataArray(json, out List<object> data))
            {
                error = "Malformed heatmap response.";
                return false;
            }

            foreach (object value in data)
            {
                if (!(value is Dictionary<string, object> entry))
                {
                    cells.Clear();
                    error = "Malformed cell entry in heatmap response.";
                    return false;
                }

                var cell = new HeatmapCell();
                if (!TryReadNumber(entry, "x", out cell.X)
                    || !TryReadNumber(entry, "y", out cell.Y)
                    || !TryReadNumberOrNumericString(entry, "weight", out cell.Weight)
                    || !TryReadNumberOrNumericString(entry, "event_count", out cell.EventCount)
                    || !TryReadNumber(entry, "avg_fps", out cell.AverageFps)
                    || !TryReadNumber(entry, "avg_frame_time", out cell.AverageFrameTime)
                    || !TryReadNumber(entry, "avg_memory", out cell.AverageMemory)
                    || !TryReadOptionalNumber(entry, "avg_gpu_time", out cell.AverageGpuTime)
                    || !TryReadOptionalNumber(entry, "avg_mem_vram", out cell.AverageVramMemory))
                {
                    cells.Clear();
                    error = "Malformed cell entry in heatmap response.";
                    return false;
                }
                cells.Add(cell);
            }
            return true;
        }

        public static string ParseProblemMessage(string json, string fallback)
        {
            if (!MiniJsonReader.TryParse(json, out object parsed)
                || !(parsed is Dictionary<string, object> root))
            {
                return fallback;
            }
            if (TryReadString(root, "detail", out string detail) && !string.IsNullOrEmpty(detail))
            {
                return detail;
            }
            if (TryReadString(root, "title", out string title) && !string.IsNullOrEmpty(title))
            {
                return title;
            }
            return fallback;
        }

        public static CellRect BuildCellRect(HeatmapCell cell, MapInfo map, double cellSize)
        {
            if (map == null || cellSize <= 0 || !IsFinite(cellSize))
            {
                return new CellRect();
            }

            double binX = Math.Floor((cell.X - map.WorldMinX) / cellSize);
            double binY = Math.Floor((cell.Y - map.WorldMinY) / cellSize);
            double minX = map.WorldMinX + binX * cellSize;
            double minY = map.WorldMinY + binY * cellSize;
            return new CellRect(
                minX,
                minY,
                Math.Min(minX + cellSize, map.WorldMaxX),
                Math.Min(minY + cellSize, map.WorldMaxY));
        }

        public static double FindMaxWeight(IReadOnlyList<HeatmapCell> cells)
        {
            if (cells == null)
            {
                return 0;
            }
            double maxWeight = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                maxWeight = Math.Max(maxWeight, cells[i].Weight);
            }
            return maxWeight;
        }

        public static double NormalizeWeight(double weight, double maxWeight)
        {
            if (!IsFinite(weight) || !IsFinite(maxWeight) || maxWeight <= 0)
            {
                return 0;
            }
            return Clamp(weight / maxWeight, 0, 1);
        }

        public static HeatmapRgba HeatmapColor(double normalizedWeight, float opacity)
        {
            float amount = (float)Clamp(normalizedWeight, 0, 1);
            float alpha = Clamp(opacity, 0, 1);
            return new HeatmapRgba(amount, 0, 1 - amount, alpha);
        }

        public static HeatmapGeometryData BuildHeatmapGeometry(
            IReadOnlyList<HeatmapRenderCell> cells,
            float opacity)
        {
            if (cells == null || cells.Count == 0)
            {
                return new HeatmapGeometryData(
                    Array.Empty<double>(),
                    Array.Empty<double>(),
                    Array.Empty<int>(),
                    Array.Empty<HeatmapRgba>());
            }

            var x = new double[cells.Count * 4];
            var y = new double[cells.Count * 4];
            var triangleIndices = new int[cells.Count * 6];
            for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
            {
                HeatmapRenderCell cell = cells[cellIndex];
                int vertexOffset = cellIndex * 4;
                x[vertexOffset] = cell.Rect.MinX;
                y[vertexOffset] = cell.Rect.MinY;
                x[vertexOffset + 1] = cell.Rect.MaxX;
                y[vertexOffset + 1] = cell.Rect.MinY;
                x[vertexOffset + 2] = cell.Rect.MaxX;
                y[vertexOffset + 2] = cell.Rect.MaxY;
                x[vertexOffset + 3] = cell.Rect.MinX;
                y[vertexOffset + 3] = cell.Rect.MaxY;

                int triangleOffset = cellIndex * 6;
                triangleIndices[triangleOffset] = vertexOffset;
                triangleIndices[triangleOffset + 1] = vertexOffset + 1;
                triangleIndices[triangleOffset + 2] = vertexOffset + 2;
                triangleIndices[triangleOffset + 3] = vertexOffset;
                triangleIndices[triangleOffset + 4] = vertexOffset + 2;
                triangleIndices[triangleOffset + 5] = vertexOffset + 3;
            }
            return new HeatmapGeometryData(
                x,
                y,
                triangleIndices,
                BuildHeatmapColors(cells, opacity));
        }

        public static HeatmapRgba[] BuildHeatmapColors(
            IReadOnlyList<HeatmapRenderCell> cells,
            float opacity)
        {
            if (cells == null)
            {
                return Array.Empty<HeatmapRgba>();
            }
            var colors = new HeatmapRgba[cells.Count * 4];
            for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
            {
                HeatmapRgba color = HeatmapColor(cells[cellIndex].NormalizedWeight, opacity);
                int vertexOffset = cellIndex * 4;
                colors[vertexOffset] = color;
                colors[vertexOffset + 1] = color;
                colors[vertexOffset + 2] = color;
                colors[vertexOffset + 3] = color;
            }
            return colors;
        }

        public static bool IsAllowedDays(int days)
        {
            return days == 1 || days == 7 || days == 14 || days == 30;
        }

        public static bool IsAllowedCellSize(int cellSize)
        {
            return cellSize == 5 || cellSize == 10 || cellSize == 25 || cellSize == 50;
        }

        private static bool TryReadSuccessfulDataArray(string json, out List<object> data)
        {
            data = null;
            if (!MiniJsonReader.TryParse(json, out object parsed)
                || !(parsed is Dictionary<string, object> root)
                || !root.TryGetValue("success", out object success)
                || !(success is bool successValue)
                || !successValue
                || !root.TryGetValue("data", out object rawData)
                || !(rawData is List<object> array))
            {
                return false;
            }
            data = array;
            return true;
        }

        private static bool TryReadString(
            Dictionary<string, object> values,
            string name,
            out string result)
        {
            result = null;
            if (!values.TryGetValue(name, out object value) || !(value is string text))
            {
                return false;
            }
            result = text;
            return true;
        }

        private static bool TryReadNumber(
            Dictionary<string, object> values,
            string name,
            out double result)
        {
            result = 0;
            if (!values.TryGetValue(name, out object value) || !(value is double number))
            {
                return false;
            }
            result = number;
            return IsFinite(result);
        }

        private static bool TryReadNumberOrNumericString(
            Dictionary<string, object> values,
            string name,
            out double result)
        {
            if (TryReadNumber(values, name, out result))
            {
                return true;
            }
            result = 0;
            if (!values.TryGetValue(name, out object value) || !(value is string text))
            {
                return false;
            }
            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result) && IsFinite(result);
        }

        private static bool TryReadOptionalNumber(
            Dictionary<string, object> values,
            string name,
            out double? result)
        {
            result = null;
            if (!values.TryGetValue(name, out object value) || value == null)
            {
                return true;
            }
            if (!(value is double number) || !IsFinite(number))
            {
                return false;
            }
            result = number;
            return true;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }

        private sealed class MiniJsonReader
        {
            private readonly string _json;
            private int _index;

            private MiniJsonReader(string json)
            {
                _json = json;
            }

            public static bool TryParse(string json, out object value)
            {
                value = null;
                if (json == null)
                {
                    return false;
                }
                try
                {
                    var reader = new MiniJsonReader(json);
                    reader.SkipWhitespace();
                    if (!reader.TryReadValue(out value))
                    {
                        return false;
                    }
                    reader.SkipWhitespace();
                    return reader._index == json.Length;
                }
                catch
                {
                    value = null;
                    return false;
                }
            }

            private bool TryReadValue(out object value)
            {
                value = null;
                if (_index >= _json.Length)
                {
                    return false;
                }
                switch (_json[_index])
                {
                    case '{':
                        return TryReadObject(out value);
                    case '[':
                        return TryReadArray(out value);
                    case '"':
                        if (TryReadString(out string text))
                        {
                            value = text;
                            return true;
                        }
                        return false;
                    case 't':
                        return TryReadLiteral("true", true, out value);
                    case 'f':
                        return TryReadLiteral("false", false, out value);
                    case 'n':
                        return TryReadLiteral("null", null, out value);
                    default:
                        return TryReadNumberValue(out value);
                }
            }

            private bool TryReadObject(out object value)
            {
                value = null;
                _index++;
                SkipWhitespace();
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                if (TryConsume('}'))
                {
                    value = result;
                    return true;
                }
                while (true)
                {
                    if (!TryReadString(out string key))
                    {
                        return false;
                    }
                    SkipWhitespace();
                    if (!TryConsume(':'))
                    {
                        return false;
                    }
                    SkipWhitespace();
                    if (!TryReadValue(out object item))
                    {
                        return false;
                    }
                    result[key] = item;
                    SkipWhitespace();
                    if (TryConsume('}'))
                    {
                        value = result;
                        return true;
                    }
                    if (!TryConsume(','))
                    {
                        return false;
                    }
                    SkipWhitespace();
                }
            }

            private bool TryReadArray(out object value)
            {
                value = null;
                _index++;
                SkipWhitespace();
                var result = new List<object>();
                if (TryConsume(']'))
                {
                    value = result;
                    return true;
                }
                while (true)
                {
                    if (!TryReadValue(out object item))
                    {
                        return false;
                    }
                    result.Add(item);
                    SkipWhitespace();
                    if (TryConsume(']'))
                    {
                        value = result;
                        return true;
                    }
                    if (!TryConsume(','))
                    {
                        return false;
                    }
                    SkipWhitespace();
                }
            }

            private bool TryReadString(out string value)
            {
                value = null;
                if (!TryConsume('"'))
                {
                    return false;
                }
                var builder = new StringBuilder();
                while (_index < _json.Length)
                {
                    char character = _json[_index++];
                    if (character == '"')
                    {
                        value = builder.ToString();
                        return true;
                    }
                    if (character < ' ')
                    {
                        return false;
                    }
                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }
                    if (_index >= _json.Length)
                    {
                        return false;
                    }
                    char escaped = _json[_index++];
                    switch (escaped)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (!TryReadUnicodeEscape(out char unicode)) return false;
                            builder.Append(unicode);
                            break;
                        default:
                            return false;
                    }
                }
                return false;
            }

            private bool TryReadUnicodeEscape(out char value)
            {
                value = (char)0;
                if (_index + 4 > _json.Length)
                {
                    return false;
                }
                int number = 0;
                for (int i = 0; i < 4; i++)
                {
                    int digit = HexValue(_json[_index++]);
                    if (digit < 0)
                    {
                        return false;
                    }
                    number = (number << 4) | digit;
                }
                value = (char)number;
                return true;
            }

            private bool TryReadNumberValue(out object value)
            {
                value = null;
                int start = _index;
                if (TryConsume('-') && _index >= _json.Length)
                {
                    return false;
                }
                if (TryConsume('0'))
                {
                    if (_index < _json.Length && char.IsDigit(_json[_index]))
                    {
                        return false;
                    }
                }
                else
                {
                    if (_index >= _json.Length || _json[_index] < '1' || _json[_index] > '9')
                    {
                        return false;
                    }
                    while (_index < _json.Length && char.IsDigit(_json[_index])) _index++;
                }
                if (TryConsume('.'))
                {
                    int fractionStart = _index;
                    while (_index < _json.Length && char.IsDigit(_json[_index])) _index++;
                    if (_index == fractionStart) return false;
                }
                if (_index < _json.Length && (_json[_index] == 'e' || _json[_index] == 'E'))
                {
                    _index++;
                    if (_index < _json.Length && (_json[_index] == '+' || _json[_index] == '-'))
                    {
                        _index++;
                    }
                    int exponentStart = _index;
                    while (_index < _json.Length && char.IsDigit(_json[_index])) _index++;
                    if (_index == exponentStart) return false;
                }
                string token = _json.Substring(start, _index - start);
                if (!double.TryParse(
                    token,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double number) || !IsFinite(number))
                {
                    return false;
                }
                value = number;
                return true;
            }

            private bool TryReadLiteral(string literal, object literalValue, out object value)
            {
                value = null;
                if (_index + literal.Length > _json.Length
                    || string.CompareOrdinal(_json, _index, literal, 0, literal.Length) != 0)
                {
                    return false;
                }
                _index += literal.Length;
                value = literalValue;
                return true;
            }

            private void SkipWhitespace()
            {
                while (_index < _json.Length)
                {
                    char character = _json[_index];
                    if (character != ' ' && character != '\t' && character != '\r' && character != '\n')
                    {
                        return;
                    }
                    _index++;
                }
            }

            private bool TryConsume(char expected)
            {
                if (_index >= _json.Length || _json[_index] != expected)
                {
                    return false;
                }
                _index++;
                return true;
            }

            private static int HexValue(char character)
            {
                if (character >= '0' && character <= '9') return character - '0';
                if (character >= 'a' && character <= 'f') return character - 'a' + 10;
                if (character >= 'A' && character <= 'F') return character - 'A' + 10;
                return -1;
            }
        }
    }
}
