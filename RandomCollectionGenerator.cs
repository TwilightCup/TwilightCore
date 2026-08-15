using System.Collections.Generic;

namespace TwilightCore;

/// <summary>
/// Builds a random collection from the config's RandomLevelCount +
/// RandomLevelPool settings. Used by the "lc random" console command.
/// The generated collection is transient — it is never written back to
/// the config file and only exists for the duration of its run.
/// </summary>
internal static class RandomCollectionGenerator
{
    /// <summary>
    /// Fallback level count when the config doesn't specify one (or the
    /// value is invalid). Also the default written to new config files.
    /// </summary>
    public const int DefaultLevelCount = 5;

    /// <summary>
    /// Fallback pool when the config's RandomLevelPool is missing/empty:
    /// the 12 regular BuiltIn levels (excludes the Intro_Reprise / Credits
    /// specials).
    /// </summary>
    private static readonly string[] _defaultLevelPool =
    {
        "Intro", "Train", "Carry", "Climb", "Break", "Siege", "Water",
        "Power", "Aztec", "Halloween", "Steam", "Ice",
    };

    /// <summary>
    /// Generate a random collection by drawing <c>RandomLevelCount</c> levels
    /// (without replacement) from <c>RandomLevelPool</c>, filtering out levels
    /// that are not currently available (e.g. unsubscribed workshop levels).
    ///
    /// On success returns the collection and sets <paramref name="message"/>
    /// to a comma-separated list of the drawn levels. On failure returns null
    /// and sets <paramref name="message"/> to a description of the problem.
    /// </summary>
    public static CollectionDefinition Generate(out string message)
    {
        message = null;

        int count = ConfigLoader.RandomLevelCount;
        if (count <= 0)
            count = DefaultLevelCount;

        List<string> pool = null;
        var configPool = ConfigLoader.RandomLevelPool;
        if (configPool != null && configPool.Count > 0)
            pool = new List<string>(configPool);
        else
            pool = new List<string>(_defaultLevelPool);

        // Keep only levels that are currently playable (available content).
        // Duplicate IDs in the pool are drawn at most once.
        var seen = new HashSet<string>();
        var available = new List<string>(pool.Count);
        foreach (var id in pool)
        {
            if (string.IsNullOrEmpty(id) || !seen.Add(id))
                continue;
            bool isMissing;
            if (CollectionManager.ValidateLevelId(id, out isMissing))
                available.Add(id);
            else
                Plugin.Logger.LogWarning(
                    $"RandomCollection: skipping unavailable level '{id}' in the level pool.");
        }

        if (available.Count == 0)
        {
            message = "random level pool is empty or contains no available levels. " +
                      "Check RandomLevelPool in LevelCollections.json.";
            return null;
        }

        if (count > available.Count)
        {
            Plugin.Logger.LogWarning(
                $"RandomCollection: requested {count} levels but only {available.Count} " +
                $"available; drawing {available.Count} instead.");
            count = available.Count;
        }

        // Fisher-Yates shuffle, then take the first `count` entries.
        var rng = new System.Random();
        for (int i = available.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            string tmp = available[i];
            available[i] = available[j];
            available[j] = tmp;
        }

        var levels = available.GetRange(0, count);
        message = string.Join(", ", levels.ToArray());

        return new CollectionDefinition
        {
            Name = "Random Collection",
            Levels = levels,
        };
    }
}
