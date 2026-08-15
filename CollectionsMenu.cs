using System;
using System.Collections.Generic;
using HumanAPI;
using I2.Loc;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Button = UnityEngine.UI.Button;

namespace TwilightCore;

public class CollectionsMenu : MenuTransition
{
    private ListView _colList,
        _lvlList;
    private readonly List<CollectionDefinition> _colData = new List<CollectionDefinition>();
    private int _selCol = -1,
        _selLvl = -1;
    private CollectionListItem _prevColItem,
        _prevLvlItem;

    private RawImage _thumbnail;
    private TextMeshProUGUI _titleText;
    private Button _startBtn;
    private GameObject _startBtnGo;

    private TextMeshProUGUI _colHeaderText;
    private TextMeshProUGUI _lvlHeaderText;
    private Action _localizeRefresh;

    private const float PanelW = 280f;
    private const float ItemH = 40f;
    private const float ThumbW = 400f;
    private const float ThumbH = 225f;

    // ── Lifecycle ──────────────────────────────────────────────────

    /// <summary>
    /// Ensure RectTransform and CanvasGroup exist before MenuTransition.OnEnable()
    /// caches them — otherwise Apply() throws NRE during transitions.
    /// </summary>
    private void Awake()
    {
        if (GetComponent<RectTransform>() == null)
            gameObject.AddComponent<RectTransform>();
        if (GetComponent<CanvasGroup>() == null)
            gameObject.AddComponent<CanvasGroup>();
    }

    private void OnDestroy()
    {
        if (_localizeRefresh != null)
        {
            LocalizedText.Unregister(_localizeRefresh);
            _localizeRefresh = null;
        }
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        BuildOnce();
    }

    public override void OnGotFocus()
    {
        base.OnGotFocus();
        _colList.onSelect = OnColSelect;
        _colList.onSubmit = OnColSubmit;
        _colList.onDeSelect = OnColDeSelect;
        _colList.onPointerClick = OnColPointerClick;
        _lvlList.onSelect = OnLvlSelect;
        _lvlList.onSubmit = OnLvlSubmit;
        _lvlList.onDeSelect = OnLvlDeSelect;
        _lvlList.onPointerClick = OnLvlPointerClick;
        Rebuild();
        if (_colData.Count > 0)
        {
            _colList.FocusItem(0);
            // FocusItem uses EventSystem, which doesn't call onSelect
            // — apply the selected highlight and track it manually.
            var firstCol = _colList.GetButton(0)?.GetComponent<CollectionListItem>();
            if (firstCol != null)
            {
                if (_prevColItem != null && _prevColItem != firstCol)
                    _prevColItem.SetActive(false);
                firstCol.SetActive(true);
                firstCol.SetFocusPrefix(true);
                _prevColItem = firstCol;
            }
            SelectCollection(0);
            SyncNavigation();
        }
        if (_startBtnGo != null)
            _startBtnGo.SetActive(true);
    }

    public override void OnLostFocus()
    {
        base.OnLostFocus();
        Clear();
    }

    /// <summary>
    /// Called AFTER the fade-in animation completes (alpha=1).
    /// Ensures the parent Canvas has a GraphicRaycaster so mouse
    /// events can reach our UI elements.  The game's "Menu" Canvas
    /// in ScreenSpaceCamera mode ships without one — only canvases
    /// like "Shell" / "DialogOverlay" have it, and our dynamically
    /// created UI lives under "Menu".
    /// </summary>
    public override void OnTansitionedIn()
    {
        base.OnTansitionedIn();

        var parentCanvas = GetComponentInParent<Canvas>();
        if (parentCanvas != null && parentCanvas.GetComponent<GraphicRaycaster>() == null)
        {
            parentCanvas.gameObject.AddComponent<GraphicRaycaster>();
        }
    }

    public override void OnBack()
    {
        Plugin.Logger.LogInfo("[CollectionsMenu] OnBack() called.");
        TransitionBack<LevelSelectMenu2>();
    }

    // ── One-time UI construction ──────────────────────────────────

    private void BuildOnce()
    {
        if (_colList != null)
            return;

        var root = GetComponent<RectTransform>() ?? gameObject.AddComponent<RectTransform>();
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = root.offsetMax = Vector2.zero;

        var tmpl = FindTemplateButton();

        var main = NewChild("MainPanel", gameObject);
        var mRT = main.GetComponent<RectTransform>();
        mRT.anchorMin = new Vector2(0.04f, 0.10f);
        mRT.anchorMax = new Vector2(0.96f, 0.92f);
        mRT.offsetMin = mRT.offsetMax = Vector2.zero;
        var hlg = main.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8f;
        hlg.childAlignment = TextAnchor.UpperCenter;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = true;

        BuildColList(main);
        BuildLvlList(main);
        BuildInfo(main, tmpl);

        BuildBackButton(tmpl);
        BuildRefreshButton(tmpl);

        RegisterLocalizeRefresh();
    }

