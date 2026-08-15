using System.Collections.Generic;

namespace TwilightCore.Net;

/// <summary>
/// Snapshot of the current round's pick — the subset of the server's
/// <c>Pick</c> model that the client needs. Built from the <c>round_start</c>
/// payload.
/// </summary>
internal sealed class PickSnapshot
{
    public string Code;     // e.g. ML1
    public string Name;     // display name
    public PickType Type;   // MULTI / SINGLE
    public int RetryCount;  // SINGLE only; 0 when null/absent
    public string Tag;      // e.g. "单关 + 全存档点"
    public string Category; // ML / IL / CP …

    /// <summary>
    /// Provider tag ids (e.g. Checkpoint / NoCheckpoint / Jumpless) mapped from
    /// the server's CT tags[]. Unsupported ids (Glitchless / Pinch / No EC /
    /// Achievement, and anything unknown) are dropped here — the timer provider
    /// only ever sees ids it supports (HSRTimer黄昏杯适配需求.md T5.4/T5.5).
    /// Never null; empty when the pick carries no mappable tags.
    /// </summary>
    public List<string> TimerTags = new List<string>();

    public static PickSnapshot From(Dictionary<string, object> pick)
    {
        var p = new PickSnapshot();
        if (pick == null) return p;
        p.Code = pick.GetString("code", "");
        p.Name = pick.GetString("name", "");
        p.Type = (PickType)pick.GetInt("type", (int)PickType.Multi);
        p.RetryCount = pick.GetInt("retry_count", 0);
        p.Tag = pick.GetString("tag");
        p.Category = pick.GetString("category");
        MapTimerTags(pick.GetList("tags"), p.TimerTags);
        return p;
    }

    /// <summary>
    /// 黄昏杯 CT 词条 → timer tag id。透传 HSRTimer 内置标签；其余静默丢弃
    /// （判定归裁判人工，适配需求 T6.2）。服务器 tag 字符串做了宽松匹配
    /// （大小写/空格/连字符），坏值不进列表。
    /// </summary>
    private static void MapTimerTags(List<object> serverTags, List<string> into)
    {
        if (serverTags == null) return;
        foreach (var t in serverTags)
        {
            if (t == null) continue;
            string id = MapTagId(t.ToString());
            if (id != null && !into.Contains(id)) into.Add(id);
        }
    }

    internal static string MapTagId(string serverTag)
    {
        if (string.IsNullOrEmpty(serverTag)) return null;
        // Normalise: lowercase, strip spaces/hyphens/underscores — the server may
        // send "No Checkpoint", "no-checkpoint", "no_checkpoint"…
        var k = serverTag.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
        switch (k)
        {
            case "checkpoint": return "Checkpoint";
            case "nocheckpoint": return "NoCheckpoint";
            case "jumpless": return "Jumpless";
            default: return null; // Glitchless / Pinch / NoEC / Achievement / unknown → drop
        }
    }
}
