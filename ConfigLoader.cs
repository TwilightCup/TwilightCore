using System.Collections.Generic;
using System.IO;
using BepInEx;
using HumanAPI;
using UnityEngine;

namespace TwilightCore;

/// <summary>
/// Loads and provides access to the collection configuration file.
/// Uses MiniJSON for serialization because Unity 2017's JsonUtility
/// does not handle nested List&lt;T&gt; correctly.
/// </summary>
public static class ConfigLoader
{
    private static CollectionConfig _config;
    private static string _configPath;

    private static readonly CollectionDefinition[] _emptyCollections = new CollectionDefinition[0];

    public static IReadOnlyList<CollectionDefinition> Collections
    {
        get
        {
            if (_config == null || _config.Collections == null)
                return _emptyCollections;
            return _config.Collections;
        }
    }

    /// <summary>
    /// Number of levels to draw for the random collection (lc random).
    /// 0 when the config doesn't specify it — RandomCollectionGenerator
    /// falls back to its own default in that case.
    /// </summary>
    public static int RandomLevelCount => _config != null ? _config.RandomLevelCount : 0;

    /// <summary>
    /// Pool of LevelId strings the random collection is drawn from.
    /// Null when the config doesn't specify it — RandomCollectionGenerator
    /// falls back to its own default pool in that case.
    /// </summary>
    public static IReadOnlyList<string> RandomLevelPool => _config?.RandomLevelPool;

    public static void Load()
    {
        _configPath = Path.Combine(Paths.ConfigPath, "LevelCollections.json");
        DoLoad();
    }

    /// <summary>
    /// Reload the config file from disk without restarting the game.
    /// Safe to call at any time — existing collection runs are unaffected
    /// (they hold indices, not references).
    /// </summary>
    public static void Reload()
    {
        if (string.IsNullOrEmpty(_configPath))
        {
            _configPath = Path.Combine(Paths.ConfigPath, "LevelCollections.json");
        }
        DoLoad();
    }

