using System.Globalization;
using UnityEngine;

namespace TwilightCore.Hud;

/// <summary>
/// A two-line, right-top-anchored OnGUI text showing the current collection
/// run — styled after TwilightTimer's HUD (plain bold text, dynamic OS font,
/// no window chrome), but single-colour (no gradient), the colour set as hex
/// in the config:
///
///   [合集名]
///   [当前关卡英文名] x/N
///
/// Only visible while a collection run is active (config or server transient).
/// The English level name is the game's own "LEVEL/&lt;id&gt;" table entry read
/// from the English column, so it does not follow the player's language.
/// </summary>
public class CollectionInfoHud : MonoBehaviour
{
    private GUIStyle _style;
    private Font _font;

    private void Awake()
    {
        _style = new GUIStyle
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperRight,
            richText = false,
            normal = { textColor = Color.white },
        };
    }

    private void OnDestroy()
    {
        if (_font != null) Destroy(_font);
        _font = null;
    }

    private void OnGUI()
    {
        if (!TwilightConfig.HudEnabled.Value) return;

        var mgr = CollectionManager.Instance;
        if (mgr == null || !mgr || !mgr.IsInCollectionRun) return;

        var col = mgr.CurrentCollection;
        if (col == null || col.Levels == null || col.Levels.Count == 0) return;

        EnsureFont(TwilightConfig.HudFontSize.Value);
        ApplyStyle();

        string line1 = string.IsNullOrEmpty(col.Name) ? "(unnamed)" : col.Name;
        int total = col.Levels.Count;
        int idx = mgr.CurrentLevelIndex + 1; // 1-based display
        if (idx < 1) idx = 1;
        if (idx > total) idx = total;
        string line2 = CollectionManager.GetEnglishLevelName(mgr.CurrentLevelId) + $" {idx}/{total}";

        const float margin = 16f;
        float widest = Mathf.Max(
            _style.CalcSize(new GUIContent(line1)).x,
            _style.CalcSize(new GUIContent(line2)).x);
        var rect = new Rect(Screen.width - widest - margin, margin, widest, _style.lineHeight * 2f + 4f);

        GUI.Label(rect, line1, _style);
        // Second line directly under the first, same right edge.
        var rect2 = new Rect(rect.x, rect.y + _style.lineHeight + 2f, rect.width, _style.lineHeight);
        GUI.Label(rect2, line2, _style);
    }

    /// <summary>(Re)create the dynamic OS font when the configured size changes
    /// (mirrors TwilightTimer's TimerHud.EnsureFont).</summary>
    private void EnsureFont(int size)
    {
        if (size <= 0) size = 18;
        if (_font != null && _style.fontSize == size) return;
        try
        {
            // A dynamic OS font with a broad fallback list renders Latin + CJK
            // (collection names may be Chinese).
            _font = Font.CreateDynamicFontFromOSFont(new[]
            {
                "PingFang SC", "Microsoft YaHei", "Noto Sans CJK SC",
                "Noto Sans CJK", "Heiti SC", "Arial Unicode MS", "Arial",
            }, size);
            _style.font = _font;
            _style.fontSize = size;
        }
        catch (System.Exception ex)
        {
            Plugin.Logger.LogWarning($"[CollectionInfoHud] dynamic font creation failed: {ex.Message}");
            _font = null;
        }
    }

    /// <summary>Refresh the text colour from the configured hex (cheap; parsed per
    /// frame — TryParseColor on a short string, same as TwilightTimer's live hex box).</summary>
    private void ApplyStyle()
    {
        _style.normal.textColor = HexColor.Parse(
            TwilightConfig.HudTextColor.Value, DefaultColor);
    }

    private static readonly Color DefaultColor = new Color(1f, 0.85f, 0.3f, 1f); // matches TwilightTimer's default ColorA
}

/// <summary>
/// Hex colour parsing for HUD config values (RRGGBB or RRGGBBAA, optional
/// leading #), same grammar as TwilightTimer's GradientText.ParseColor —
/// kept local because that class lives in the other plugin.
/// </summary>
internal static class HexColor
{
    public static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        string h = hex.Trim();
        if (h.StartsWith("#")) h = h.Substring(1);
        if (h.Length != 6 && h.Length != 8) return fallback;
        if (!uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return fallback;
        if (h.Length == 6)
            return new Color(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, 1f);
        return new Color(((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }
}
