using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HumanAPI;
using Multiplayer;
using TwilightCore.Match;
using TwilightCore.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwilightCore.Preload;

/// <summary>
/// Held-scene preloader (激进预载held-scene方案调研.md §3, M1+M2+M3): during PREP,
/// after this player's <c>!ready</c> and once the referee's <c>pick_announced</c>
/// carries a MULTI collection, the first level's scene is loaded ADDITIVELY and
/// put to sleep (all root objects deactivated). At <c>round_start</c>,
/// <see cref="CollectionManager.LaunchLevel"/> diverts into
/// <see cref="SwapInSequence"/> instead of the standard launch — the countdown
/// ends with the player already in the level.
///
/// <para>M3 chaining (<see cref="TwilightConfig.EnableChainedPreload"/>): while a
/// collection run is in <c>PlayingLevel</c>, <see cref="Update"/> idempotently
/// holds the NEXT level dormant the same way; the level advance reuses the same
/// <c>LaunchLevel → TrySwapIn</c> path. Chained holds never report to the server
/// (<c>preload_report</c> belongs to the round-start gate only), adjacent same
/// levels never preload (the Empty-dwell transition owns them), and the driver
/// self-heals: any hold destroyed by an external Single load is simply re-held
/// on a later frame once the gates pass again.</para>
///
/// <para>SINGLE picks never preload (report <c>na</c>); preloading is an
/// optimisation, never a correctness dependency — any failure falls back to the
/// standard launch path, and without a server <c>pick_announced</c> this manager
/// stays completely idle (the chained driver only needs a local collection run,
/// no server required).</para>
///
/// <para>All entry points (<see cref="OnPickAnnounced"/>,
/// <see cref="OnReadyOrPhaseChanged"/>) are pushed from
/// <see cref="TwilightCore.Match.MatchController.Handle"/> on the main thread;
/// coroutines and scene hooks run on the main thread as well.</para>
/// </summary>
internal sealed class ScenePreloadManager : MonoBehaviour
{
    public static ScenePreloadManager Instance { get; private set; }

    private TwilightClient _client;
    private bool _hooked;

    private HeldScene _held;
    private HeldScene _lastConsumed;   // status display only
    private Coroutine _pipeline;

    // True while a swap-in coroutine is mid-flight (up to and including its
    // trailing outgoing-scene unload). The chained driver waits it out rather
    // than starting an additive load under a scene teardown.
    private bool _swapRunning;

    // Scene-load plumbing: the sceneLoaded hook captures + dormifies the scene
    // (before it ever renders); the pipeline makes the keep/discard decision
    // once its op completes.
    private string _awaitingSceneName;
    private Scene _pendingLoadedScene;
    private bool _gotPendingScene;

    // Last held-scene unload, so a follow-up preload of the SAME scene name can
    // wait for it (Unity 2017 queues scene ops, but same-name load/unload
    // racing is flaky — a pick change to a collection sharing the first level
    // hits exactly this).
    private AsyncOperation _lastUnloadOp;
    private string _lastUnloadName;

    // Drop requests are observed by the pipeline rather than acted on by
    // StopCoroutine — an in-flight additive LoadSceneAsync cannot be cancelled
    // on Unity 2017.4, so the pipeline waits it out and unloads the result.
    private bool _dropRequested;

    // Set when the gates want a preload start but the previous pipeline is still
    // winding down (drop requested, e.g. a changed pick_announced); the pipeline
    // consumes it on exit. Without this, the new pick's preload would never start.
    private bool _pendingStart;

    private MatchPhase _lastPhase = MatchPhase.Idle;

