using System;
using System.Collections;
using BepInEx;
using BepInEx.Logging;
using I2.Loc;
using Multiplayer;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TwilightCore.Chat;
using TwilightCore.Match;
using TwilightCore.Net;
using TwilightCore.Ready;
using TwilightCore.Timer;

namespace TwilightCore;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;

    internal static class PluginInfo
    {
        public const string PLUGIN_GUID = "TwilightCore";
        public const string PLUGIN_NAME = "TwilightCore";
        public const string PLUGIN_VERSION = "0.1.0";
    }

    private void Awake()
    {
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} {PluginInfo.PLUGIN_VERSION} is loaded!");

        // ── LevelCollections subsystem (ported) ──────────────────────
        ConfigLoader.Load();

        var mgr = new GameObject("CollectionManager");
        DontDestroyOnLoad(mgr);
        mgr.AddComponent<CollectionManager>();

        ConsoleCommands.Register();
        HarmonyPatches.Apply();

        var boot = new GameObject("TwilightCoreBootstrapper");
        DontDestroyOnLoad(boot);
        boot.AddComponent<Bootstrapper>();

        // ── Twilight match subsystem ────────────────────────────────
        TwilightConfig.Init(Config);

        // TLS bypass: Human Fall Flat ships an old Unity/Mono whose CA store
        // doesn't trust modern certs (Let's Encrypt etc.), so UnityWebRequest's
        // REST login fails the TLS handshake with a generic "network error" and
        // no HTTP status. The WebSocket client already accepts any cert via its
        // own SslStream; this mirrors that for the REST path by routing Mono's
        // validation through ServicePointManager (the only hook available on a
        // Unity this old — there's no UnityWebRequest.certificateHandler yet).
        // Idempotent: only installs once, and only when TLS is on.
        if (TwilightConfig.UseTLS.Value) InstallTrustAllCertsForRest();

        // Background→main-thread marshal for WebSocket callbacks.
        var dispatcher = new GameObject("TwilightDispatcher");
        DontDestroyOnLoad(dispatcher);
        dispatcher.AddComponent<MainThreadDispatcher>();

        // Chat surface: drives the built-in NetChat panel (unlocked for single-player)
        // and falls back to an OnGUI panel when NetChat is unavailable.
        var chatGo = new GameObject("TwilightChatView");
        DontDestroyOnLoad(chatGo);
        var chatView = chatGo.AddComponent<ChatView>();

        // Collection info HUD (top-right, two lines, single colour) during runs.
        var hudGo = new GameObject("TwilightCollectionInfoHud");
        DontDestroyOnLoad(hudGo);
        hudGo.AddComponent<Hud.CollectionInfoHud>();

        // Networking: REST login → WebSocket (heartbeat / reconnect handled inside).
        var netGo = new GameObject("TwilightClient");
        DontDestroyOnLoad(netGo);
        var client = netGo.AddComponent<TwilightClient>();

        // Held-scene preloader: additively holds the announced MULTI pick's first
        // level during PREP (dormant) and swaps it in at round_start instead of a
        // full launch (激进预载held-scene方案调研.md M1+M2). Idle without a
        // pick_announced-capable server; `twi preload …` exercises it manually.
        var preloadGo = new GameObject("TwilightPreload");
        DontDestroyOnLoad(preloadGo);
        preloadGo.AddComponent<Preload.ScenePreloadManager>().Init(client);

        // Match state + round reporter + controller. The reporter is resolved
        // lazily by the controller: a registered timer provider (real timer
        // plugin, self-registered via TimerProviderRegistry — necessarily AFTER
        // our Awake, since the dependency chain loads us first) takes precedence
        // over this fallback (simulated timer / no-op) — ITimerProvider接口需求.md §1.2.
        var session = new MatchSession();
        MatchSession.Instance = session;
        IRoundReporter fallback = TwilightConfig.EnableSimTimer.Value
            ? new SimulatedTimer(client)
            : NullRoundReporter.Instance;
        var match = new MatchController(client, session, chatView, fallback);
        chatView.OnOutgoing = match.SendChat;

        // Patches: false-start lock (chat is a standalone OnGUI console, no NetChat patching).
        ReadyLockPatches.Apply();

        // Menu fall-speed limiter: while connected to the match server, clamp the
        // menu ragdoll's downward speed (anti-fall off the menu scenery).
        var fallLimiter = new GameObject("TwilightMenuFallLimiter");
        DontDestroyOnLoad(fallLimiter);
        fallLimiter.AddComponent<Physics.MenuFallSpeedLimiter>();

        // Console commands (twi connect <host> [port] / disconnect / status / probe / sim / finish …).
        TwilightCommands.Init(client, session, fallback as SimulatedTimer, match);

        // No auto-connect: the player runs `twi connect <host> [port]` in the console.
    }

    /// <summary>
    /// Install a ServerCertificateValidationCallback that accepts any certificate.
    /// Required so REST login (UnityWebRequest → Mono) can complete the TLS handshake
    /// against the public nginx endpoint, whose cert the game's stale CA store won't
    /// trust. Idempotent across reloads; the WebSocket client does its own bypass.
    /// </summary>
    private static void InstallTrustAllCertsForRest()
    {
        try
        {
            System.Net.ServicePointManager.ServerCertificateValidationCallback =
                (_sender, _cert, _chain, _errors) => true;
            Logger.LogInfo("[Twilight] TLS: accepting any server certificate for REST login (legacy Unity/Mono CA store).");
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[Twilight] TLS bypass failed to install: " + ex.Message);
        }
    }
}

