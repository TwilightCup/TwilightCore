using TMPro;
using UnityEngine;

namespace TwilightCore;

/// <summary>
/// Ensures the plugin's TextMeshPro text renders with a GAME font.
///
/// New TextMeshProUGUI components get font = null and fall back to
/// TMP_FontAsset.defaultFontAsset ("Fonts & Materials/LiberationSans SDF")
/// — a Latin-only font, so Chinese text would render as tofu boxes.  The
/// game's own CJK fonts (NotoSansCJKsc / ARIALUNI / rounded-x-mgenplus)
/// are static subset atlases: they only contain glyphs the game UI text
/// uses (the localisation table in sharedassets0.assets is the exact set).
///
/// All plugin UI terms are deliberately chosen from that set (e.g. the
/// Simplified-Chinese "Collections" term is "合辑" — 合✓ 辑✓), so every
/// fixed UI string renders with the game's native font.  For any text the
/// game fonts cannot cover (e.g. user custom collection names) we keep the
/// current font and log a warning — no third-party/system fonts are used.
/// </summary>
internal static class UiFont
{
    /// <summary>Simplified-Chinese characters the plugin's UI can show.
    /// All of these are present in the game's own UI text, hence in its
    /// CJK fonts (verified against the localisation table).</summary>
    private const string ZhSample = "合辑关卡返回刷新开始";

    /// <summary>Japanese characters the plugin's UI can show.</summary>
    private const string JaSample = "コレクションレベル戻る更新開始";

    private static TMP_FontAsset _gameFont;
    private static bool _warnedNoFont;

    /// <summary>
    /// If <paramref name="tmp"/>'s current font cannot display its text,
    /// swap in a game font that can.  No-op when the font is fine, the
    /// text is empty, or no game font covers the text.
    /// </summary>
    public static void EnsureCjkFont(TMP_Text tmp)
    {
        if (tmp == null || !tmp)
            return;
        if (string.IsNullOrEmpty(tmp.text))
            return;
        if (tmp.font != null && tmp.font.HasCharacters(tmp.text))
            return;

        var game = GetGameCjkFont();
        if (game != null && game.HasCharacters(tmp.text))
        {
            Plugin.Logger.LogInfo("[UiFont] swapping font on '" + tmp.name + "' to " + game.name);
            tmp.font = game;
            return;
        }

        if (!_warnedNoFont)
        {
            _warnedNoFont = true;
            Plugin.Logger.LogWarning(
                "[UiFont] no game font covers this text; it may show tofu: " + tmp.text
            );
        }
    }

    /// <summary>
    /// The loaded game TMP_FontAsset covering the most plugin CJK
    /// characters (cached).  Null when none contains any.
    /// </summary>
    private static TMP_FontAsset GetGameCjkFont()
    {
        if (_gameFont != null && _gameFont)
            return _gameFont;
        var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        TMP_FontAsset best = null;
        int bestScore = 0;
        foreach (var fa in fonts)
        {
            if (fa == null || !fa)
                continue;
            int score = CountCovered(fa, ZhSample) + CountCovered(fa, JaSample);
            if (score > bestScore)
            {
                bestScore = score;
                best = fa;
            }
        }
        if (best != null && bestScore > 0)
        {
            _gameFont = best;
            Plugin.Logger.LogInfo(
                "[UiFont] game font '" + best.name + "' covers "
                + bestScore + "/" + (ZhSample.Length + JaSample.Length)
                + " plugin CJK chars"
            );
        }
        return _gameFont;
    }

    private static int CountCovered(TMP_FontAsset fa, string chars)
    {
        int n = 0;
        for (int i = 0; i < chars.Length; i++)
            if (fa.HasCharacter(chars[i]))
                n++;
        return n;
    }
}
