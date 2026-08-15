using System;
using System.Collections.Generic;
using I2.Loc;

namespace TwilightCore;

/// <summary>
/// Lightweight localisation for the plugin's own UI text
/// (English / Simplified Chinese / Japanese), following the game's
/// I2.Loc language.  The game's localisation table has no terms for
/// our custom UI, so we ship our own dictionary and refresh registered
/// text setters whenever LocalizationManager.OnLocalisation fires
/// (i.e. when the player changes language in Options → Language).
/// </summary>
internal static class LocalizedText
{
    private sealed class Entry
    {
        public readonly string En;
        public readonly string Zh;
        public readonly string Ja;

        public Entry(string en, string zh, string ja)
        {
            En = en;
            Zh = zh;
            Ja = ja;
        }
    }

    private static readonly Dictionary<string, Entry> Terms =
        new Dictionary<string, Entry>
        {
            { "Collections", new Entry("Collections", "合辑", "コレクション") },
            { "Levels", new Entry("Levels", "关卡", "レベル") },
            { "Back", new Entry("Back", "返回", "戻る") },
            { "Refresh", new Entry("Refresh", "刷新", "更新") },
            { "Start", new Entry("Start", "开始", "開始") },
        };

    private static readonly List<Action> Refreshers = new List<Action>();
    private static bool _hooked;

    /// <summary>
    /// Current translation for a term key, based on
    /// LocalizationManager.CurrentLanguage.  Falls back to English for
    /// every language other than Simplified Chinese / Japanese.
    /// </summary>
    public static string Get(string key)
    {
        Entry e;
        if (key == null || !Terms.TryGetValue(key, out e))
        {
            Plugin.Logger.LogWarning("[LocalizedText] Unknown term: " + key);
            return key;
        }
        string lang = LocalizationManager.CurrentLanguage;
        if (!string.IsNullOrEmpty(lang))
        {
            lang = lang.ToLowerInvariant();
            if (lang == "chinese simplified" || lang.Contains("chinese simplified"))
                return e.Zh;
            if (lang == "japanese" || lang.Contains("japanese"))
                return e.Ja;
        }
        return e.En;
    }

    /// <summary>
    /// Register a callback that updates one piece of UI text; it is
    /// invoked immediately (to apply the current language) and again
    /// every time the game's language changes.
    /// </summary>
    public static void Register(Action refresher)
    {
        if (refresher == null)
            return;
        HookLocalisation();
        if (!Refreshers.Contains(refresher))
        {
            Refreshers.Add(refresher);
            try
            {
                refresher();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError("[LocalizedText] initial refresh failed: " + ex);
            }
        }
    }

    public static void Unregister(Action refresher)
    {
        if (refresher == null)
            return;
        Refreshers.Remove(refresher);
    }

    private static void HookLocalisation()
    {
        if (_hooked)
            return;
        _hooked = true;
        LocalizationManager.OnLocalisation += Fire;
    }

    private static void Fire()
    {
        for (int i = 0; i < Refreshers.Count; i++)
        {
            try
            {
                Refreshers[i]();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError("[LocalizedText] refresher failed: " + ex);
            }
        }
    }
}