/// <summary>
/// Injects the Collections menu + button into the game's level-select menu,
/// exactly as the original LevelCollections plugin did. Behaviour is unchanged:
/// the button is hidden in multiplayer lobbies (collections don't work over the
/// network) and re-injected when returning to single-player.
/// </summary>
internal class Bootstrapper : MonoBehaviour
{
    private bool _menuInjected;
    private Button _collectionsButton;
    private bool _buttonFailed;
    private Action _collectionsLabelRefresh;

    private void Start()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        StartCoroutine(InjectLoop());
    }
    private void OnDestroy() => SceneManager.sceneLoaded -= OnSceneLoaded;

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // IMPORTANT: MenuSystem and the pages it instantiates (LevelSelectMenu2,
        // …) are persistent across scene loads — going in and out of a level
        // does NOT destroy the CollectionsMenu or the Collections button.
        // Clearing _collectionsButton here used to make InjectLoop inject a
        // fresh button on every scene load while the old button lingered on
        // the persistent LevelSelectMenu2, leaking one button (with all its
        // Graphics) per scene transition — menu framerate dropped over time.
        // ButtonAlive() already handles the fake-null case (button really
        // destroyed → re-injected), so we only release the localisation
        // refresher when the button is actually gone.
        if (!_collectionsButton && _collectionsLabelRefresh != null)
        {
            LocalizedText.Unregister(_collectionsLabelRefresh);
            _collectionsLabelRefresh = null;
        }
        _menuInjected = false;
        _buttonFailed = false;
    }

    private static readonly WaitForSeconds _pollInterval = new WaitForSeconds(1f);

    private IEnumerator InjectLoop()
    {
        while (true)
        {
            if (MenuSystem.instance != null)
            {
                if (IsMultiplayerMode())
                {
                    // Collections don't work in multiplayer — using them causes a soft-lock.
                    // Destroy known button and also sweep for orphaned clones.
                    if (ButtonAlive())
                    {
                        Destroy(_collectionsButton.gameObject);
                        _collectionsButton = null;
                    }
                    if (_collectionsLabelRefresh != null)
                    {
                        LocalizedText.Unregister(_collectionsLabelRefresh);
                        _collectionsLabelRefresh = null;
                    }
                    SweepCollectionsButtons();
                }
                else
                {
                    if (!_menuInjected) InjectCollectionsMenu();
                    if (_menuInjected && !ButtonAlive() && !_buttonFailed) InjectCollectionsButton();
                }
            }
            yield return _pollInterval;
        }
    }

    private static bool IsMultiplayerMode()
    {
        // NetGame.isNetStarted covers most cases (hosted/joined a room).
        // LevelSelectMenu2.displayMode covers the edge case where the
        // menu has been set up for lobbies but isNetStarted hasn't flipped yet.
        if (NetGame.isNetStarted)
            return true;
        var mode = LevelSelectMenu2.displayMode;
        return mode == LevelSelectMenuMode.BuiltInLobbies
            || mode == LevelSelectMenuMode.WorkshopLobbies;
    }

    /// <summary>
    /// Destroy every "CollectionsTitle" GameObject under every
    /// LevelSelectMenu2 instance. Used to clean up orphaned buttons
    /// (e.g. accumulated by older versions that re-injected on every
    /// scene load) and to remove the button in multiplayer modes.
    /// </summary>
    private static void SweepCollectionsButtons()
    {
        if (MenuSystem.instance == null)
            return;
        var all = MenuSystem.instance.GetComponentsInChildren<LevelSelectMenu2>(includeInactive: true);
        if (all == null)
            return;
        foreach (var lsm2 in all)
        {
            if (lsm2 == null)
                continue;
            // Button lives under topPanel, not directly under lsm2 — search recursively.
            foreach (Transform child in lsm2.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                if (child != null && child.name == "CollectionsTitle")
                {
                    Destroy(child.gameObject);
                    Plugin.Logger.LogInfo("LevelCollections: swept orphan CollectionsTitle button.");
                }
            }
        }
    }

    // ── Menu ──────────────────────────────────────────────────────

    private void InjectCollectionsMenu()
    {
        var ex = MenuSystem.instance.GetComponentInChildren<CollectionsMenu>(includeInactive: true);
        if (ex != null) { _menuInjected = true; return; }
        var go = new GameObject("CollectionsMenu");
        go.SetActive(false); // deactivate BEFORE AddComponent so OnEnable won't fire yet
        go.transform.SetParent(MenuSystem.instance.transform, false);
        go.AddComponent<CollectionsMenu>(); // only Awake runs; OnEnable waits for real transition
        _menuInjected = true;
        Plugin.Logger.LogInfo("CollectionsMenu injected into MenuSystem.");
    }

    // ── Button ────────────────────────────────────────────────────

    private bool ButtonAlive() => _collectionsButton != null && _collectionsButton;

    private void InjectCollectionsButton()
    {
        // Never inject the Collections button in a multiplayer lobby —
        // collection runs don't work over the network and cause a soft-lock.
        if (IsMultiplayerMode())
        {
            Plugin.Logger.LogInfo("LevelCollections: skipping button injection (multiplayer).");
            return;
        }

        // Self-heal: sweep any orphaned "CollectionsTitle" buttons left behind
        // (e.g. accumulated by older versions that re-injected on every scene
        // load). Only one button may ever exist. Reaching this method means
        // ButtonAlive() was false, so no live reference is swept. Also drop a
        // stale localisation refresher (it pointed at a swept/dead button) so
        // Refreshers never grows.
        _collectionsButton = null;
        if (_collectionsLabelRefresh != null)
        {
            LocalizedText.Unregister(_collectionsLabelRefresh);
            _collectionsLabelRefresh = null;
        }
        SweepCollectionsButtons();

        var all = MenuSystem.instance.GetComponentsInChildren<LevelSelectMenu2>(includeInactive: true);
        if (all == null || all.Length == 0) return;
        var lsm2 = all[0];

        Plugin.Logger.LogInfo("LevelCollections: injecting Collections button...");

        // Clone from an actual Button (not a text label like TitleCampaign)
        GameObject refBtn = FirstValid(
            lsm2.showCustomButton, lsm2.showSubscribedButton,
            lsm2.ShowSubscribedLevelButton, lsm2.ShowLocalLevelButton, lsm2.PlayButton);
        if (refBtn == null || !refBtn) { Plugin.Logger.LogWarning("LevelCollections: no reference button."); return; }
        Plugin.Logger.LogInfo("LevelCollections: Collections Button using " + refBtn.gameObject.ToString());

        // Parent under topPanel (AutoNavigation) so the button lives in the tab bar
        Transform parent;
        if (lsm2.topPanel != null && lsm2.topPanel)
            parent = lsm2.topPanel.transform;
        else
            parent = refBtn.transform.parent;
        if (parent == null) { Plugin.Logger.LogWarning("LevelCollections: no parent."); return; }

        var go = Instantiate(refBtn, parent, false);
        go.name = "CollectionsTitle";

        // The cloned tab button carries the original's Localize component,
        // which overwrites our label (and may inject button-icon glyphs)
        // on every language change — destroy it, same as
        // CollectionsMenu.CloneOrCreateButton does for its clones.
        foreach (var loc in go.GetComponentsInChildren<Localize>(true))
            DestroyImmediate(loc);

        // Nudge away from screen edges (keep original anchor/pivot — changing them
        // breaks the button's internal layout).
        var rt = go.GetComponent<RectTransform>();
        if (rt != null)
            rt.anchoredPosition += new Vector2(-8f, -8f);

        go.transform.SetAsFirstSibling(); // leftmost in AutoNavigation order
        go.SetActive(true);

        var lbl = go.GetComponentInChildren<TextMeshProUGUI>();
        if (lbl != null)
        {
            var tmp = lbl;
            _collectionsLabelRefresh = () =>
            {
                if (tmp != null && tmp)
                {
                    tmp.text = LocalizedText.Get("Collections").ToUpper();
                    UiFont.EnsureCjkFont(tmp);
                }
            };
            LocalizedText.Register(_collectionsLabelRefresh);
        }
        else
        {
            var t = go.GetComponentInChildren<Text>();
            if (t != null)
            {
                var legacy = t;
                _collectionsLabelRefresh = () =>
                {
                    if (legacy != null && legacy)
                        legacy.text = LocalizedText.Get("Collections").ToUpper();
                };
                LocalizedText.Register(_collectionsLabelRefresh);
            }
        }

        var btn = go.GetComponentInChildren<Button>();
        if (btn != null)
        {
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() =>
            {
                Plugin.Logger.LogInfo("Collections button clicked.");
                if (MenuSystem.instance != null &&
                    MenuSystem.instance.GetComponentInChildren<CollectionsMenu>(includeInactive: true) != null)
                    lsm2.TransitionForward<CollectionsMenu>();
                else
                    Plugin.Logger.LogError("CollectionsMenu missing.");
            });
            _collectionsButton = btn;
            Plugin.Logger.LogInfo("LevelCollections: Collections button injected.");
        }
        else
        {
            Plugin.Logger.LogWarning("LevelCollections: no Button on clone.");
            if (_collectionsLabelRefresh != null)
            {
                LocalizedText.Unregister(_collectionsLabelRefresh);
                _collectionsLabelRefresh = null;
            }
            Destroy(go);
            _buttonFailed = true;
        }
    }

    private static GameObject FirstValid(params GameObject[] cands)
    {
        foreach (var g in cands) if (g != null && g) return g;
        return null;
    }
}