    // Latest announced pick (PREVIEW only — round_start stays authoritative).
    private PickSnapshot _announced;
    private string _announcedFirstLevel;
    private string _announcedKey;
    private bool _doneReported;   // re-arm: re-report done after un-ready→ready cycles (server resets state)

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_hooked)
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }
    }

    /// <summary>Wire the client used for preload_report and subscribe the scene hooks.</summary>
    public void Init(TwilightClient client)
    {
        _client = client;
        if (!_hooked)
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            _hooked = true;
        }
    }

    // ── Match-flow entry points (main thread, from MatchController) ─────────

    /// <summary>
    /// A <c>pick_announced</c> arrived (PREP preview of the upcoming round).
    /// Replaces any previous announcement; a changed pick discards the old hold.
    /// </summary>
    public void OnPickAnnounced(Dictionary<string, object> pickDict, Dictionary<string, object> collectionDict)
    {
        var pick = PickSnapshot.From(pickDict);
        var levels = RoundIngestion.ResolveLevels(collectionDict);
        string first = levels != null && levels.Count > 0 && levels[0] != null
            ? CollectionManager.CanonicalizeLevelId(levels[0].ToString())
            : null;

        string key = (pick.Code ?? "") + "|" + (first ?? "");
        bool unchanged = _announcedKey == key;
        _announced = pick;
        _announcedFirstLevel = first;
        _announcedKey = key;
        _doneReported = false;

        Plugin.Logger.LogInfo($"[Preload] pick_announced: {pick.Code} - {pick.Name} type={pick.Type} first='{first}'");

        if (pick.Type != PickType.Multi || !TwilightConfig.EnableScenePreload.Value)
        {
            // Scene preloading never applies (SINGLE pick / feature disabled) —
            // report na so the server-side start gate passes immediately.
            ForceDrop("pick_announced: not a preloadable pick");
            Report("na");
            return;
        }

        if (unchanged)
        {
            // Referee re-applied the same pick: the server reset our preload
            // state, so re-report; keep an existing (or in-flight) hold.
            if (IsHeldDormant)
            {
                Report("done");
                _doneReported = true;
                return;
            }
            if (_pipeline != null) return;
        }

        ForceDrop("pick_announced: pick changed");
        MaybeStartPreload();
    }

    /// <summary>
    /// Something about readiness/phase changed (ready_state or phase_change) —
    /// re-evaluate the preload gates.
    /// </summary>
    public void OnReadyOrPhaseChanged()
    {
        var session = MatchSession.Instance;
        if (session == null) return;

        // un-ready re-arms the done report (server reset our state on the abort);
        // so does a countdown aborted back to PREP (R2.3.3 resets both seats there)
        if (session.Phase == MatchPhase.Prep && (!session.MyReady || _lastPhase == MatchPhase.Countdown))
            _doneReported = false;
        _lastPhase = session.Phase;

        // Leaving PREP for anything other than COUNTDOWN means any hold is stale
        // (round_start's own flip to InRound doesn't pass through here — it
        // consumes the hold via TrySwapIn before the phase_change arrives).
        if (_held != null && session.IsAuthenticated &&
            session.Phase != MatchPhase.Prep && session.Phase != MatchPhase.Countdown)
        {
            ForceDrop("phase left PREP/COUNTDOWN: " + session.Phase);
            return;
        }

        MaybeStartPreload();
    }

    /// <summary>
    /// Idempotent gate check + start. Preload begins at the player's own
    /// <c>!ready</c> (ready-lock then prevents manual launches that would destroy
    /// additive scenes) and only from the main menu, single-player.
    /// </summary>
    private void MaybeStartPreload()
    {
        var session = MatchSession.Instance;
        if (session == null || !session.IsAuthenticated || !session.IsPlayerSeat) return;
        if (!TwilightConfig.EnableScenePreload.Value) return;
        if (_announced == null || _announced.Type != PickType.Multi) return;
        if (string.IsNullOrEmpty(_announcedFirstLevel)) return;
        if (session.Phase != MatchPhase.Prep || !session.MyReady) return;

        // Preload only from the main menu, single-player: an additive load while
        // a level is running would hijack Game.currentLevel through the dormant
        // Level's Awake (the player can !ready from inside a practice level via
        // the chat console — returning to the menu re-triggers this check).
        if (App.state != AppSate.Menu || !NetGame.isLocal) return;

        if (_pipeline != null || _dropRequested)
        {
            _pendingStart = true;   // previous pipeline still winding down — start when it exits
            return;
        }

        if (IsHeldDormant)
        {
            if (!_doneReported)
            {
                Report("done");
                _doneReported = true;
            }
            return;
        }

        Plugin.Logger.LogInfo($"[Preload] starting preload of '{_announcedFirstLevel}' (PREP, readied, {session.Seat}).");
        _pipeline = StartCoroutine(PreloadPipeline(_announcedFirstLevel, matchDriven: true));
    }

    private bool IsHeldDormant =>
        _held != null && _held.State == HeldSceneState.Dormant && _held.Scene.IsValid();

    /// <summary>Still meaningful to finish this preload? (round may have been force-started.)</summary>
    private static bool PreloadStillWanted()
    {
        var s = MatchSession.Instance;
        return s != null && s.IsAuthenticated && s.MyReady
            && (s.Phase == MatchPhase.Prep || s.Phase == MatchPhase.Countdown);
    }

    /// <summary>
    /// Still meaningful to finish THIS hold? Match-driven holds live inside the
    /// PREP/COUNTDOWN window; chained holds live while the run is playing a
    /// level and still expects exactly this level next; debug holds only die
    /// by explicit drop.
    /// </summary>
    private static bool HoldStillWanted(HeldScene held)
    {
        if (held.Chained)
        {
            var mgr = CollectionManager.Instance;
            var game = Game.instance;
            if (mgr == null || !mgr.IsInCollectionRun) return false;
            if (game == null || game.state != GameState.PlayingLevel) return false;
            string next = mgr.NextLevelId;
            return next != null && string.Equals(
                CollectionManager.CanonicalizeLevelId(next), held.LevelId, StringComparison.Ordinal);
        }
        if (!held.MatchDriven) return true;
        return PreloadStillWanted();
    }

    // ── M3 chained driver ────────────────────────────────────────────────────

    /// <summary>
    /// Idempotent chained-preload driver: while a collection run is playing a
    /// level, hold the NEXT level dormant so the advance swaps it in. Runs every
    /// frame; every guard is re-checked so the chain self-heals after
    /// standard-path fallbacks, skips, restarts or any external load that
    /// destroyed the hold. Adjacent same levels never preload — the
    /// SameLevelTransition Empty-dwell path owns them (its Single 'Empty' load
    /// would destroy the hold anyway).
    /// </summary>
    private void Update()
    {
        if (!TwilightConfig.EnableChainedPreload.Value) return;
        if (!EnableScenePreloadOn) return;
        if (_pipeline != null || _dropRequested) return;
        if (_swapRunning) return;   // a swap is mid-flight (incl. its trailing unload) — don't start a load under it

        // Don't race an outgoing-scene unload from a previous swap/drop — let
        // it finish first (same-name load/unload racing on Unity 2017 is flaky,
        // and mixed same-frame ops warn).
        if (_lastUnloadOp != null && !_lastUnloadOp.isDone) return;

        var mgr = CollectionManager.Instance;
        if (mgr == null || !mgr.IsInCollectionRun) return;
        var game = Game.instance;
        if (game == null || game.state != GameState.PlayingLevel) return;

        string next = mgr.NextLevelId;
        if (string.IsNullOrEmpty(next)) return;   // last level of the run
        next = CollectionManager.CanonicalizeLevelId(next);
        string current = CollectionManager.CanonicalizeLevelId(mgr.CurrentLevelId);
        if (string.Equals(next, current, StringComparison.Ordinal)) return;   // adjacent same level: Empty-dwell owns it

        if (IsHeldDormant)
        {
            if (string.Equals(_held.LevelId, next, StringComparison.Ordinal)) return;   // already holding the right scene
            // The run's next level moved under us (lc skip / restart / server
            // restart) — drop the stale hold; next frame re-holds.
            ForceDrop("chained: next level changed");
            return;
        }
        if (_held != null) return;   // non-dormant hold in flight (pipeline guard above covers its coroutine)

        Plugin.Logger.LogInfo($"[Preload] chained hold: '{next}' (next of '{mgr.CurrentLevelId}', level {mgr.CurrentLevelIndex + 1}/{mgr.CurrentCollection.Levels.Count}).");
        _pipeline = StartCoroutine(PreloadPipeline(next, matchDriven: false, chained: true));
    }

    private static bool EnableScenePreloadOn => TwilightConfig.EnableScenePreload.Value;

    // ── P-phase pipeline (调研 §3.1) ─────────────────────────────────────────

    /// <summary>
    /// Serial preload: resolve type/scene name → (workshop) ensure downloaded →
    /// additive scene load → dormify (+ lighting freeze &amp; RS probe).
    /// <seealso cref="LevelRepository"/> has single-slot Steam callbacks, so at
    /// most one pipeline may ever run.
    /// </summary>
    private IEnumerator PreloadPipeline(string levelId, bool matchDriven, bool chained = false)
    {
        // The in-game console lowercases its whole input line (Shell.cs:102) —
        // canonicalise (case-insensitive built-in/editor-pick match) before any
        // dictionary lookups. Server-sent ids are already canonical; no-op there.
        levelId = CollectionManager.CanonicalizeLevelId(levelId);
        if (string.IsNullOrEmpty(levelId))
        {
            Plugin.Logger.LogWarning("[Preload] empty level id — nothing to hold.");
            yield break;
        }

        var held = new HeldScene { LevelId = levelId, MatchDriven = matchDriven, Chained = chained };
        held.PreserveLevel = Game.currentLevel;   // the dormant Level's OnEnable hijacks currentLevel at load; restore this (null at the menu)
        _held = held;
        _doneReported = false;
        _dropRequested = false;
        _pendingStart = false;
        _awaitingSceneName = null;
        _gotPendingScene = false;
        if (matchDriven) Report("in_progress");
        Plugin.Logger.LogInfo($"[Preload] pipeline start: '{levelId}' (matchDriven={matchDriven})");

        // Reject unknown ids up front. FindBuiltInLevelIndex silently falls back
        // to index 0 (Intro) for legacy-launch leniency — preloading that
        // fallback would hold the WRONG scene. Same for a workshop id with no
        // resolvable metadata (not subscribed / folder missing).
        if (!CollectionManager.ValidateLevelId(levelId, out bool isMissing))
        {
            FailPipeline(held, isMissing
                ? $"level '{levelId}' not available (workshop item not subscribed / folder missing)"
                : $"unknown level id '{levelId}' (built-in ids: Intro, Train, Carry, Climb, Break, Siege, Water, Power, Aztec, Halloween, Steam, Ice, Intro_Reprise, Credits — exact spelling/case; or a subscribed workshop id)");
            yield break;
        }

        // ── resolve type, metadata, bundle, scene name ──
        held.Type = CollectionManager.ResolveLevelType(levelId);
        string fail = null;
        string sceneName = null;
        WorkshopLevelMetadata meta = null;

        if (Game.instance == null)
        {
            fail = "Game.instance is null";
        }
        else if (held.Type == WorkshopItemSource.BuiltIn)
        {
            held.Number = (int)CollectionManager.FindBuiltInLevelIndex(levelId);
            var levels = Game.instance.levels;
            if (held.Number < 0 || held.Number >= levels.Length) fail = "built-in index out of range: " + held.Number;
            else sceneName = levels[held.Number];
        }
        else if (held.Type == WorkshopItemSource.EditorPick)
        {
            held.Number = (int)CollectionManager.FindEditorPickLevelIndex(levelId);
            var levels = Game.instance.editorPickLevels;
            if (levels == null || held.Number < 0 || held.Number >= levels.Length) fail = "editor-pick index out of range: " + held.Number;
            else sceneName = levels[held.Number];
        }
        else if (held.Type == WorkshopItemSource.Subscription || held.Type == WorkshopItemSource.LocalWorkshop)
        {
            meta = CollectionManager.ResolveWorkshopMetadata(levelId);

            // Subscription: always run the game's ensure-installed path — it is a
            // no-op when current, and downloads the update otherwise (作者推更新
            // would otherwise land in the round's own load path). Single-slot
            // callbacks: safe here because we're serial and the menu has no
            // concurrent game load (ready-lock blocks manual launches).
            if (held.Type == WorkshopItemSource.Subscription)
            {
                if (!ulong.TryParse(levelId, out ulong wsId))
                {
                    fail = "subscription level id is not a workshop id: " + levelId;
                }
                else
                {
                    held.Number = unchecked((int)wsId); // the game casts the ulong level number to int
                    held.State = HeldSceneState.Downloading;
                    bool done = false;
                    WorkshopRepository.instance.levelRepo.LoadLevel(wsId, l => { meta = l; done = true; });
                    while (!done)
                    {
                        if (_dropRequested) break;
                        yield return null;
                    }
                    if (meta == null) fail = "workshop download/metadata failed (subscribed? id=" + wsId + ")";
                }
            }
            else
            {
                held.Number = 0; // App.LaunchCustomLevel passes level 0
            }

            if (fail == null && meta != null)
            {
                held.Bundle = FileTools.LoadBundle(meta.dataPath); // synchronous LoadFromFile
                if (held.Bundle == null) fail = "bundle load failed: " + meta.dataPath;
                else
                {
                    string[] scenePaths = held.Bundle.GetAllScenePaths();
                    if (scenePaths == null || scenePaths.Length == 0) fail = "bundle has no scenes: " + meta.dataPath;
                    else sceneName = Path.GetFileNameWithoutExtension(scenePaths[0]);
                }
            }
            else if (fail == null)
            {
                fail = "workshop metadata unavailable (local folder missing?)";
            }
        }
        else
        {
            fail = "unsupported level type: " + held.Type;
        }

        if (fail != null || _dropRequested || !HoldStillWanted(held))
        {
            FailPipeline(held, fail ?? (_dropRequested ? "dropped during resolve" : "hold no longer wanted during resolve"));
            yield break;
        }

        held.Metadata = meta;
        held.SceneName = sceneName;

        // ── capture the global lighting state BEFORE the load ──
        // The additive load switches lightProbes / lightmapsMode to the new
        // scene's (and the RS probe below needs the pre-load baseline). The
        // lightmap TABLE itself is engine-managed — never touched.
        held.PreLoadProbes = LightmapSettings.lightProbes;
        held.PreLoadRS = HeldScene.RenderSettingsSnapshot.Capture();
        held.PreLoadLMMode = LightmapSettings.lightmapsMode;
        held.PreLoadLMCount = LightmapSettings.lightmaps != null ? LightmapSettings.lightmaps.Length : 0;

        // ── additive scene load (the heavy part the player would otherwise
        //    wait for after round_start) ──
        held.State = HeldSceneState.LoadingScene;
        Application.backgroundLoadingPriority = ThreadPriority.Low; // same setting the game uses (Game.cs:693)

        // A just-dropped hold of the SAME scene may still be unloading — wait it
        // out first (same-name load/unload racing on Unity 2017 is flaky).
        if (_lastUnloadOp != null && _lastUnloadName == sceneName)
            while (!_lastUnloadOp.isDone) yield return null;

        _awaitingSceneName = sceneName;
        _gotPendingScene = false;
        _pendingLoadedScene = default(Scene);

        AsyncOperation op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
        if (op == null)
        {
            _awaitingSceneName = null;
            FailPipeline(held, "LoadSceneAsync returned null for '" + sceneName + "'");
            yield break;
        }
        while (!op.isDone)
            yield return null;   // drops can't cancel an in-flight load on 2017.4 — the hook already dormified it; discard happens below
        _awaitingSceneName = null;

        // The sceneLoaded hook captured + dormified the scene (it fires before
        // isDone); a short safety wait in case of ordering quirks.
        int waits = 0;
        while (!_gotPendingScene && waits++ < 300) yield return null;

        if (!_gotPendingScene || !_pendingLoadedScene.IsValid())
        {
            // Defensive: if a scene did load but the callback somehow missed,
            // unload the stray by handle so it can't linger visibly.
            if (_pendingLoadedScene.IsValid())
                SceneManager.UnloadSceneAsync(_pendingLoadedScene);
            FailPipeline(held, "scene '" + sceneName + "' loaded but no sceneLoaded callback matched");
            yield break;
        }

        // The hook sets State to Dormant, or Invalid with FailDetail when the
        // scene has no Level component.
        if (held.State != HeldSceneState.Dormant)
        {
            FailPipeline(held, held.FailDetail ?? "scene capture failed");   // held.Scene is set → really unloads
            yield break;
        }

        if (_dropRequested || !HoldStillWanted(held))
        {
            // Round already started (gate timeout / force start), the hold was
            // dropped, or the chained next level moved — discard the dormant
            // scene (unloads it; no report — the server cleared preload states
            // when the round began / pick changed; chained holds never report).
            DiscardLoadedScene(held, _dropRequested ? "dropped during scene load" : "hold no longer wanted after scene load");
            yield break;
        }

        // ── lighting freeze (post-load, before render) ──
        // Diagnostics so far: this Unity's additive load does NOT apply the
        // loaded scene's RenderSettings — but lightmapsMode/lightProbes may
        // still flip, which re-decodes the playing scene's lightmaps
        // (everything dark). Freeze the post-load state for the swap-in,
        // restore the playing scene's.
        var probesAfterLoad = LightmapSettings.lightProbes;
        var modeAfterLoad = LightmapSettings.lightmapsMode;
        int lmCountAfter = LightmapSettings.lightmaps != null ? LightmapSettings.lightmaps.Length : 0;

        LightmapSettings.lightmapsMode = held.PreLoadLMMode;   // decode mode back to the playing scene's
        held.PreLoadRS.Apply();
        held.SceneRS = held.PreLoadRS;   // degraded default — the probe overwrites on success
        // NOTE: lightProbes is deliberately NOT touched. Manual assignment of
        // LightmapSettings.lightProbes breaks dynamic-object sampling (the
        // "player goes dark" regression) — the switch is engine-owned and
        // stays that way (known accepted cosmetic: the tinting during the
        // hold window; see ignored/M3遗留问题调查-反编译实证.md §1/§2).
        Plugin.Logger.LogInfo(
            $"[Preload] lighting freeze for '{held.SceneName}': lmCount {held.PreLoadLMCount}->{lmCountAfter}, mode {held.PreLoadLMMode}->{modeAfterLoad}{(held.PreLoadLMMode == modeAfterLoad ? "" : " (FLIPPED — restored)")}, " +
            $"probes {(held.PreLoadProbes == null ? "null" : held.PreLoadProbes.GetInstanceID().ToString())}->{(probesAfterLoad == null ? "null" : probesAfterLoad.GetInstanceID().ToString())}{(Equals(held.PreLoadProbes, probesAfterLoad) ? "" : " (engine-switched — left alone)")}");

        // ── RS probe: learn the held scene's OWN RenderSettings ──
        // The engine never applies them for an additive load — but activation
        // does, eventually (sync, or by the next frame's integration point).
        // Briefly activate the held scene (roots stay dormant, nothing renders
        // from it), capture what the engine applies, then restore. The swap-in
        // re-applies the captured values so Level.OnEnable adopts the scene's
        // OWN fog instead of the outgoing level's leftovers.
        //
        // CaveRender is SUPPRESSED for the probe frames: it rewrites the
        // global fog from the playing level every OnPreCull, so an unsuppressed
        // post-frame capture would store the PLAYING level's fog as the held
        // scene's — and if the player happens to be underwater at that moment
        // (the hold can start/retry at any time during play), the swap would
        // permanently latch UNDERWATER fog into the next level (调查 §3.2
        // 锁存 B).
        yield return StartCoroutine(RsProbe(held));

        // Final drop/wanted check — the freeze+probe crossed frames, during
        // which a drop may have arrived (pick change / phase left PREP / the
        // chained next level moved).
        if (_dropRequested || !HoldStillWanted(held))
        {
            DiscardLoadedScene(held, _dropRequested ? "dropped during lighting freeze/probe" : "hold no longer wanted after probe");
            yield break;
        }

        Plugin.Logger.LogInfo($"[Preload] dormant: '{levelId}' scene '{held.SceneName}' roots={held.Roots.Length} bundle={(held.Bundle != null ? "yes" : "no")}; probedRS={held.SceneRS.Describe()}");
        TwilightLog.Print($"[Preload] '{levelId}' held dormant (scene '{held.SceneName}', {held.Roots.Length} roots) — twi preload swap");
        if (matchDriven)
        {
            Report("done");
            _doneReported = true;
        }
        PipelineExited();
    }

    /// <summary>
    /// One-frame RS probe of the held scene (see the pipeline). Runs as a
    /// coroutine so it can cross exactly one frame; restores the previous
    /// active scene, lightmapsMode and RenderSettings before returning. Never
    /// throws — a failed probe leaves <c>held.SceneRS</c> at its degraded
    /// default (the pre-load values) and logs a warning.
    /// </summary>
    private IEnumerator RsProbe(HeldScene held)
    {
        var probePrevActive = SceneManager.GetActiveScene();

        // Suppress every CaveRender for the probe window (one frame): the
        // camera just keeps the previous frame — invisible — but nothing
        // overwrites the fog the engine applies for the activated held scene.
        var caveRenders = UnityEngine.Object.FindObjectsOfType<CaveRender>();
        var caveWasEnabled = new bool[caveRenders.Length];
        for (int i = 0; i < caveRenders.Length; i++)
        {
            caveWasEnabled[i] = caveRenders[i].enabled;
            if (caveWasEnabled[i]) caveRenders[i].enabled = false;
        }

        var preRS = held.PreLoadRS;
        var syncRS = preRS;
        bool activated = false;
        try
        {
            preRS = HeldScene.RenderSettingsSnapshot.Capture();
            SceneManager.SetActiveScene(held.Scene);
            activated = true;
            syncRS = HeldScene.RenderSettingsSnapshot.Capture();   // applied synchronously?
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Preload] RS probe '{held.SceneName}' failed (activation): {e.Message} — using pre-load values.");
        }
        yield return null;   // one frame for the engine's integration to apply the scene's stored RS
        try
        {
            if (activated)
            {
                var postRS = HeldScene.RenderSettingsSnapshot.Capture();   // applied by this frame's integration?
                held.SceneRS = syncRS.Equals(preRS) ? postRS : syncRS;      // whichever one the engine actually applied
                held.SceneLMMode = LightmapSettings.lightmapsMode;
                Plugin.Logger.LogInfo(
                    $"[Preload] RS probe '{held.SceneName}': {(syncRS.Equals(preRS) ? "post-frame" : "sync")} applied, rs={held.SceneRS.Describe()}; probesAcrossActivation={(Equals(LightmapSettings.lightProbes, held.PreLoadProbes) ? "unchanged" : "SWITCHED (left engine-owned)")}");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Preload] RS probe '{held.SceneName}' failed (capture): {e.Message} — using pre-load values.");
        }
        finally
        {
            try
            {
                SceneManager.SetActiveScene(probePrevActive);
                LightmapSettings.lightmapsMode = held.PreLoadLMMode;
                held.PreLoadRS.Apply();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Preload] RS probe '{held.SceneName}' restore failed: {e.Message}");
            }
            for (int i = 0; i < caveRenders.Length; i++)
                if (caveWasEnabled[i] && caveRenders[i] != null) caveRenders[i].enabled = true;
        }
    }

    /// <summary>Capture + dormify our additive load; unrelated loads re-run the gates.</summary>
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (_awaitingSceneName != null && mode == LoadSceneMode.Additive && scene.name == _awaitingSceneName)
        {
            _awaitingSceneName = null;
            _pendingLoadedScene = scene;
            _gotPendingScene = true;
            // Capture AND deactivate right here: this callback runs before the
            // scene ever renders, so the menu never flashes the level. The
            // pipeline makes the keep/discard decision once its op completes —
            // either way held.Scene is set, so every abort path really unloads.
            var held = _held;
            if (held != null && held.State == HeldSceneState.LoadingScene && held.SceneName == scene.name)
                CaptureAndDormScene(held, scene);
            return;
        }

        // Any other load (e.g. returning to the menu from a practice level) may
        // make the preload gates newly satisfiable. A Single-mode load also means
        // a held scene is about to be destroyed — OnSceneUnloaded handles that.
        MaybeStartPreload();
    }

    private void OnSceneUnloaded(Scene scene)
    {
        var held = _held;
        if (held == null || held.State != HeldSceneState.Dormant) return;
        // Unity 2017.4's Scene struct has no handle — compare by name (the held
        // scene's name is unique among loaded scenes for our single-hold use).
        if (scene.name != held.SceneName) return;

        // The held scene was unloaded under us (any Single-mode load does this).
        held.State = HeldSceneState.Invalid;
        held.FailDetail = "held scene unloaded externally";
        Plugin.Logger.LogWarning("[Preload] " + held.FailDetail);
        ReleaseBundleOnly(held);
        _held = null;
        MaybeStartPreload(); // re-preload if the gates still hold (PREP + readied)
    }

    // ── Dormify / discard / release ──────────────────────────────────────────

    /// <summary>
    /// Capture a freshly loaded additive scene and put it to sleep: remember the
    /// authored active state of every root, deactivate them all (no rendering,
    /// no physics, no audio — inactive objects have zero presence on Unity
    /// 2017.4), grab the HumanAPI Level component, and undo the Game.currentLevel
    /// registration its Awake performed (the swap-in re-registers via
    /// Game.LevelLoaded). Runs inside the sceneLoaded callback — before the
    /// scene ever renders. Sets State to Dormant, or Invalid (+FailDetail) when
    /// the scene has no Level component.
    /// </summary>
    private void CaptureAndDormScene(HeldScene held, Scene scene)
    {
        held.Scene = scene;              // set FIRST — every abort path unloads through this
        held.SceneName = scene.name;
        held.Roots = scene.GetRootGameObjects();
        held.RootWasActive = new bool[held.Roots.Length];
        for (int i = 0; i < held.Roots.Length; i++)
        {
            held.RootWasActive[i] = held.Roots[i].activeSelf;
            if (held.Roots[i].activeSelf) held.Roots[i].SetActive(false);
        }

        foreach (var root in held.Roots)
        {
            var lv = root.GetComponent<Level>();
            if (lv == null) lv = root.GetComponentInChildren<Level>(true);
            if (lv != null) { held.Level = lv; break; }
        }

        if (held.Level == null)
        {
            held.State = HeldSceneState.Invalid;
            held.FailDetail = "no HumanAPI Level component in scene '" + scene.name + "'";
            return;
        }

        // Undo the Game.currentLevel registration the dormant Level's OnEnable
        // performed at load integration: restore what was current when the
        // pipeline started (null at the menu / M2, the level being played for
        // M3 chained holds). The swap-in re-registers via Game.LevelLoaded.
        if (Game.currentLevel == held.Level) Game.currentLevel = held.PreserveLevel;

        held.State = HeldSceneState.Dormant;
    }

    /// <summary>A loaded-but-unwanted scene: unload it and release the bundle.</summary>
    private void DiscardLoadedScene(HeldScene held, string reason)
    {
        Plugin.Logger.LogInfo($"[Preload] discarding held scene '{held.SceneName}': {reason}");
        held.State = HeldSceneState.Invalid;
        held.FailDetail = reason;
        ReleaseHeld(held);   // held.Scene was set at capture — this really unloads
        _held = null;
        PipelineExited();
    }

    private void FailPipeline(HeldScene held, string detail)
    {
        Plugin.Logger.LogWarning($"[Preload] failed ('{held.LevelId}'): {detail}");
        TwilightLog.Print($"[Preload] hold failed: {detail}");
        held.State = HeldSceneState.Invalid;
        held.FailDetail = detail;
        ReleaseHeld(held);
        _held = null;
        PipelineExited();
        if (held.MatchDriven) Report("failed", detail);
    }

    /// <summary>Common pipeline-exit bookkeeping: run a deferred start if one was latched.</summary>
    private void PipelineExited()
    {
        _pipeline = null;
        _dropRequested = false;   // the pipeline honoured any pending drop by exiting
        if (!_pendingStart) return;
        _pendingStart = false;
        MaybeStartPreload();   // e.g. a changed pick_announced arrived while the old hold was winding down
    }

    /// <summary>Unload the held scene (if loaded) and release the bundle file handle.</summary>
    private void ReleaseHeld(HeldScene held)
    {
        if (held.Scene.IsValid())
        {
            _lastUnloadOp = SceneManager.UnloadSceneAsync(held.Scene);
            _lastUnloadName = held.SceneName;   // a same-name reload waits for this op first
        }
        ReleaseBundleOnly(held);
    }

    private void ReleaseBundleOnly(HeldScene held)
    {
        if (held.Bundle == null) return;
        try { held.Bundle.Unload(false); } // keep loaded objects; mirrors the game's own post-load unload
        catch (Exception ex) { Plugin.Logger.LogWarning("[Preload] bundle Unload failed: " + ex.Message); }
        held.Bundle = null;
    }

    /// <summary>
    /// Drop the current hold. If the pipeline is mid-flight it observes the
    /// request and cleans up once its scene op completes (in-flight ops can't be
    /// cancelled on Unity 2017.4).
    /// </summary>
    public void ForceDrop(string reason)
    {
        if (_held == null && _pipeline == null) return;
        Plugin.Logger.LogInfo("[Preload] drop: " + reason);
        _dropRequested = true;
        if (_pipeline == null)
        {
            // Direct release — no pipeline will observe the flag, so honour the
            // drop IMMEDIATELY. Leaving _dropRequested set here deadlocked
            // MaybeStartPreload's gate forever (round 2+: new pick_announced
            // dropped the previous hold directly, and no preload ever ran again).
            var held = _held;
            _held = null;
            if (held != null)
            {
                held.State = HeldSceneState.Invalid;
                held.FailDetail = reason;
                ReleaseHeld(held);
            }
            _dropRequested = false;
        }
    }

    // ── GO-time swap-in hook (调研 §3.2) ─────────────────────────────────────

    /// <summary>
    /// Called from CollectionManager.LaunchLevel: if a dormant scene matches this
    /// level exactly, consume it and run the swap-in sequence instead of the
    /// standard launch. LevelStarted has already fired by the time this runs.
    /// </summary>
    public bool TrySwapIn(string levelId)
    {
        var held = _held;
        if (held == null || held.State != HeldSceneState.Dormant || !held.Scene.IsValid()) return false;
        string canonical = CollectionManager.CanonicalizeLevelId(levelId);
        if (!string.Equals(held.LevelId, canonical, StringComparison.Ordinal)) return false;

        _held = null;
        _pipeline = null;
        _lastConsumed = held;
        held.State = HeldSceneState.Consumed;
        Plugin.Logger.LogInfo($"[Preload] swap-in: '{levelId}' (scene '{held.SceneName}')");
        _swapRunning = true;
        StartCoroutine(SwapRunner(held));
        return true;
    }

    /// <summary>Drives the swap coroutine and clears the in-flight flag (also on fallback). The chained driver waits the flag out.</summary>
    private IEnumerator SwapRunner(HeldScene held)
    {
        yield return SwapInSequence.Run(held, OnSwapInFallback, OnOutgoingUnload);
        _swapRunning = false;
    }

    /// <summary>
    /// The swap sequence unloads the outgoing scene (last step); track that op
    /// so the chained driver doesn't race a new additive load against it and a
    /// same-name reload waits it out first.
    /// </summary>
    private void OnOutgoingUnload(AsyncOperation op, string sceneName)
    {
        _lastUnloadOp = op;
        _lastUnloadName = sceneName;
    }

    /// <summary>
    /// The swap-in sequence hit an error mid-way — recover through the standard
    /// launch path (a Single-mode load rebuilds every piece of game state from
    /// scratch, so it recovers from any intermediate state). LevelStarted was
    /// already fired by LaunchLevel and is NOT re-fired here.
    /// </summary>
    private void OnSwapInFallback(HeldScene held, string error)
    {
        Plugin.Logger.LogError($"[Preload] swap-in failed ('{held.LevelId}'): {error} — falling back to the standard launch path.");
        var mgr = CollectionManager.Instance;
        if (mgr != null)
            mgr.LaunchLevelStandard(held.LevelId);
        else
            Plugin.Logger.LogError("[Preload] CollectionManager missing — cannot fall back to the standard launch.");
    }

    // ── Debug / M1 entry points (`twi preload …`) ────────────────────────────

    /// <summary>
    /// M1/M3 prototype: hold an arbitrary level, bypassing the match gates.
    /// From the menu this exercises the M2 round-start swap; from inside a
    /// level (PlayingLevel) it exercises the M3 chained swap — hold the next
    /// level, then `twi preload swap` and check the level's machines.
    /// </summary>
    public void DebugHold(string levelId)
    {
        if (string.IsNullOrEmpty(levelId)) return;
        if (!TwilightConfig.EnableScenePreload.Value)
        {
            Plugin.Logger.LogWarning("[Preload] EnableScenePreload is off — nothing to do.");
            return;
        }
        bool inLevel = App.state == AppSate.PlayLevel && Game.instance != null
            && Game.instance.state == GameState.PlayingLevel;
        if (App.state != AppSate.Menu && !inLevel)
        {
            Plugin.Logger.LogWarning("[Preload] debug hold must run from the main menu or inside a playing level.");
            return;
        }
        if (_pipeline != null)
        {
            Plugin.Logger.LogWarning("[Preload] a pipeline is already running (see `twi preload status`).");
            return;
        }
        if (IsHeldDormant)
        {
            Plugin.Logger.LogWarning($"[Preload] already holding '{_held.LevelId}' — run `twi preload drop` first.");
            return;
        }
        _pipeline = StartCoroutine(PreloadPipeline(levelId, matchDriven: false));
    }

    /// <summary>M1 prototype: run the swap-in sequence on the current hold, outside any match flow.</summary>
    public void DebugSwap()
    {
        var session = MatchSession.Instance;
        if (session != null && session.ReadyLockActive)
        {
            Plugin.Logger.LogWarning("[Preload] ready-lock is active — the standard-launch fallback would be blocked. Un-ready first.");
            return;
        }
        if (_swapRunning)
        {
            Plugin.Logger.LogWarning("[Preload] a swap is already in flight.");
            return;
        }
        var held = _held;
        if (held == null || held.State != HeldSceneState.Dormant)
        {
            Plugin.Logger.LogWarning("[Preload] nothing dormant to swap in (see `twi preload status`).");
            return;
        }
        TrySwapIn(held.LevelId);
    }

    public string StatusString()
    {
        var sb = new StringBuilder();
        if (_announced != null)
            sb.Append($"announced: {_announced.Code} type={_announced.Type} first='{_announcedFirstLevel ?? "-"}'");
        else
            sb.Append("announced: (none)");
        sb.Append($" doneReported={_doneReported}");

        var held = _held;
        if (held != null)
        {
            sb.Append($"\nheld: '{held.LevelId}' state={held.State} type={held.Type} scene='{held.SceneName ?? "-"}'");
            if (held.Chained) sb.Append(" chained");
            if (held.Scene.IsValid()) sb.Append($" roots={held.Roots.Length}");
            if (held.Bundle != null) sb.Append(" bundle=yes");
            if (!string.IsNullOrEmpty(held.FailDetail)) sb.Append($" detail='{held.FailDetail}'");
        }
        else sb.Append("\nheld: (none)");

        if (_lastConsumed != null)
            sb.Append($"\nlastConsumed: '{_lastConsumed.LevelId}' scene='{_lastConsumed.SceneName}'");
        return sb.ToString();
    }

    /// <summary>
    /// On-demand machine-state dump (`twi preload mach`) — run it the moment a
    /// machine is OBSERVED broken in-game; the dump shows the live joint
    /// angles/drives/sleep state of every machine component in the level.
    /// </summary>
    public void DumpMachines()
    {
        try { MachineDiagnostics.DumpCurrentLevel("manual"); }
        catch (Exception ex) { Plugin.Logger.LogWarning("[Preload] machine dump failed: " + ex.Message); }
    }

    /// <summary>
    /// Dump the live global lighting state for real-machine diagnosis
    /// (`twi preload rs`) — probe-set id, ambient, fog, lightmap table. Copy
    /// into bug reports alongside the pipeline's lighting-freeze / RS-probe
    /// log lines.
    /// </summary>
    public string RenderStateString()
    {
        var sb = new StringBuilder();
        Scene active = SceneManager.GetActiveScene();
        sb.Append($"activeScene='{active.name}' lmCount={(LightmapSettings.lightmaps != null ? LightmapSettings.lightmaps.Length : 0)} lmMode={LightmapSettings.lightmapsMode}");
        var probes = LightmapSettings.lightProbes;
        sb.Append($"\nprobes: {(probes != null ? probes.GetInstanceID() + " '" + probes.name + "'" : "null")}");
        sb.Append($"\nambient: mode={RenderSettings.ambientMode} light={RenderSettings.ambientLight} intensity={RenderSettings.ambientIntensity:0.###}");
        sb.Append($"\nfog: {RenderSettings.fog}/{RenderSettings.fogColor}/d={RenderSettings.fogDensity:0.####}/{RenderSettings.fogMode} (multiplier={CaveRender.fogDensityMultiplier:0.##})");
        sb.Append($"\nsun={(RenderSettings.sun != null ? RenderSettings.sun.name : "none")} skybox={(RenderSettings.skybox != null ? RenderSettings.skybox.name : "none")}");
        sb.Append($"\ncurrentLevel={(Game.currentLevel != null ? Game.currentLevel.name + " fog=" + Game.currentLevel.fogColor + "/d=" + Game.currentLevel.fogDensity : "null")}");
        var held = _held;
        if (held != null && held.Scene.IsValid())
            sb.Append($"\nheld[{held.State}]: probedRS={held.SceneRS.Describe()}");
        return sb.ToString();
    }

    // ── Reporting (需求-合集提前下发与预载门控.md R2) ─────────────────────────

    /// <summary>
    /// Send a <c>preload_report</c>. Strict fields (pydantic extra=forbid):
    /// type/status(+detail). Only meaningful from an authenticated player seat —
    /// the server rejects other seats, and without a pick_announced-capable
    /// server the report is pointless anyway.
    /// </summary>
    private void Report(string status, string detail = null)
    {
        var session = MatchSession.Instance;
        if (session == null || !session.IsAuthenticated || !session.IsPlayerSeat) return;
        if (_client == null || !_client.IsConnected) return;

        var msg = new Dictionary<string, object> { { "type", Msg.PreloadReport }, { "status", status } };
        if (!string.IsNullOrEmpty(detail)) msg["detail"] = detail;
        _client.Send(msg);
        Plugin.Logger.LogInfo($"[Preload] report: {status}" + (string.IsNullOrEmpty(detail) ? "" : $" ({detail})"));
    }
}
