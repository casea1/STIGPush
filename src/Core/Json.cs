using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StigGpoManager.Core
{
    /// <summary>
    /// Minimal JSON reader/writer with no external dependencies (.NET Framework 4.8 has no
    /// in-box JSON library suitable for config files). Objects are parsed into
    /// case-insensitive Dictionary&lt;string, object&gt;, arrays into List&lt;object&gt;,
    /// numbers into long or double, plus string, bool and null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var p = new Parser(text);
            p.SkipWs();
            var value = p.ReadValue();
            p.SkipWs();
            if (!p.AtEnd) throw p.Error("Unexpected trailing characters");
            return value;
        }

        public static string Serialize(object value, bool indent = true)
        {
            var sb = new StringBuilder();
            Write(sb, value, indent, 0);
            return sb.ToString();
        }

        // ---------- helpers for reading parsed objects ----------

        public static Dictionary<string, object> AsObject(object o) => o as Dictionary<string, object>;

        public static string GetString(Dictionary<string, object> o, string key, string fallback = null)
        {
            if (o != null && o.TryGetValue(key, out var v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return fallback;
        }

        public static bool GetBool(Dictionary<string, object> o, string key, bool fallback = false)
        {
            if (o != null && o.TryGetValue(key, out var v) && v is bool b) return b;
            return fallback;
        }

        public static long GetLong(Dictionary<string, object> o, string key, long fallback = 0)
        {
            if (o != null && o.TryGetValue(key, out var v) && v != null)
            {
                if (v is long l) return l;
                if (v is double d) return (long)d;
                if (v is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) return p;
            }
            return fallback;
        }

        public static DateTime? GetDate(Dictionary<string, object> o, string key)
        {
            var s = GetString(o, key);
            if (string.IsNullOrEmpty(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)) return d;
            return null;
        }

        public static List<object> GetArray(Dictionary<string, object> o, string key)
        {
            if (o != null && o.TryGetValue(key, out var v) && v is List<object> l) return l;
            return new List<object>();
        }

        public static List<string> GetStringArray(Dictionary<string, object> o, string key)
        {
            var result = new List<string>();
            foreach (var item in GetArray(o, key))
                if (item != null) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            return result;
        }

        public static Dictionary<string, object> NewObject() => new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // ---------- writer ----------

        private static void Write(StringBuilder sb, object value, bool indent, int level)
        {
            switch (value)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case DateTime dt: WriteString(sb, dt.ToString("o", CultureInfo.InvariantCulture)); return;
                case Guid g: WriteString(sb, g.ToString("B")); return;
                case Enum e: WriteString(sb, e.ToString()); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case IDictionary dict:
                {
                    if (dict.Count == 0) { sb.Append("{}"); return; }
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        NewLine(sb, indent, level + 1);
                        WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                        sb.Append(indent ? ": " : ":");
                        Write(sb, kv.Value, indent, level + 1);
                    }
                    NewLine(sb, indent, level);
                    sb.Append('}');
                    return;
                }
                case IEnumerable list:
                {
                    var items = new List<object>();
                    foreach (var item in list) items.Add(item);
                    if (items.Count == 0) { sb.Append("[]"); return; }
                    sb.Append('[');
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        NewLine(sb, indent, level + 1);
                        Write(sb, items[i], indent, level + 1);
                    }
                    NewLine(sb, indent, level);
                    sb.Append(']');
                    return;
                }
                default:
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
            }
        }

        private static void NewLine(StringBuilder sb, bool indent, int level)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- parser ----------

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            public Parser(string s) { _s = s; if (_s.Length > 0 && _s[0] == '﻿') _i = 1; }
            public bool AtEnd => _i >= _s.Length;

            public FormatException Error(string msg)
            {
                int line = 1, col = 1;
                for (int k = 0; k < _i && k < _s.Length; k++)
                {
                    if (_s[k] == '\n') { line++; col = 1; } else col++;
                }
                return new FormatException($"JSON error at line {line}, column {col}: {msg}");
            }

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { _i++; continue; }
                    break;
                }
            }

            public object ReadValue()
            {
                if (AtEnd) throw Error("Unexpected end of input");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Error($"Unexpected character '{c}'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error($"Expected '{word}'");
                _i += word.Length;
            }

            private Dictionary<string, object> ReadObject()
            {
                var obj = NewObject();
                _i++; // {
                SkipWs();
                if (!AtEnd && _s[_i] == '}') { _i++; return obj; }
                while (true)
                {
                    SkipWs();
                    if (AtEnd || _s[_i] != '"') throw Error("Expected property name in double quotes");
                    var key = ReadString();
                    SkipWs();
                    if (AtEnd || _s[_i] != ':') throw Error("Expected ':' after property name");
                    _i++;
                    SkipWs();
                    var val = ReadValue();
                    if (obj.ContainsKey(key)) throw Error($"Duplicate property '{key}'");
                    obj[key] = val;
                    SkipWs();
                    if (AtEnd) throw Error("Unterminated object");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return obj; }
                    throw Error("Expected ',' or '}' (check for a missing comma)");
                }
            }

            private List<object> ReadArray()
            {
                var list = new List<object>();
                _i++; // [
                SkipWs();
                if (!AtEnd && _s[_i] == ']') { _i++; return list; }
                while (true)
                {
                    SkipWs();
                    list.Add(ReadValue());
                    SkipWs();
                    if (AtEnd) throw Error("Unterminated array");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return list; }
                    throw Error("Expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                var sb = new StringBuilder();
                _i++; // opening quote
                while (true)
                {
                    if (AtEnd) throw Error("Unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (AtEnd) throw Error("Unterminated escape");
                        char e = _s[_i++];
                        switch (e)
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
                                if (_i + 4 > _s.Length) throw Error("Bad \\u escape");
                                sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                _i += 4;
                                break;
                            default:
                                throw Error($"Invalid escape '\\{e}' (in JSON, backslashes in paths must be doubled: \\\\)");
                        }
                    }
                    else
                    {
                        if (c < 0x20) throw Error("Control character in string");
                        sb.Append(c);
                    }
                }
            }

            private object ReadNumber()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                bool isFloat = false;
                while (!AtEnd)
                {
                    char c = _s[_i];
                    if (c >= '0' && c <= '9') { _i++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isFloat = true; _i++; continue; }
                    break;
                }
                var token = _s.Substring(start, _i - start);
                if (!isFloat && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                throw Error($"Invalid number '{token}'");
            }
        }
    }
}