    private static void DoLoad()
    {
        if (!File.Exists(_configPath))
        {
            CreateDefaultConfig();
        }

        try
        {
            string json = File.ReadAllText(_configPath);
            _config = DeserializeConfig(json);
            if (_config == null || _config.Collections == null)
            {
                Plugin.Logger.LogWarning(
                    "LevelCollections config parsed but was null or had no Collections; using empty list."
                );
                _config = new CollectionConfig { Collections = new List<CollectionDefinition>() };
            }
            else
            {
                Plugin.Logger.LogInfo(
                    $"LevelCollections: loaded {_config.Collections.Count} collection(s)."
                );
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Logger.LogError($"Failed to load LevelCollections config: {ex.Message}");
            if (_config == null)
                _config = new CollectionConfig { Collections = new List<CollectionDefinition>() };
            // else: keep the previously-loaded config — don't discard user data
            // because of a transient parse error (e.g. damaged file, bad unicode escape).
            // The error is visible in the BepInEx console; per-collection errors
            // (bad level names, etc.) are surfaced in the UI by DeserializeConfig.
        }
    }

    /// <summary>
    /// Try to get a thumbnail for a BuiltIn level by its LevelId.
    /// </summary>
    public static Texture2D GetThumbnail(string levelId)
    {
        if (
            CollectionManager.ResolveLevelType(levelId) == WorkshopItemSource.BuiltIn
            && HFFResources.instance != null
        )
        {
            return HFFResources.instance.FindTextureResource("LevelImages/" + levelId);
        }
        return null;
    }

    // ── Serialization via MiniJSON ─────────────────────────────────

    private static void CreateDefaultConfig()
    {
        var defaultConfig = new CollectionConfig
        {
            Collections = new List<CollectionDefinition>
            {
                new CollectionDefinition
                {
                    Name = "Example Collection",
                    Levels = new List<string>
                    {
                        "Intro",
                        "Water",
                        "Train",
                        "Carry",
                        "Climb",
                        "Halloween",
                        "Steam",
                        "Ice",
                    },
                },
            },
            RandomLevelCount = 5,
            RandomLevelPool = new List<string>
            {
                "Intro",
                "Train",
                "Carry",
                "Climb",
                "Break",
                "Siege",
                "Water",
                "Power",
                "Aztec",
                "Halloween",
                "Steam",
                "Ice",
            },
        };

        try
        {
            string json = MiniJSON.Serialize(defaultConfig);
            // Pretty-print: re-parse and re-serialize with indentation
            object parsed = MiniJSON.Deserialize(json);
            string pretty = SerializePretty(parsed, 0);
            File.WriteAllText(_configPath, pretty);
            Plugin.Logger.LogInfo($"Created default LevelCollections config at: {_configPath}");
        }
        catch (System.Exception ex)
        {
            Plugin.Logger.LogError($"Failed to create default config: {ex.Message}");
        }
    }

    private static CollectionConfig DeserializeConfig(string json)
    {
        object parsed = MiniJSON.Deserialize(json);
        if (parsed is Dictionary<string, object> root)
        {
            var config = new CollectionConfig();
            if (
                root.TryGetValue("Collections", out object colsObj)
                && colsObj is List<object> colsList
            )
            {
                config.Collections = new List<CollectionDefinition>();
                foreach (object colObj in colsList)
                {
                    CollectionDefinition col;
                    try
                    {
                        if (colObj is Dictionary<string, object> colDict)
                        {
                            col = new CollectionDefinition();
                            if (colDict.TryGetValue("Name", out object nameObj))
                                col.Name = nameObj as string ?? "";

                            // ── Parse Levels ──────────────────────
                            if (colDict.TryGetValue("Levels", out object lvlsObj))
                            {
                                if (lvlsObj is List<object> lvlsList)
                                {
                                    col.Levels = new List<string>();
                                    int skipped = 0;
                                    foreach (object lvlObj in lvlsList)
                                    {
                                        if (lvlObj is string s)
                                        {
                                            col.Levels.Add(s);
                                        }
                                        else
                                        {
                                            skipped++;
                                            // Salvage the value so the level count
                                            // still matches what's in the file.
                                            col.Levels.Add(lvlObj?.ToString() ?? "(null)");
                                        }
                                    }
                                    if (skipped > 0)
                                    {
                                        col.IsBroken = true;
                                        col.ErrorMessage = $"{skipped} level(s) are not strings (e.g. a number).";
                                    }
                                }
                                else
                                {
                                    // "Levels" exists but is not an array
                                    col.IsBroken = true;
                                    col.ErrorMessage = "\"Levels\" is not an array.";
                                }
                            }
                        }
                        else
                        {
                            // The collection element is not an object (e.g. a bare string)
                            col = new CollectionDefinition
                            {
                                Name = "(invalid entry)",
                                Levels = new List<string>(),
                                IsBroken = true,
                                ErrorMessage = "Collection entry is not a JSON object."
                            };
                        }
                    }
                    catch (System.Exception ex)
                    {
                        col = new CollectionDefinition
                        {
                            Name = "(parse error)",
                            Levels = new List<string>(),
                            IsBroken = true,
                            ErrorMessage = $"Failed to parse collection: {ex.Message}"
                        };
                    }

                    // ── Validation (applies even after successful parse) ──
                    if (!col.IsBroken && string.IsNullOrEmpty(col.Name))
                    {
                        col.IsBroken = true;
                        col.ErrorMessage = "Collection has no Name.";
                        col.Name = "(unnamed)";
                    }
                    if (!col.IsBroken && (col.Levels == null || col.Levels.Count == 0))
                    {
                        col.IsBroken = true;
                        col.ErrorMessage = "Collection has no Levels.";
                        col.Levels = new List<string>();
                    }

                    config.Collections.Add(col);
                }
            }

            // ── Parse random-collection settings (lc random) ──────
            // Missing fields stay 0/null; RandomCollectionGenerator falls
            // back to defaults in that case (old config files, etc.).
            if (root.TryGetValue("RandomLevelCount", out object countObj))
            {
                try
                {
                    config.RandomLevelCount = System.Convert.ToInt32(countObj);
                }
                catch (System.Exception)
                {
                    config.RandomLevelCount = 0; // invalid value → default
                }
            }
            if (root.TryGetValue("RandomLevelPool", out object poolObj) && poolObj is List<object> poolList)
            {
                config.RandomLevelPool = new List<string>();
                foreach (object entry in poolList)
                {
                    // Salvage non-string entries so the pool length still
                    // matches what's in the file.
                    config.RandomLevelPool.Add(entry as string ?? entry?.ToString() ?? "");
                }
            }

            return config;
        }
        return null;
    }

    // ── Pretty-printer ─────────────────────────────────────────────

    private static string SerializePretty(object obj, int indent)
    {
        var sb = new System.Text.StringBuilder();
        SerializePrettyValue(obj, indent, sb);
        return sb.ToString();
    }

    private static void SerializePrettyValue(object obj, int indent, System.Text.StringBuilder sb)
    {
        if (obj == null)
        {
            sb.Append("null");
        }
        else if (obj is Dictionary<string, object> dict)
        {
            sb.Append("{\n");
            bool first = true;
            foreach (var kvp in dict)
            {
                if (!first)
                    sb.Append(",\n");
                first = false;
                Indent(sb, indent + 1);
                sb.Append('"');
                sb.Append(EscapeString(kvp.Key));
                sb.Append("\": ");
                SerializePrettyValue(kvp.Value, indent + 1, sb);
            }
            sb.Append('\n');
            Indent(sb, indent);
            sb.Append('}');
        }
        else if (obj is List<object> list)
        {
            if (list.Count == 0)
            {
                sb.Append("[]");
                return;
            }
            sb.Append("[\n");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0)
                    sb.Append(",\n");
                Indent(sb, indent + 1);
                SerializePrettyValue(list[i], indent + 1, sb);
            }
            sb.Append('\n');
            Indent(sb, indent);
            sb.Append(']');
        }
        else if (obj is string s)
        {
            sb.Append('"');
            sb.Append(EscapeString(s));
            sb.Append('"');
        }
        else if (obj is bool b)
        {
            sb.Append(b ? "true" : "false");
        }
        else if (obj is long l)
        {
            sb.Append(l);
        }
        else if (obj is int i32)
        {
            sb.Append(i32);
        }
        else if (obj is double d)
        {
            sb.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append(obj.ToString());
        }
    }

    private static void Indent(System.Text.StringBuilder sb, int level)
    {
        for (int i = 0; i < level * 2; i++)
            sb.Append(' ');
    }

    private static string EscapeString(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        var sb = new System.Text.StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
