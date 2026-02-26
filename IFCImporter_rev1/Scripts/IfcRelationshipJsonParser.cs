using System;
using System.Collections.Generic;
using UnityEngine;

namespace IFCImporter
{
    // ════════════════════════════════════════════════════════════════
    //  Typed, spec-compliant JSON parser for _relationships.json
    //  Replaces the lightweight string-scanner with a robust
    //  recursive-descent parser that validates the expected schema.
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validation result returned by the typed JSON parser.
    /// </summary>
    public class JsonParseResult
    {
        public IfcRelationshipBundle Bundle;
        public List<string> Warnings = new List<string>();
        public List<string> Errors = new List<string>();
        public bool Success => Errors.Count == 0;
        public int TotalRelationshipsParsed;
    }

    /// <summary>
    /// Typed, spec-compliant JSON parser for the IFC relationship artefact.
    /// Uses a recursive-descent tokeniser instead of split/indexOf heuristics,
    /// providing strict schema validation, meaningful error messages, and
    /// forward compatibility with extended relationship schemas.
    /// </summary>
    public static class IfcRelationshipJsonParser
    {
        /// <summary>
        /// Parse the relationships JSON string into a validated IfcRelationshipBundle.
        /// Returns a JsonParseResult with the bundle, warnings, and errors.
        /// </summary>
        public static JsonParseResult Parse(string json)
        {
            var result = new JsonParseResult();
            result.Bundle = new IfcRelationshipBundle();

            if (string.IsNullOrEmpty(json))
            {
                result.Errors.Add("JSON input is null or empty.");
                return result;
            }

            try
            {
                var parser = new JsonTokenParser(json);
                var root = parser.ParseValue();

                if (root == null || root.Type != JsonNode.NodeType.Object)
                {
                    result.Errors.Add("Root JSON element is not an object.");
                    return result;
                }

                var rootObj = root.AsObject();

                // Parse each relationship array with schema validation
                ParseVoids(rootObj, result);
                ParseFills(rootObj, result);
                ParseContainments(rootObj, result);
                ParseSpaceBoundaries(rootObj, result);
                ParseAggregates(rootObj, result);
                ParseMeta(rootObj, result);

                result.TotalRelationshipsParsed =
                    result.Bundle.Voids.Count +
                    result.Bundle.Fills.Count +
                    result.Bundle.Containments.Count +
                    result.Bundle.SpaceBoundaries.Count +
                    result.Bundle.Aggregates.Count;
            }
            catch (Exception e)
            {
                result.Errors.Add($"JSON parse exception: {e.Message}");
            }

            return result;
        }

        // ─── Per-relationship-type parsers with field validation ───

        private static void ParseVoids(Dictionary<string, JsonNode> root, JsonParseResult result)
        {
            var array = GetArray(root, "IfcRelVoidsElement");
            if (array == null) return;

            foreach (var node in array)
            {
                if (node.Type != JsonNode.NodeType.Object)
                {
                    result.Warnings.Add("IfcRelVoidsElement: skipped non-object entry.");
                    continue;
                }
                var obj = node.AsObject();
                var data = new IfcRelVoidData
                {
                    RelatingBuildingElement = GetString(obj, "RelatingBuildingElement"),
                    RelatedOpeningElement = GetString(obj, "RelatedOpeningElement")
                };
                if (string.IsNullOrEmpty(data.RelatingBuildingElement) && string.IsNullOrEmpty(data.RelatedOpeningElement))
                {
                    result.Warnings.Add("IfcRelVoidsElement: entry with both IDs empty, skipped.");
                    continue;
                }
                result.Bundle.Voids.Add(data);
            }
        }

        private static void ParseFills(Dictionary<string, JsonNode> root, JsonParseResult result)
        {
            var array = GetArray(root, "IfcRelFillsElement");
            if (array == null) return;

            foreach (var node in array)
            {
                if (node.Type != JsonNode.NodeType.Object)
                {
                    result.Warnings.Add("IfcRelFillsElement: skipped non-object entry.");
                    continue;
                }
                var obj = node.AsObject();
                var data = new IfcRelFillData
                {
                    RelatingOpeningElement = GetString(obj, "RelatingOpeningElement"),
                    RelatedBuildingElement = GetString(obj, "RelatedBuildingElement")
                };
                if (string.IsNullOrEmpty(data.RelatingOpeningElement) && string.IsNullOrEmpty(data.RelatedBuildingElement))
                {
                    result.Warnings.Add("IfcRelFillsElement: entry with both IDs empty, skipped.");
                    continue;
                }
                result.Bundle.Fills.Add(data);
            }
        }

        private static void ParseContainments(Dictionary<string, JsonNode> root, JsonParseResult result)
        {
            var array = GetArray(root, "IfcRelContainedInSpatialStructure");
            if (array == null) return;

            foreach (var node in array)
            {
                if (node.Type != JsonNode.NodeType.Object)
                {
                    result.Warnings.Add("IfcRelContainedInSpatialStructure: skipped non-object entry.");
                    continue;
                }
                var obj = node.AsObject();
                result.Bundle.Containments.Add(new IfcRelContainmentData
                {
                    RelatingStructure = GetString(obj, "RelatingStructure"),
                    RelatedElement = GetString(obj, "RelatedElement")
                });
            }
        }

