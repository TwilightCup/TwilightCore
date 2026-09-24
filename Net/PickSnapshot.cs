using System.Collections.Generic;
using TwilightCore.Timer;

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
    /// Provider tag ids mapped from the server's CT tags[]. The registered
    /// provider is the authority on which tags exist: when it implements
    /// <see cref="ITimerTagProvider"/> every tag it registered (built-in or
    /// third-party extension) is mappable, so Glitchless / No EC are no longer
    /// dropped. Providers without that interface fall back to the built-in
    /// Checkpoint / NoCheckpoint / Jumpless map. Unknown ids are dropped here
    /// and would also be ignored by the provider (T5.5).
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
    /// 黄昏杯 CT 词条 → timer tag id。优先回调已注册提供方的
    /// <see cref="ITimerTagProvider.ResolveServerTag"/>（提供方拥有权威标签集，
    /// 扩展插件注册的标签也能被服务端词条命中）；提供方未实现该扩展或未识别时，
    /// 退化为内置的 Checkpoint / NoCheckpoint / Jumpless 宽松匹配。坏值不进列表。
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

        // The timer provider is the authority on its own tag set (it may have
        // registered extension tags TwilightCore cannot know about). Ask it
        // first, then fall back to the built-in map for older providers.
        var tagProvider = TimerProviderRegistry.Current as ITimerTagProvider;
        if (tagProvider != null)
        {
            string resolved = tagProvider.ResolveServerTag(serverTag);
            if (!string.IsNullOrEmpty(resolved)) return resolved;
        }

        // Normalise: lowercase, strip spaces/hyphens/underscores — the server may
        // send "No Checkpoint", "no-checkpoint", "no_checkpoint"…
        var k = Normalize(serverTag);
        switch (k)
        {
            case "checkpoint": return "Checkpoint";
            case "nocheckpoint": return "NoCheckpoint";
            case "jumpless": return "Jumpless";
            default: return null; // Pinch / Achievement / unknown → drop
        }
    }

    /// <summary>Lowercase and drop spaces/hyphens/underscores for loose matching.</summary>
    internal static string Normalize(string tag)
        => tag == null
            ? null
            : tag.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
}