    /// <summary>
    /// Register one localisation refresh callback covering every text
    /// this menu owns: it applies the current language immediately and
    /// re-applies it whenever the game language changes.
    /// </summary>
    private void RegisterLocalizeRefresh()
    {
        _localizeRefresh = () =>
        {
            if (_colHeaderText != null && _colHeaderText)
            {
                _colHeaderText.text = LocalizedText.Get("Collections");
                UiFont.EnsureCjkFont(_colHeaderText);
            }
            if (_lvlHeaderText != null && _lvlHeaderText)
            {
                _lvlHeaderText.text = LocalizedText.Get("Levels");
                UiFont.EnsureCjkFont(_lvlHeaderText);
            }
            if (_backBtnGo != null && _backBtnGo)
                SetButtonText(_backBtnGo, LocalizedText.Get("Back"));
            if (_refreshBtnGo != null && _refreshBtnGo)
                SetButtonText(_refreshBtnGo, LocalizedText.Get("Refresh"));
            if (_startBtnGo != null && _startBtnGo)
                SetButtonText(_startBtnGo, LocalizedText.Get("Start"));
            RefreshLevelNames();
        };
        LocalizedText.Register(_localizeRefresh);
    }

    private static GameObject FindTemplateButton()
    {
        var all = MenuSystem.instance.GetComponentsInChildren<LevelSelectMenu2>(true);
        if (all == null || all.Length == 0)
        {
            Plugin.Logger.LogWarning(
                "[CollectionsMenu] FindTemplateButton: no LevelSelectMenu2 found."
            );
            return null;
        }
        var lsm2 = all[0];
        // Prefer PlayButton / showCustomButton over BackButton —
        // BackButton carries serialised back-transition behaviour
        // that we don't want on our cloned buttons.
        if (lsm2.showCustomButton != null && lsm2.showCustomButton)
        {
            Plugin.Logger.LogInfo("[CollectionsMenu] FindTemplateButton: using " + lsm2.showCustomButton.name);
            return lsm2.showCustomButton;
        }
        if (lsm2.PlayButton != null && lsm2.PlayButton)
        {
            Plugin.Logger.LogInfo("[CollectionsMenu] FindTemplateButton: using " + lsm2.PlayButton.name);
            return lsm2.PlayButton;
        }
        if (lsm2.BackButton != null && lsm2.BackButton)
        {
            Plugin.Logger.LogInfo(
                "[CollectionsMenu] FindTemplateButton: using " + lsm2.BackButton.name + "(fallback)."
            );
            return lsm2.BackButton;
        }
        Plugin.Logger.LogWarning(
            "[CollectionsMenu] FindTemplateButton: no suitable button found on LevelSelectMenu2."
        );
        return null;
    }

    // ── Left / Middle: list panels ────────────────────────────────

    private void BuildColList(GameObject parent)
    {
        var panel = NewChild("ColPanel", parent);
        panel.AddComponent<LayoutElement>().preferredWidth = PanelW;
        var vlg = panel.AddComponent<VerticalLayoutGroup>();
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.spacing = 4f;
        _colHeaderText = AddHeader(LocalizedText.Get("Collections"), panel);
        _colList = BuildListView(panel);
    }

    private void BuildLvlList(GameObject parent)
    {
        var panel = NewChild("LvlPanel", parent);
        panel.AddComponent<LayoutElement>().preferredWidth = PanelW;
        var vlg = panel.AddComponent<VerticalLayoutGroup>();
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.spacing = 4f;
        _lvlHeaderText = AddHeader(LocalizedText.Get("Levels"), panel);
        _lvlList = BuildListView(panel);
    }

    /// <summary>
    /// Build a game-native ListView inside a panel.  The ListView is created
    /// inactive so its Awake sees the fully-wired itemTemplate and itemContainer.
    /// </summary>
    private static ListView BuildListView(GameObject panelParent)
    {
        var lvGo = new GameObject("ListView", typeof(RectTransform));
        lvGo.SetActive(false);

        // Fill remaining space in parent
        var lvRT = lvGo.GetComponent<RectTransform>();
        lvRT.anchorMin = Vector2.zero;
        lvRT.anchorMax = Vector2.one;
        lvRT.offsetMin = Vector2.zero;
        lvRT.offsetMax = Vector2.zero;
        var le = lvGo.AddComponent<LayoutElement>();
        le.preferredHeight = 100f;
        le.flexibleHeight = 1f;

        // Unified dark backdrop behind the items, matching the game's
        // LevelSelectMenu2.itemListNormal colour.  Also satisfies the
        // GraphicRaycaster requirement for InControlInputModule.
        var bg = lvGo.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.192f);
        bg.raycastTarget = false;

        // RectMask2D clips children (items) to the list bounds so
        // items don't bleed out past the dark backdrop.
        lvGo.AddComponent<RectMask2D>();