        private static void ParseSpaceBoundaries(Dictionary<string, JsonNode> root, JsonParseResult result)
        {
            var array = GetArray(root, "IfcRelSpaceBoundary");
            if (array == null) return;

            foreach (var node in array)
            {
                if (node.Type != JsonNode.NodeType.Object)
                {
                    result.Warnings.Add("IfcRelSpaceBoundary: skipped non-object entry.");
                    continue;
                }
                var obj = node.AsObject();
                result.Bundle.SpaceBoundaries.Add(new IfcRelSpaceBoundaryData
                {
                    RelatingSpace = GetString(obj, "RelatingSpace"),
                    RelatedBuildingElement = GetString(obj, "RelatedBuildingElement"),
                    PhysicalOrVirtualBoundary = GetString(obj, "PhysicalOrVirtualBoundary"),
                    InternalOrExternalBoundary = GetString(obj, "InternalOrExternalBoundary")
                });
            }
        }

        private static void ParseAggregates(Dictionary<string, JsonNode> root, JsonParseResult result)
        {
            var array = GetArray(root, "IfcRelAggregates");
            if (array == null) return;

            foreach (var node in array)
            {
                if (node.Type != JsonNode.NodeType.Object)
                {
                    result.Warnings.Add("IfcRelAggregates: skipped non-object entry.");
                    continue;
                }
                var obj = node.AsObject();
                result.Bundle.Aggregates.Add(new IfcRelAggregateData
                {
                    RelatingObject = GetString(obj, "RelatingObject"),
                    RelatedObject = GetString(obj, "RelatedObject")
                });
            }
        }

        private static void ParseMeta(Dictionary<string, JsonNode> root, JsonParseResult result)
        {
            if (!root.TryGetValue("_meta", out var metaNode) || metaNode.Type != JsonNode.NodeType.Object)
            {
                result.Warnings.Add("_meta block not found; defaulting unit scale to 1.0.");
                return;
            }

            var meta = metaNode.AsObject();

            if (meta.TryGetValue("unit_scale_to_metres", out var scaleNode))
            {
                if (scaleNode.Type == JsonNode.NodeType.Number)
                    result.Bundle.UnitScaleToMetres = (float)scaleNode.NumberValue;
                else if (scaleNode.Type == JsonNode.NodeType.String &&
                         float.TryParse(scaleNode.StringValue,
                             System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture,
                             out float parsed))
                    result.Bundle.UnitScaleToMetres = parsed;
                else
                    result.Warnings.Add("_meta.unit_scale_to_metres: unrecognised type, defaulting to 1.0.");
            }

            if (meta.TryGetValue("schema", out var schemaNode) && schemaNode.Type == JsonNode.NodeType.String)
                result.Bundle.Schema = schemaNode.StringValue;

            // Forward compatibility: log unknown keys
            foreach (var key in meta.Keys)
            {
                if (key != "unit_scale_to_metres" && key != "schema")
                    result.Warnings.Add($"_meta: unknown key '{key}' (ignored, forward compatible).");
            }
        }

        // ─── Helpers ───

        private static List<JsonNode> GetArray(Dictionary<string, JsonNode> root, string key)
        {
            if (!root.TryGetValue(key, out var node)) return null;
            if (node.Type != JsonNode.NodeType.Array) return null;
            return node.ArrayValue;
        }

