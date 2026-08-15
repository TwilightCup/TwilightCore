using System.Collections.Generic;

namespace TwilightCore.Net;

/// <summary>
/// JSON helpers built on the ported <see cref="MiniJSON"/>.
///
/// Outbound messages MUST be strict: the server uses pydantic v2 with
/// <c>extra="forbid"</c>, so a single unknown field yields HTTP/WS 400. Build
/// outbound messages as dictionaries holding EXACTLY the documented fields.
///
/// Inbound parsing is loose — the server includes defaulted fields (ts, kind)
/// and serialises datetimes as ISO strings, all of which we tolerate.
/// </summary>
internal static class JsonCodec
{
    public static string Serialize(object obj) => MiniJSON.Serialize(obj);

    /// <summary>Parse a JSON object into a dictionary; null if not a JSON object.</summary>
    public static Dictionary<string, object> Parse(string json)
        => MiniJSON.Deserialize(json) as Dictionary<string, object>;

    // ── Loose field readers (missing / wrong-type tolerant) ──────────

    public static string GetString(this Dictionary<string, object> d, string key, string def = null)
    {
        if (d != null && d.TryGetValue(key, out var o) && o != null)
            return o.ToString();
        return def;
    }

    public static int GetInt(this Dictionary<string, object> d, string key, int def = 0)
    {
        if (d != null && d.TryGetValue(key, out var o) && o != null)
        {
            switch (o)
            {
                case long l: return (int)l;
                case int i: return i;
                case double dd: return (int)dd;
                case float f: return (int)f;
                default:
                    int.TryParse(o.ToString(), out int v);
                    return v;
            }
        }
        return def;
    }

    public static long GetLong(this Dictionary<string, object> d, string key, long def = 0)
    {
        if (d != null && d.TryGetValue(key, out var o) && o != null)
        {
            switch (o)
            {
                case long l: return l;
                case int i: return i;
                case double dd: return (long)dd;
                default:
                    long.TryParse(o.ToString(), out long v);
                    return v;
            }
        }
        return def;
    }

    public static bool GetBool(this Dictionary<string, object> d, string key, bool def = false)
    {
        if (d != null && d.TryGetValue(key, out var o) && o is bool b) return b;
        return def;
    }

    public static List<object> GetList(this Dictionary<string, object> d, string key)
    {
        if (d != null && d.TryGetValue(key, out var o) && o is List<object> l) return l;
        return null;
    }

    public static Dictionary<string, object> GetDict(this Dictionary<string, object> d, string key)
    {
        if (d != null && d.TryGetValue(key, out var o) && o is Dictionary<string, object> dd) return dd;
        return null;
    }
}
