using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TwilightCore;

/// <summary>
/// ListViewItem subclass for collection/level list entries.
/// Binds a string name to a TextMeshProUGUI label and manages selection highlight.
/// </summary>
public class CollectionListItem : ListViewItem, ISelectHandler
{
    private static readonly Color SelColor = new Color(0f, 0f, 0f, 0.9f);
    private static readonly Color OkNormalColor = new Color(1f, 1f, 1f, 0f);
    private static readonly Color BrokenTintColor = new Color(0.94f, 0.25f, 0.25f, 0.55f);

    private string _bindText;
    private GameObject _tintGo;

    private bool IsBroken =>
        _bindText != null &&
        (_bindText.StartsWith("(!) ") || _bindText.StartsWith("(missing) "));

    // ── Lifecycle ──────────────────────────────────────────────

    private void OnEnable()
    {
        if (_bindText != null)
            SetLabel(_bindText);
    }

    private void Start()
    {
        var label = GetComponentInChildren<TextMeshProUGUI>();
        if (label != null)
        {
            label.enableAutoSizing = false;
            label.fontSize = 30;
        }
        EnsureTint();
    }

    public override void Bind(int index, object data)
    {
        base.Bind(index, data);
        _bindText = data as string ?? "";
        if (gameObject.activeInHierarchy)
            SetLabel(_bindText);

        EnsureTint();

        var btn = GetComponent<Button>();
        if (btn != null)
        {
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(HandleClick);
        }
    }

    // ── Tint layer ─────────────────────────────────────────────

    /// <summary>
    /// Add or remove a red-tinted overlay behind the label so broken
    /// items stand out visually.  This is a SEPARATE RawImage from the
    /// Button's targetGraphic, so it is never affected by ColorTint
    /// transitions or MenuButton state changes.
    /// </summary>
    private void EnsureTint()
    {
        if (IsBroken && _tintGo == null)
        {
            _tintGo = new GameObject("BrokenTint", typeof(RectTransform));
            _tintGo.transform.SetParent(transform, false);
            var rt = _tintGo.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            _tintGo.transform.SetAsFirstSibling(); // behind everything
            var img = _tintGo.AddComponent<RawImage>();
            img.texture = Texture2D.whiteTexture;
            img.color = BrokenTintColor;
            img.raycastTarget = false;
        }
        else if (!IsBroken && _tintGo != null)
        {
            Destroy(_tintGo);
            _tintGo = null;
        }
    }

    // ── Highlight ──────────────────────────────────────────────

    public void SetActive(bool active)
    {
        var mb = GetComponent<MenuButton>();
        if (mb == null || mb.targetGraphic == null) return;

        var c = active ? SelColor : OkNormalColor;
        mb.isOn = active;
        mb.targetGraphic.color = c;
        mb.targetGraphic.CrossFadeColor(c, 0f, true, true);
    }

    // ── Helpers ────────────────────────────────────────────────

    private void HandleClick()
    {
        var lv = GetComponentInParent<ListView>();
        if (lv != null)
            lv.OnSelect(this);
    }

    public new void OnSelect(BaseEventData eventData)
    {
        var lv = GetComponentInParent<ListView>();
        if (lv != null)
            lv.OnSelect(this);
    }

    public void SetFocusPrefix(bool focused)
    {
        var label = GetComponentInChildren<TextMeshProUGUI>();
        if (label == null) return;
        var text = label.text;
        if (focused && !text.StartsWith("> "))
            SetLabel("> " + text);
        else if (!focused && text.StartsWith("> "))
            SetLabel(text.Substring(2));
    }

    /// <summary>
    /// Set the item label text, swapping to a CJK-capable font when the
    /// current font cannot display the text (Chinese/Japanese names).
    /// </summary>
    private void SetLabel(string text)
    {
        var label = GetComponentInChildren<TextMeshProUGUI>();
        if (label == null) return;
        label.text = text;
        UiFont.EnsureCjkFont(label);
    }
}