        private static string GetString(Dictionary<string, JsonNode> obj, string key)
        {
            if (!obj.TryGetValue(key, out var node)) return "";
            if (node.Type == JsonNode.NodeType.String) return node.StringValue ?? "";
            if (node.Type == JsonNode.NodeType.Number) return node.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "";
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Recursive-descent JSON tokeniser
    //  Handles arbitrary whitespace, nested structures, escape
    //  sequences, and numeric types correctly.
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lightweight JSON AST node.
    /// </summary>
    public class JsonNode
    {
        public enum NodeType { Null, String, Number, Bool, Object, Array }

        public NodeType Type;
        public string StringValue;
        public double NumberValue;
        public bool BoolValue;
        public List<JsonNode> ArrayValue;
        private Dictionary<string, JsonNode> _objectValue;

        public Dictionary<string, JsonNode> AsObject()
        {
            return _objectValue ?? new Dictionary<string, JsonNode>();
        }

        public static JsonNode MakeNull() => new JsonNode { Type = NodeType.Null };
        public static JsonNode MakeString(string v) => new JsonNode { Type = NodeType.String, StringValue = v };
        public static JsonNode MakeNumber(double v) => new JsonNode { Type = NodeType.Number, NumberValue = v };
        public static JsonNode MakeBool(bool v) => new JsonNode { Type = NodeType.Bool, BoolValue = v };
        public static JsonNode MakeArray(List<JsonNode> v) => new JsonNode { Type = NodeType.Array, ArrayValue = v };
        public static JsonNode MakeObject(Dictionary<string, JsonNode> v) => new JsonNode { Type = NodeType.Object, _objectValue = v };
    }

    /// <summary>
    /// Recursive-descent JSON parser producing a JsonNode AST.
    /// </summary>
    public class JsonTokenParser
    {
        private readonly string _json;
        private int _pos;

        public JsonTokenParser(string json)
        {
            _json = json;
            _pos = 0;
        }

        public JsonNode ParseValue()
        {
            SkipWhitespace();
            if (_pos >= _json.Length)
                throw new FormatException("Unexpected end of JSON input.");

            char c = _json[_pos];
            switch (c)
            {
                case '"': return ParseString();
                case '{': return ParseObject();
                case '[': return ParseArray();
                case 't':
                case 'f': return ParseBool();
                case 'n': return ParseNull();
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return ParseNumber();
                    throw new FormatException($"Unexpected character '{c}' at position {_pos}.");
            }
        }

        private JsonNode ParseString()
        {
            return JsonNode.MakeString(ReadString());
        }

        private string ReadString()
        {
            Expect('"');
            var sb = new System.Text.StringBuilder();
            while (_pos < _json.Length)
            {
                char c = _json[_pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (_pos >= _json.Length) throw new FormatException("Unterminated string escape.");
                    char esc = _json[_pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_pos + 4 > _json.Length) throw new FormatException("Invalid unicode escape.");
                            string hex = _json.Substring(_pos, 4);
                            sb.Append((char)Convert.ToInt32(hex, 16));
                            _pos += 4;
                            break;
                        default:
                            sb.Append(esc);
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            throw new FormatException("Unterminated string.");
        }

        private JsonNode ParseNumber()
        {
            int start = _pos;
            if (_pos < _json.Length && _json[_pos] == '-') _pos++;
            while (_pos < _json.Length && _json[_pos] >= '0' && _json[_pos] <= '9') _pos++;
            if (_pos < _json.Length && _json[_pos] == '.')
            {
                _pos++;
                while (_pos < _json.Length && _json[_pos] >= '0' && _json[_pos] <= '9') _pos++;
            }
            if (_pos < _json.Length && (_json[_pos] == 'e' || _json[_pos] == 'E'))
            {
                _pos++;
                if (_pos < _json.Length && (_json[_pos] == '+' || _json[_pos] == '-')) _pos++;
                while (_pos < _json.Length && _json[_pos] >= '0' && _json[_pos] <= '9') _pos++;
            }
            string numStr = _json.Substring(start, _pos - start);
            if (double.TryParse(numStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double val))
                return JsonNode.MakeNumber(val);
            throw new FormatException($"Invalid number '{numStr}' at position {start}.");
        }

        private JsonNode ParseBool()
        {
            if (Match("true")) return JsonNode.MakeBool(true);
            if (Match("false")) return JsonNode.MakeBool(false);
            throw new FormatException($"Expected 'true' or 'false' at position {_pos}.");
        }

        private JsonNode ParseNull()
        {
            if (Match("null")) return JsonNode.MakeNull();
            throw new FormatException($"Expected 'null' at position {_pos}.");
        }

        private JsonNode ParseObject()
        {
            Expect('{');
            var dict = new Dictionary<string, JsonNode>();
            SkipWhitespace();
            if (_pos < _json.Length && _json[_pos] == '}')
            {
                _pos++;
                return JsonNode.MakeObject(dict);
            }

            while (true)
            {
                SkipWhitespace();
                string key = ReadString();
                SkipWhitespace();
                Expect(':');
                JsonNode value = ParseValue();
                dict[key] = value;
                SkipWhitespace();
                if (_pos >= _json.Length) break;
                if (_json[_pos] == ',') { _pos++; continue; }
                if (_json[_pos] == '}') { _pos++; break; }
                throw new FormatException($"Expected ',' or '}}' at position {_pos}.");
            }
            return JsonNode.MakeObject(dict);
        }

        private JsonNode ParseArray()
        {
            Expect('[');
            var list = new List<JsonNode>();
            SkipWhitespace();
            if (_pos < _json.Length && _json[_pos] == ']')
            {
                _pos++;
                return JsonNode.MakeArray(list);
            }

            while (true)
            {
                list.Add(ParseValue());
                SkipWhitespace();
                if (_pos >= _json.Length) break;
                if (_json[_pos] == ',') { _pos++; continue; }
                if (_json[_pos] == ']') { _pos++; break; }
                throw new FormatException($"Expected ',' or ']' at position {_pos}.");
            }
            return JsonNode.MakeArray(list);
        }

        private void SkipWhitespace()
        {
            while (_pos < _json.Length)
            {
                char c = _json[_pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    _pos++;
                else
                    break;
            }
        }

        private void Expect(char c)
        {
            SkipWhitespace();
            if (_pos >= _json.Length || _json[_pos] != c)
                throw new FormatException($"Expected '{c}' at position {_pos}.");
            _pos++;
        }

        private bool Match(string s)
        {
            if (_pos + s.Length <= _json.Length && _json.Substring(_pos, s.Length) == s)
            {
                _pos += s.Length;
                return true;
            }
            return false;
        }
    }
}
