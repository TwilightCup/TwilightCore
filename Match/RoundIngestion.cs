using System.Collections.Generic;
using TwilightCore.Net;

namespace TwilightCore.Match;

/// <summary>
/// Ingests a server-pushed <c>round_start</c> payload into the LevelCollections
/// engine: builds a transient <see cref="CollectionDefinition"/> from the pick's
/// collection and starts a run via <see cref="CollectionManager.StartTransientCollectionRun"/>.
///
/// <para><b>Collection format</b> (opaque <c>collection.raw</c>, defined by this
/// plugin): the canonical shape is <c>{ "name": str, "levels": [LevelId, ...] }</c>
/// — i.e. <c>collection.raw.levels</c>. The resolver is deliberately tolerant and
/// also accepts <c>collection.levels</c> (no <c>raw</c> wrapper) or <c>collection.raw</c>
/// being the levels array itself, and <c>name</c> at either level (falling back to
/// the pick code). This keeps a malformed admin entry from silently breaking a match.</para>
///
/// SINGLE picks with exactly one level are expanded to <c>retry_count</c> copies
/// — i.e. the plugin "auto-arranges" the repeats (需求文档 §3.3).
/// </summary>
internal static class RoundIngestion
{
    /// <summary>
    /// The levels resolved from the last successful <see cref="TryStart"/>,
    /// PRE-SINGLE-expansion (i.e. the collection's own ids, not the
    /// retry-expanded run list). Consumed by the leaderboard snapshot
    /// (LevelIds must be index-true for MULTI collections).
    /// </summary>
    public static List<string> LastResolvedLevels { get; private set; }

    /// <summary>True when the pick is SINGLE (tolerant default MULTI, as in TryStart).</summary>
    public static bool IsSinglePick(Dictionary<string, object> pick)
        => (PickType)(pick != null ? pick.GetInt("type", (int)PickType.Multi) : (int)PickType.Multi)
           == PickType.Single;

    public static bool TryStart(
        Dictionary<string, object> pick,
        Dictionary<string, object> collection,
        out string error)
    {
        error = null;

        var mgr = CollectionManager.Instance;
        if (mgr == null || !mgr) { error = "CollectionManager not ready"; return false; }

        var levelsList = ResolveLevels(collection);
        if (levelsList == null || levelsList.Count == 0)
        {
            error = "collection has no levels (expected collection.raw.levels = [\"Aztec\", ...])";
            return false;
        }

        var levels = new List<string>(levelsList.Count);
        foreach (var l in levelsList)
            levels.Add(l == null ? "" : l.ToString());

        string name = ResolveName(collection, pick);
        LastResolvedLevels = levels;

        // SINGLE 自动编排：单关 + retry_count → 重复 retry_count 次。
        if (IsSinglePick(pick) && pick != null && pick.GetInt("retry_count", 0) > 0 && levels.Count == 1)
        {
            int retry = pick.GetInt("retry_count", 0);
            string only = levels[0];
            levels = new List<string>(retry);
            for (int i = 0; i < retry; i++) levels.Add(only);
        }

        var col = new CollectionDefinition { Name = name, Levels = levels };
        Plugin.Logger.LogInfo($"[Twilight] starting server collection '{name}' ({levels.Count} level/attempt(s)).");
        return mgr.StartTransientCollectionRun(col);
    }

    // ── Tolerant field resolution ──────────────────────────────────

    /// <summary>
    /// Find the levels array, trying (in order): collection.raw.levels,
    /// collection.levels, collection.raw (if it's itself an array of strings).
    /// Internal: the preload manager resolves the first level of an announced
    /// pick through the same tolerant path.
    /// </summary>
    internal static List<object> ResolveLevels(Dictionary<string, object> collection)
    {
        if (collection == null) return null;

        var raw = collection.GetDict("raw");
        if (raw != null)
        {
            var l = raw.GetList("levels");
            if (l != null && l.Count > 0) return l;
        }

        var l2 = collection.GetList("levels");
        if (l2 != null && l2.Count > 0) return l2;

        // collection.raw itself is the levels array (rare, but tolerate).
        var rawArr = collection.GetList("raw");
        if (rawArr != null && rawArr.Count > 0) return rawArr;

        return null;
    }

    /// <summary>Resolve a display name from raw.name / collection.name / pick.code / default.</summary>
    private static string ResolveName(Dictionary<string, object> collection, Dictionary<string, object> pick)
    {
        string name = collection?.GetDict("raw")?.GetString("name");
        if (string.IsNullOrEmpty(name)) name = collection?.GetString("name");
        if (string.IsNullOrEmpty(name)) name = pick?.GetString("code");
        if (string.IsNullOrEmpty(name)) name = "Server Collection";
        return name;
    }
}