        // Item container pinned to top, grows downward
        var container = NewChild("ItemContainer", lvGo);
        var crt = container.GetComponent<RectTransform>();
        crt.anchorMin = new Vector2(0, 1);
        crt.anchorMax = new Vector2(1, 1);
        crt.pivot = new Vector2(0.5f, 1);
        crt.sizeDelta = Vector2.zero;
        container.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter
            .FitMode
            .PreferredSize;
        var vlg = container.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 2f;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        // Item template: built from scratch with correct settings.
        // Explicit Navigation mode is required for EventSystem to
        // route input events correctly with InControlInputModule.
        var tmpl = NewChild("ItemTemplate", lvGo);
        var tmplRT2 = tmpl.GetComponent<RectTransform>();
        tmplRT2.anchorMin = new Vector2(0, 1);
        tmplRT2.anchorMax = new Vector2(1, 1);
        tmplRT2.pivot = new Vector2(0.5f, 1);
        tmpl.AddComponent<LayoutElement>().preferredHeight = ItemH;
        // Items are transparent in normal state — only the list's dark
        // backdrop shows through.  MenuButton.DoStateTransition handles
        // highlight (dark overlay) and selection (darker overlay).
        // RawImage is used instead of Image because runtime-created
        // Sprite objects can have texture compatibility issues in UI.
        var img2 = tmpl.AddComponent<RawImage>();
        img2.texture = Texture2D.whiteTexture;
        img2.color = new Color(1f, 1f, 1f, 0f);
        var mb2 = tmpl.AddComponent<MenuButton>();
        mb2.targetGraphic = img2;
        var nav = mb2.navigation;
        nav.mode = Navigation.Mode.Automatic;
        mb2.navigation = nav;
        mb2.transition = Selectable.Transition.ColorTint;
        // normalColor alpha controls the background opacity when NOT
        // highlighted/selected.  Zero = fully transparent (invisible).
        var mbColors = mb2.colors;
        mbColors.normalColor = new Color(1f, 1f, 1f, 0f);
        mb2.colors = mbColors;
        tmpl.AddComponent<CollectionListItem>();
        var labelGo2 = NewChild("Label", tmpl);
        var lrt2 = labelGo2.GetComponent<RectTransform>();
        lrt2.anchorMin = new Vector2(0, 0.5f);
        lrt2.anchorMax = new Vector2(1, 0.5f);
        lrt2.pivot = new Vector2(0.5f, 0.5f);
        lrt2.anchoredPosition = new Vector2(0, 2);
        lrt2.sizeDelta = new Vector2(-24, 30);
        var txt2 = labelGo2.AddComponent<TextMeshProUGUI>();
        txt2.enableAutoSizing = false;
        txt2.fontSize = 30;
        txt2.alignment = TextAlignmentOptions.Left;
        tmpl.SetActive(false);

        // Add ListView component while still inactive, wire fields, then activate
        var lv = lvGo.AddComponent<ListView>();
        lv.itemTemplate = tmpl.GetComponent<Button>();
        lv.itemContainer = container;

        lvGo.transform.SetParent(panelParent.transform, false);
        lvGo.SetActive(true);

