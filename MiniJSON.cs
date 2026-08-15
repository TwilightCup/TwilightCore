using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace TwilightCore;

/// <summary>
/// Minimal JSON serializer/deserializer compatible with Unity 2017.4 / .NET 3.5.
/// Adapted from the widely-used MiniJSON implementation for Unity.
/// Handles nested List&lt;T&gt; correctly — unlike JsonUtility in older Unity versions.
/// </summary>
public static class MiniJSON
{
    public static string Serialize(object obj)
    {
        var sb = new StringBuilder();
        SerializeValue(obj, sb);
        return sb.ToString();
    }

    public static object Deserialize(string json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        int pos = 0;
        SkipWhitespace(json, ref pos);
        return ParseValue(json, ref pos);
    }

    // ── Serialize ─────────────────────────────────────────────────

    private static void SerializeValue(object obj, StringBuilder sb)
    {
        if (obj == null)
        {
            sb.Append("null");
        }
        else if (obj is string)
        {
            SerializeString((string)obj, sb);
        }
        else if (obj is bool)
        {
            sb.Append((bool)obj ? "true" : "false");
        }
        else if (obj is int)
        {
            sb.Append((int)obj);
        }
        else if (obj is long)
        {
            sb.Append((long)obj);
        }
        else if (obj is float)
        {
            sb.Append(((float)obj).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        else if (obj is double)
        {
            sb.Append(((double)obj).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        else if (obj is IDictionary)
        {
            SerializeDict((IDictionary)obj, sb);
        }
        else if (obj is IList)
        {
            SerializeList((IList)obj, sb);
        }
        else
        {
            // Treat as object: serialize public fields
            SerializeObject(obj, sb);
        }
    }

    private static void SerializeObject(object obj, StringBuilder sb)
    {
        sb.Append('{');
        bool first = true;
        var type = obj.GetType();
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (field.IsStatic) continue;
            if (field.IsNotSerialized) continue;
            // Skip properties with getters (Harmony/compiler-generated)
            if (field.Name.StartsWith("<")) continue;

            if (!first) sb.Append(',');
            first = false;
            SerializeString(field.Name, sb);
            sb.Append(':');
            SerializeValue(field.GetValue(obj), sb);
        }
        sb.Append('}');
    }

    private static void SerializeDict(IDictionary dict, StringBuilder sb)
    {
        sb.Append('{');
        bool first = true;
        foreach (DictionaryEntry kvp in dict)
        {
            if (!first) sb.Append(',');
            first = false;
            SerializeString(kvp.Key.ToString(), sb);
            sb.Append(':');
            SerializeValue(kvp.Value, sb);
        }
        sb.Append('}');
    }

    private static void SerializeList(IList list, StringBuilder sb)
    {
        sb.Append('[');
        bool first = true;
        foreach (var item in list)
        {
            if (!first) sb.Append(',');
            first = false;
            SerializeValue(item, sb);
        }
        sb.Append(']');
    }

    private static void SerializeString(string str, StringBuilder sb)
    {
        sb.Append('"');
        foreach (char c in str)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32)
                    {
                        sb.Append("\\u" + ((int)c).ToString("X4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    // ── Deserialize ───────────────────────────────────────────────

    private static object ParseValue(string json, ref int pos)
    {
        SkipWhitespace(json, ref pos);
        if (pos >= json.Length) return null;

        char c = json[pos];
        switch (c)
        {
            case '"': return ParseString(json, ref pos);
            case '{': return ParseDict(json, ref pos);
            case '[': return ParseList(json, ref pos);
            case 't': pos += 4; return true;
            case 'f': pos += 5; return false;
            case 'n': pos += 4; return null;
            default: return ParseNumber(json, ref pos);
        }
    }

    private static Dictionary<string, object> ParseDict(string json, ref int pos)
    {
        var dict = new Dictionary<string, object>();
        pos++; // skip '{'
        SkipWhitespace(json, ref pos);
        if (json[pos] == '}')
        {
            pos++;
            return dict;
        }
        while (true)
        {
            SkipWhitespace(json, ref pos);
            string key = ParseString(json, ref pos);
            SkipWhitespace(json, ref pos);
            pos++; // skip ':'
            object value = ParseValue(json, ref pos);
            dict[key] = value;
            SkipWhitespace(json, ref pos);
            if (json[pos] == '}')
            {
                pos++;
                return dict;
            }
            pos++; // skip ','
        }
    }

    private static List<object> ParseList(string json, ref int pos)
    {
        var list = new List<object>();
        pos++; // skip '['
        SkipWhitespace(json, ref pos);
        if (json[pos] == ']')
        {
            pos++;
            return list;
        }
        while (true)
        {
            SkipWhitespace(json, ref pos);
            object value = ParseValue(json, ref pos);
            list.Add(value);
            SkipWhitespace(json, ref pos);
            if (json[pos] == ']')
            {
                pos++;
                return list;
            }
            pos++; // skip ','
        }
    }

    private static string ParseString(string json, ref int pos)
    {
        pos++; // skip opening '"'
        var sb = new StringBuilder();
        while (pos < json.Length)
        {
            char c = json[pos];
            if (c == '"')
            {
                pos++;
                return sb.ToString();
            }
            if (c == '\\')
            {
                pos++;
                if (pos >= json.Length) break;
                switch (json[pos])
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
                        pos++;
                        if (pos + 4 > json.Length)
                        {
                            // Truncated unicode escape — append fallback
                            // and consume whatever remains so the outer
                            // pos++ doesn't overshoot.
                            sb.Append('?');
                            pos = json.Length - 1;
                        }
                        else
                        {
                            try
                            {
                                string hex = json.Substring(pos, 4);
                                sb.Append((char)Convert.ToInt32(hex, 16));
                            }
                            catch
                            {
                                // Invalid hex digits — fallback
                                sb.Append('?');
                            }
                            pos += 3;
                        }
                        break;
                }
                pos++;
            }
            else
            {
                sb.Append(c);
                pos++;
            }
        }
        return sb.ToString();
    }

    private static object ParseNumber(string json, ref int pos)
    {
        int start = pos;
        while (pos < json.Length && (char.IsDigit(json[pos]) || json[pos] == '-' || json[pos] == '.' || json[pos] == 'e' || json[pos] == 'E' || json[pos] == '+'))
        {
            pos++;
        }
        string numStr = json.Substring(start, pos - start);
        if (numStr.Contains("."))
        {
            if (double.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                return d;
        }
        else
        {
            if (long.TryParse(numStr, out long l))
                return l;
        }
        return 0;
    }

    private static void SkipWhitespace(string json, ref int pos)
    {
        while (pos < json.Length && char.IsWhiteSpace(json[pos]))
        {
            pos++;
        }
    }
}