        return lv;
    }

    // ── Right: info panel ─────────────────────────────────────────

    private void BuildInfo(GameObject parent, GameObject tmpl)
    {
        var panel = NewChild("InfoPanel", parent);
        panel.AddComponent<LayoutElement>().preferredWidth = ThumbW + 16f;
        var vlg = panel.AddComponent<VerticalLayoutGroup>();
        vlg.childForceExpandWidth = false;
        vlg.childForceExpandHeight = false;
        vlg.spacing = 0f;
        vlg.childAlignment = TextAnchor.UpperCenter;

        var spacer = NewChild("Spacer", panel);
        spacer.AddComponent<LayoutElement>().preferredHeight = 32f;

        var thumbArea = NewChild("ThumbArea", panel);
        var thumbLE = thumbArea.AddComponent<LayoutElement>();
        thumbLE.preferredWidth = ThumbW;
        thumbLE.preferredHeight = ThumbH;
        thumbArea.AddComponent<Image>().color = Color.white;
        thumbArea.AddComponent<Mask>().showMaskGraphic = false;

        // RawImage must be a CHILD of thumbArea (not on thumbArea itself —
        // Unity forbids two Graphic components on the same GameObject).
        var thumbChild = NewChild("ThumbImage", thumbArea);
        _thumbnail = thumbChild.AddComponent<RawImage>();
        _thumbnail.color = Color.grey;
        _thumbnail.enabled = false;
        var tnRT = thumbChild.GetComponent<RectTransform>();
        tnRT.anchorMin = Vector2.zero;
        tnRT.anchorMax = Vector2.one;
        tnRT.offsetMin = tnRT.offsetMax = Vector2.zero;

        var overlay = NewChild("Overlay", thumbArea);
        var ovRT = overlay.GetComponent<RectTransform>();
        ovRT.anchorMin = new Vector2(0f, 0f);
        ovRT.anchorMax = new Vector2(1f, 0.25f);
        ovRT.offsetMin = ovRT.offsetMax = Vector2.zero;
        var ovImg = overlay.AddComponent<Image>();
        ovImg.color = new Color(0f, 0f, 0f, 0.55f);
        ovImg.raycastTarget = false;

        // TextMeshProUGUI must be on a child — overlay already has an Image.
        var titleGo = NewChild("Title", overlay);
        _titleText = titleGo.AddComponent<TextMeshProUGUI>();
        _titleText.enableAutoSizing = false;
        _titleText.fontSize = 30;
        _titleText.alignment = TextAlignmentOptions.Center;
        _titleText.color = Color.white;
        var ttRT = titleGo.GetComponent<RectTransform>();
        ttRT.anchorMin = Vector2.zero;
        ttRT.anchorMax = Vector2.one;
        ttRT.offsetMin = new Vector2(8f, 0f);
        ttRT.offsetMax = new Vector2(-8f, 0f);

        // Gap between thumbnail and Start button
        var btnGap = NewChild("BtnGap", panel);
        btnGap.AddComponent<LayoutElement>().preferredHeight = 8f;

        // Start button inside InfoPanel so it aligns with the thumbnail
        BuildStartButton(tmpl, panel);
    }

    // ── Buttons ───────────────────────────────────────────────────

    private Button _backBtn;
    private GameObject _backBtnGo;
    private Button _refreshBtn;
    private GameObject _refreshBtnGo;

    private void BuildBackButton(GameObject tmpl)
    {
        var go = CloneOrCreateButton(tmpl, "CollectionsBackBtn");
        if (go == null)
            return;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(20f, 10f);
        EnsureMinSize(rt, 180f, 36f);
        SetButtonText(go, LocalizedText.Get("Back"));
        var backLabel = go.GetComponentInChildren<TextMeshProUGUI>();
        if (backLabel != null)
        {
            backLabel.enableAutoSizing = false;
            backLabel.fontSize = 32;
            backLabel.alignment = TextAlignmentOptions.Left;
        }
        _backBtn = go.GetComponentInChildren<Button>() ?? go.GetComponent<Button>();
        if (_backBtn != null)
        {
            _backBtn.onClick.RemoveAllListeners();
            _backBtn.onClick.AddListener(() => OnBack());
            var nav = _backBtn.navigation;
            nav.mode = Navigation.Mode.None;
            _backBtn.navigation = nav;
        }
        else
        {
            Plugin.Logger.LogError(
                "[CollectionsMenu] Back button: NO Button component found on clone!"
            );
        }
        _backBtnGo = go;
        go.transform.SetAsLastSibling();
        go.SetActive(true);
    }

    private void BuildRefreshButton(GameObject tmpl)
    {
        var go = CloneOrCreateButton(tmpl, "CollectionsRefreshBtn");
        if (go == null)
            return;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(215f, 10f);
        EnsureMinSize(rt, 180f, 36f);
        SetButtonText(go, LocalizedText.Get("Refresh"));
        var label = go.GetComponentInChildren<TextMeshProUGUI>();
        if (label != null)
        {
            label.enableAutoSizing = false;
            label.fontSize = 32;
            label.alignment = TextAlignmentOptions.Left;
        }
        _refreshBtn = go.GetComponentInChildren<Button>() ?? go.GetComponent<Button>();
        if (_refreshBtn != null)
        {
            _refreshBtn.onClick.RemoveAllListeners();
            _refreshBtn.onClick.AddListener(() => DoRefresh());
            var nav = _refreshBtn.navigation;
            nav.mode = Navigation.Mode.None;
            _refreshBtn.navigation = nav;
        }
        else
        {
            Plugin.Logger.LogError(
                "[CollectionsMenu] Refresh button: NO Button component found on clone!"
            );
        }
        _refreshBtnGo = go;
        go.transform.SetAsLastSibling();
        go.SetActive(true);
    }

    private void BuildStartButton(GameObject tmpl, GameObject parent)
    {
        // Clone the game's button template — same reasoning as Back button.
        var go = CloneOrCreateButton(tmpl, "CollectionsStartBtn");
        if (go == null)
            return;
        // Reparent into InfoPanel so the VerticalLayoutGroup positions it
        // under the thumbnail with matching width.
        go.transform.SetParent(parent.transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = ThumbW;
        le.preferredHeight = 44f;
        SetButtonText(go, LocalizedText.Get("Start"));
        _startBtn = go.GetComponentInChildren<Button>() ?? go.GetComponent<Button>();
        if (_startBtn != null)
        {
            _startBtn.onClick.RemoveAllListeners();
            _startBtn.onClick.AddListener(() => DoPlay());
            var nav = _startBtn.navigation;
            nav.mode = Navigation.Mode.None;
            _startBtn.navigation = nav;
        }
        else
        {
            Plugin.Logger.LogError(
                "[CollectionsMenu] Start button: NO Button component found on clone!"
            );
        }
        _startBtnGo = go;
        go.SetActive(false);
    }

    private GameObject CloneOrCreateButton(GameObject tmpl, string name)
    {
        GameObject go;
        if (tmpl != null && tmpl)
        {
            Plugin.Logger.LogInfo(
                $"[CollectionsMenu] CloneOrCreateButton('{name}'): cloning template '{tmpl.name}'."
            );
            go = Instantiate(tmpl, transform, false);

            go.transform.localScale = Vector3.one;

            // Destroy text-localisation scripting (I2.Loc).
            foreach (var loc in go.GetComponentsInChildren<Localize>(true))
                DestroyImmediate(loc);

            // ── Replace cloned Buttons with fresh MenuButtons ──────
            // Unity 2017 IL2CPP: RemoveAllListeners() does NOT clear
            // persistent (serialised) onClick calls.  The cloned
            // PlayButton's persistent LevelSelectMenu2.PlayClick()
            // fires on click, NREs, and blocks our runtime listener.
            // Fix: DestroyImmediate wipes persistent state; then
            // create a fresh MenuButton, copying the original's visual
            // settings (colors, targetGraphic, label) for native look.
            var allBtns = go.GetComponentsInChildren<Button>(true);
            for (int i = allBtns.Length - 1; i >= 0; i--)
            {
                var old = allBtns[i];
                var savedGraphic = old.targetGraphic;
                var savedColors = old.colors;
                var savedLabel = old is MenuButton mb
                    ? mb.label
                    : old.GetComponentInChildren<TextMeshProUGUI>();
                DestroyImmediate(old);
                var newMb = go.AddComponent<MenuButton>();
                newMb.targetGraphic = savedGraphic;
                newMb.colors = savedColors;
                newMb.transition = Selectable.Transition.ColorTint;
                if (savedLabel != null)
                    newMb.SetLabel(savedLabel);
            }

            // 4.  Make sure images are enabled AND catch raycasts.
            //     Disable raycasts on text — it can steal clicks from the
            //     Button if it sits on top of the targetGraphic Image.
            foreach (var img in go.GetComponentsInChildren<Image>(true))
            {
                img.enabled = true;
                img.raycastTarget = true;
            }
            foreach (var raw in go.GetComponentsInChildren<RawImage>(true))
            {
                raw.enabled = true;
                raw.raycastTarget = true;
            }
            foreach (var tmp in go.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                tmp.raycastTarget = false;
            }

            // 4.  Reset CanvasGroup so the button is visible + interactive.
            var cg = go.GetComponent<CanvasGroup>();
            if (cg != null)
            {
                cg.alpha = 1f;
                cg.blocksRaycasts = true;
            }

            // 5.  Stretch each Button's targetGraphic to fill the root,
            //     and disable non-targetGraphic Images so raycasts
            //     only hit the targetGraphic.
            allBtns = go.GetComponentsInChildren<Button>(true);
            foreach (var b in allBtns)
            {
                if (b.targetGraphic != null && b.targetGraphic.raycastTarget)
                {
                    var tgRT = b.targetGraphic.rectTransform;
                    if (tgRT != null)
                    {
                        tgRT.anchorMin = Vector2.zero;
                        tgRT.anchorMax = Vector2.one;
                        tgRT.offsetMin = Vector2.zero;
                        tgRT.offsetMax = Vector2.zero;
                    }
                }
            }
            // Disable raycasts on non-targetGraphic Images so only
            // the targetGraphic catches clicks.
            var tgSet = new HashSet<Graphic>();
            foreach (var b in allBtns)
                if (b.targetGraphic != null)
                    tgSet.Add(b.targetGraphic);
            foreach (var img in go.GetComponentsInChildren<Image>(true))
                if (!tgSet.Contains(img))
                    img.raycastTarget = false;
            foreach (var raw in go.GetComponentsInChildren<RawImage>(true))
                if (!tgSet.Contains(raw))
                    raw.raycastTarget = false;
        }
        else
        {
            go = NewChild(name, gameObject);
            var img = go.AddComponent<Image>();
            img.sprite = GetDefaultSprite();
            img.color = new Color(1f, 1f, 1f, 0.85f);
            // Use MenuButton to match the game's native button styling (highlight/press colors etc.)
            var mb = go.AddComponent<MenuButton>();
            mb.targetGraphic = img;
            mb.transition = Selectable.Transition.ColorTint;
            var lb = NewChild("Label", go);
            var lrt = lb.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            var txt = lb.AddComponent<TextMeshProUGUI>();
            txt.fontSize = 30;
            txt.alignment = TextAlignmentOptions.Center;
            txt.color = new Color(0.19f, 0.19f, 0.19f, 1f);
            // Let MenuButton find its label
            mb.SetLabel(txt);
        }
        go.name = name;
        return go;
    }

    private static Sprite _defaultSprite;

    private static Sprite GetDefaultSprite()
    {
        if (_defaultSprite != null && _defaultSprite)
            return _defaultSprite;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        _defaultSprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f));
        _defaultSprite.hideFlags = HideFlags.HideAndDontSave;
        return _defaultSprite;
    }

    private static void EnsureMinSize(RectTransform rt, float minW, float minH)
    {
        var sd = rt.sizeDelta;
        if (sd.x < minW)
            sd.x = minW;
        if (sd.y < minH)
            sd.y = minH;
        rt.sizeDelta = sd;
    }

    private static void SetButtonText(GameObject go, string text)
    {
        var upper = (text ?? "").ToUpper();
        var tmp = go.GetComponentInChildren<TextMeshProUGUI>();
        if (tmp != null)
        {
            tmp.text = upper;
            UiFont.EnsureCjkFont(tmp);
            return;
        }
        var t = go.GetComponentInChildren<Text>();
        if (t != null)
            t.text = upper;
    }

    // ── Data / selection logic ────────────────────────────────────

    /// <summary>
    /// Wire explicit navigation so that right-arrow from any col item
    /// always targets the first lvl item, and left-arrow from any lvl
    /// item always targets the currently selected col item.
    /// </summary>
    private void SyncNavigation()
    {
        // ── Col items: up/down within list, right → first lvl ─────
        for (int i = 0; i < _colList.GetNumberItems; i++)
        {
            var btn = _colList.GetButton(i)?.GetComponent<Button>();
            if (btn == null)
                continue;
            var nav = btn.navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = i > 0 ? _colList.GetButton(i - 1)?.GetComponent<Button>() : null;
            nav.selectOnDown =
                i < _colList.GetNumberItems - 1
                    ? _colList.GetButton(i + 1)?.GetComponent<Button>()
                    : null;
            nav.selectOnLeft = null;
            nav.selectOnRight =
                _lvlList.GetNumberItems > 0 ? _lvlList.GetButton(0)?.GetComponent<Button>() : null;
            btn.navigation = nav;
        }

        // ── Lvl items: up/down within list, left → current col ────
        var curColBtn = _prevColItem?.GetComponent<Button>();
        for (int i = 0; i < _lvlList.GetNumberItems; i++)
        {
            var btn = _lvlList.GetButton(i)?.GetComponent<Button>();
            if (btn == null)
                continue;
            var nav = btn.navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = i > 0 ? _lvlList.GetButton(i - 1)?.GetComponent<Button>() : null;
            nav.selectOnDown =
                i < _lvlList.GetNumberItems - 1
                    ? _lvlList.GetButton(i + 1)?.GetComponent<Button>()
                    : null;
            nav.selectOnLeft = curColBtn;
            nav.selectOnRight = null;
            btn.navigation = nav;
        }
    }

    private void Rebuild()
    {
        _colData.Clear();
        _colData.AddRange(ConfigLoader.Collections);
        var names = new List<string>(_colData.Count);
        foreach (var c in _colData)
            names.Add(c.IsBroken ? "(!) " + c.Name : c.Name);
        _prevColItem = null;
        _prevLvlItem = null;
        _colList.Bind(names);
        _lvlList.Bind(new List<string>());
        _selCol = -1;
        _selLvl = -1;
    }

    private void Clear()
    {
        _prevColItem = null;
        _prevLvlItem = null;
        _colList?.Clear();
        _lvlList?.Clear();
        _colData.Clear();
        _selCol = -1;
        _selLvl = -1;
    }

    // ── ListView callbacks ────────────────────────────────────────

    private void OnColSelect(ListViewItem item)
    {
        var ci = item as CollectionListItem;
        if (ci == null)
            return;
        if (_prevColItem != null && _prevColItem != ci)
            _prevColItem.SetActive(false);
        ci.SetActive(true);
        // Focus indicator: remove > from old items in both lists
        _prevColItem?.SetFocusPrefix(false);
        _prevLvlItem?.SetFocusPrefix(false);
        ci.SetFocusPrefix(true);
        _prevColItem = ci;
        SyncNavigation();
        SelectCollection(item.index);
    }

    private void OnColDeSelect(ListViewItem item)
    {
        // Background is managed by OnColSelect._prevColItem.SetActive(false).
        // EventSystem-driven deselection (mouse leave, keyboard focus change)
        // must NOT clear the selection background.
    }

    private void OnColSubmit(ListViewItem item)
    {
        if (_lvlList.GetNumberItems > 0)
            _lvlList.FocusItem(0);
    }

    private void OnColPointerClick(
        ListViewItem item,
        int clickCount,
        PointerEventData.InputButton button
    )
    {
        // Single click: select (in case hover didn't fire onSelect)
        if (clickCount == 1)
            OnColSelect(item);
        // Double click: move focus to level list
        else if (clickCount > 1 && _lvlList.GetNumberItems > 0)
            _lvlList.FocusItem(0);
    }

    private void OnLvlSelect(ListViewItem item)
    {
        var ci = item as CollectionListItem;
        if (ci == null)
            return;
        if (_prevLvlItem != null && _prevLvlItem != ci)
            _prevLvlItem.SetActive(false);
        ci.SetActive(true);
        // Focus indicator: remove > from old items in both lists
        _prevLvlItem?.SetFocusPrefix(false);
        _prevColItem?.SetFocusPrefix(false);
        ci.SetFocusPrefix(true);
        _prevLvlItem = ci;
        SelectLevel(item.index);
    }

    private void OnLvlDeSelect(ListViewItem item)
    {
        // Background is managed by OnLvlSelect._prevLvlItem.SetActive(false).
        // EventSystem-driven deselection must NOT clear the background.
    }

    private void OnLvlSubmit(ListViewItem item)
    {
        DoPlay();
    }

    private void OnLvlPointerClick(
        ListViewItem item,
        int clickCount,
        PointerEventData.InputButton button
    )
    {
        // Single click: select (in case hover didn't fire onSelect)
        if (clickCount == 1)
            OnLvlSelect(item);
        // Double click: start game
        else if (clickCount > 1)
            DoPlay();
    }

    // ── Selection logic ───────────────────────────────────────────

    private void SelectCollection(int i)
    {
        if (i < 0 || i >= _colData.Count)
            return;
        if (_selCol == i)
            return;
        SetCollection(i);
    }

    /// <summary>
    /// Apply collection <paramref name="i"/>: rebuild the level list and the
    /// info panel.  Used by <see cref="SelectCollection"/> on selection change
    /// and by <see cref="RefreshLevelNames"/> on language change (same
    /// selection, so the caller bypasses SelectCollection's early-return).
    /// </summary>
    private void SetCollection(int i)
    {
        _selCol = i;

        var col = _colData[i];

        // Broken collections show an error message instead of level list.
        if (col.IsBroken)
        {
            _prevLvlItem = null;
            _selLvl = -1;
            _lvlList.Bind(new List<string> { "Broken collection! Check your config file" });
            ClearInfo();
            if (_startBtnGo)
                _startBtnGo.SetActive(false);
            SyncNavigation();
            return;
        }

        var rawIds = col.Levels ?? new List<string>();
        var names = new List<string>(rawIds.Count);
        foreach (var id in rawIds)
        {
            // Localized display name for the CURRENT game language; the
            // config file still stores the raw LevelId.
            string title = CollectionManager.GetLocalizedLevelName(id);

            bool isMissing;
            if (!CollectionManager.ValidateLevelId(id, out isMissing))
            {
                string prefix = isMissing ? "(missing) " : "(!) ";
                title = prefix + title;
            }
            names.Add(title);
        }

        _prevLvlItem = null;
        _selLvl = -1; // force ShowInfo on first level of new collection
        _lvlList.Bind(names);
        if (names.Count > 0)
        {
            _lvlList.FocusItem(0);
            // Apply background manually (FocusItem doesn't call onSelect)
            // and track for correct deselection on next select.
            var firstLvl = _lvlList.GetButton(0)?.GetComponent<CollectionListItem>();
            if (firstLvl != null)
            {
                if (_prevLvlItem != null && _prevLvlItem != firstLvl)
                    _prevLvlItem.SetActive(false);
                firstLvl.SetActive(true);
                _prevLvlItem = firstLvl;
            }
            SelectLevel(0);
        }
        else
            ClearInfo();
        if (_startBtnGo)
            _startBtnGo.SetActive(names.Count > 0);
        SyncNavigation();
    }

    /// <summary>
    /// Re-apply the current game language to the level list and the info
    /// panel without resetting the selection.  Called from the localisation
    /// refresh when the player changes language in Options → Language.
    /// </summary>
    private void RefreshLevelNames()
    {
        if (_selCol < 0 || _selCol >= _colData.Count)
            return;
        var col = _colData[_selCol];
        if (col.IsBroken)
            return;

        int lvl = _selLvl;
        SetCollection(_selCol); // bypasses SelectCollection's "same index" guard
        if (lvl > 0 && lvl < _lvlList.GetNumberItems)
        {
            _lvlList.FocusItem(lvl);
            // FocusItem uses EventSystem, which doesn't call onSelect
            // — restore the highlight manually (mirrors SetCollection).
            var item = _lvlList.GetButton(lvl)?.GetComponent<CollectionListItem>();
            if (item != null)
            {
                if (_prevLvlItem != null && _prevLvlItem != item)
                    _prevLvlItem.SetActive(false);
                item.SetActive(true);
                _prevLvlItem = item;
            }
            SelectLevel(lvl);
        }
    }

    private void SelectLevel(int i)
    {
        var col = (_selCol >= 0 && _selCol < _colData.Count) ? _colData[_selCol] : null;
        if (col?.Levels == null || i < 0 || i >= col.Levels.Count)
            return;
        if (_selLvl == i)
            return;
        _selLvl = i;
        ShowInfo(col.Levels[i]);
    }

    private void ShowInfo(string levelId)
    {
        if (_titleText != null)
        {
            _titleText.enableAutoSizing = false;
            _titleText.fontSize = 30;
            // Localized name for the current game language (BuiltIn / EditorPick
            // from the game's LEVEL/ term, workshop from its metadata title).
            _titleText.text = CollectionManager.GetLocalizedLevelName(levelId);
            UiFont.EnsureCjkFont(_titleText);
        }

        Texture2D tex = null;
        var type = CollectionManager.ResolveLevelType(levelId);

        // BuiltIn and EditorPick both use HFFResources.LevelImages sprites
        if (
            (type == WorkshopItemSource.BuiltIn || type == WorkshopItemSource.EditorPick)
            && HFFResources.instance != null
        )
        {
            tex = HFFResources.instance.FindTextureResource("LevelImages/" + levelId);
        }

        // Workshop metadata provides the thumbnail for subscription/local
        // levels.  The title is handled by GetLocalizedLevelName above — for
        // BuiltIn / EditorPick it returns the game-localised name; for
        // workshop levels the author-provided metadata title.
        var meta = CollectionManager.ResolveWorkshopMetadata(levelId);
        if (meta != null)
        {
            // Subscription/LocalWorkshop thumbnails come from metadata
            if (type != WorkshopItemSource.BuiltIn && type != WorkshopItemSource.EditorPick)
                tex = meta.thumbnailTexture;
        }
        if (tex != null)
        {
            _thumbnail.texture = tex;
            _thumbnail.enabled = true;
            _thumbnail.color = Color.white;
            var tnRT = _thumbnail.rectTransform;
            if (tnRT != null)
            {
                float w = tex.width,
                    h = tex.height;
                float rw = tnRT.rect.width,
                    rh = tnRT.rect.height;
                float sx = 1f,
                    sy = 1f;
                if (w / rw > h / rh)
                    sx = h / rh / (w / rw);
                else
                    sy = w / rw / (h / rh);
                _thumbnail.uvRect = new Rect(0.5f - sx / 2f, 0.5f - sy / 2f, sx, sy);
            }
        }
        else
        {
            _thumbnail.texture = null;
            _thumbnail.enabled = false;
        }
    }

    private void ClearInfo()
    {
        if (_titleText != null)
            _titleText.text = "";
        if (_thumbnail != null)
        {
            _thumbnail.texture = null;
            _thumbnail.enabled = false;
        }
    }

    private void DoRefresh()
    {
        Plugin.Logger.LogInfo("[CollectionsMenu] Refresh: reloading config...");
        ConfigLoader.Reload();
        Rebuild();
        if (_colData.Count > 0)
        {
            _colList.FocusItem(0);
            var firstCol = _colList.GetButton(0)?.GetComponent<CollectionListItem>();
            if (firstCol != null)
            {
                if (_prevColItem != null && _prevColItem != firstCol)
                    _prevColItem.SetActive(false);
                firstCol.SetActive(true);
                firstCol.SetFocusPrefix(true);
                _prevColItem = firstCol;
            }
            SelectCollection(0);
            SyncNavigation();
        }
        else
        {
            _lvlList.Bind(new List<string>());
            ClearInfo();
            if (_startBtnGo)
                _startBtnGo.SetActive(false);
        }
    }

    private void DoPlay()
    {
        Plugin.Logger.LogInfo(
            $"[CollectionsMenu] DoPlay() called. selCol={_selCol} selLvl={_selLvl}"
        );
        if (_selCol < 0 || _selCol >= _colData.Count)
            return;
        var col = _colData[_selCol];
        if (col.IsBroken)
            return;
        if (col.Levels == null || col.Levels.Count == 0)
            return;

        // Scan every level — if any one is invalid the whole
        // collection is blocked (missing workshop, bad name, etc.).
        foreach (var id in col.Levels)
        {
            bool isMissing;
            if (!CollectionManager.ValidateLevelId(id, out isMissing))
            {
                Plugin.Logger.LogWarning(
                    $"[CollectionsMenu] DoPlay blocked: level '{id}' is invalid.");
                return;
            }
        }

        // Save indices before FadeOutForward — OnLostFocus → Clear() resets them to -1
        int colIdx = _selCol,
            lvlIdx = _selLvl;
        FadeOutForward();
        CollectionManager.Instance?.StartCollectionRun(colIdx, lvlIdx >= 0 ? lvlIdx : 0);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static TextMeshProUGUI AddHeader(string text, GameObject parent)
    {
        var go = NewChild("Header", parent);
        go.AddComponent<LayoutElement>().preferredHeight = 28f;
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 30;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        UiFont.EnsureCjkFont(tmp);
        return tmp;
    }

    private static GameObject NewChild(string name, GameObject parent = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        if (parent != null)
            go.transform.SetParent(parent.transform, false);
        else
            go.transform.SetParent(null, false);
        return go;
    }
}
