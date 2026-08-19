using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using HumanAPI;
using Multiplayer;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TwilightCore.Preload;

/// <summary>
/// The GO-time swap-in sequence (激进预载held-scene方案调研.md §3.2): turns a
/// dormant held scene into the ACTIVE level without any scene loading — the
/// countdown ends with the player in the level. Replicates, step for step, what
/// <c>App.LaunchSinglePlayer → App.LaunchGame → Game.LoadLevel</c> would have
/// done around the load itself, with ONE structural difference to the naive
/// translation (see the window note below).
///
/// <list type="bullet">
/// <item>App prologue — App.cs:954/975-990 (startedCheckpoint, HideMenus,
/// SuspendDeltasForLoad) and the LaunchGame coroutine head — App.cs:993-1017
/// (FadeOutActive, state=LoadLevel, 0.2s ui fade, DiscardPools);</item>
/// <item>Game.LoadLevel prologue — Game.cs:652-693 (ClearAllButPlayers,
/// BeforeLoad/SignalManager.BeginReset, skybox capture, state=LoadingLevel);</item>
/// <item>timer-edge waits, THEN the "load" itself as one atomic frame:
/// activate the held scene + apply its probed RenderSettings, re-enable the
/// root objects, re-register game fields/currentLevel, bundle fixup, AfterLoad
/// (BeginLevel/RespawnAllPlayers/Reset/SignalManager.EndReset);</item>
/// <item>the outgoing scene unload LAST — pure cleanup, no game state depends
/// on it anymore;</item>
/// <item>the App onComplete — App.cs:1035-1052 (ExitMenus, DiscardPools,
/// ResumeDeltasAfterLoad, FadeOut, PlayLevel).</item>
/// </list>
///
/// <para><b>The two ordering rules (M3 machine fixes).</b> (1) SCENE
/// COEXISTENCE: the built-in levels are all laid out around the world origin
/// and overlap heavily — if the incoming level's colliders go live while the
/// outgoing scene is still loaded, the penetration solver fires separation
/// impulses on every machine sitting inside the outgoing geometry (dumpsters
/// launched, catapult pre-wound, position-dependent breakage within one
/// level). The standard path never coexists: a Single load swaps the scenes
/// atomically. The swap therefore UNLOADS the outgoing scene BEFORE activating
/// the held roots (cheap — they are still dormant), covering the empty-world
/// frames with the game's own quick network-load fade. (2) START ORDER: after
/// activation, yield exactly one frame so Start() runs before AfterLoad —
/// dormancy deferred Start to the first Update after activation, and
/// ResetState code assumes Start-initialized state (same-frame AfterLoad made
/// Break/Siege/Halloween NRE on the real machine). All timer-edge waiting
/// (2×WaitForFixedUpdate) happens before activation; the single activation →
/// AfterLoad gap matches the standard path's own one-frame window.</para>
///
/// <para><b>Fog-claim order.</b> A level's fogColor/fogDensity are ADOPTED from
/// the live RenderSettings in Level.OnEnable (decompiled HumanAPI) — the
/// standard path works because a Single load applies the new scene's stored
/// RenderSettings BEFORE the objects enable. The activation block replicates
/// that: apply the held scene's probed values FIRST, then re-enable the roots,
/// with no yield in between (CaveRender rewrites the global fog from
/// currentLevel every frame and must not interleave).</para>
///
/// <para>Any failed step falls back to the standard launch path (a Single-mode
/// load rebuilds all game state, recovering from any intermediate point).
/// Steps that are pure parity (analytics, playtime, GameSave) only log on
/// failure.</para>
/// </summary>
internal static class SwapInSequence
{
    // Private Game members (stable across the tournament-pinned game version).
    private static MethodInfo _fixupLoadedBundle;
    private static FieldInfo _skyColorField;
    private static FieldInfo _gameBundleField;
    private static bool _reflectionResolved;

    // JointImplementation.initialized (private) — cleared to force re-init.
    private static FieldInfo _jointInitializedField;

    private static void ResolveReflection()
    {
        if (_reflectionResolved) return;
        _fixupLoadedBundle = AccessTools.Method(typeof(Game), "FixupLoadedBundle");
        _skyColorField = AccessTools.Field(typeof(Game), "skyColor");
        _gameBundleField = AccessTools.Field(typeof(Game), "bundle");
        _reflectionResolved = true;
        if (_fixupLoadedBundle == null || _skyColorField == null)
            Plugin.Logger.LogWarning("[Preload] private Game members not found — swap-in will use the manual skybox-restore fallback (game update?).");
    }

    /// <summary>Run a sync step; null on success, an error string on failure.</summary>
    private static string TryStep(string what, Action step)
    {
        try { step(); return null; }
        catch (Exception e) { return what + ": " + e.Message; }
    }

    /// <summary>Run a pure-parity step; failures are logged, never fatal.</summary>
    private static void BestEffort(string what, Action step)
    {
        try { step(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Preload] parity step '{what}' failed (non-fatal): {e.Message}"); }
    }

    /// <summary>
    /// Run the swap-in. <paramref name="onOutgoingUnload"/> (optional) receives
    /// the outgoing scene's unload operation the moment it is issued — the
    /// chained-preload driver uses it to avoid racing a new additive load
    /// against an unload that is still in flight.
    /// </summary>
    public static IEnumerator Run(HeldScene held, Action<HeldScene, string> onFallback,
        Action<AsyncOperation, string> onOutgoingUnload = null)
    {
        var game = Game.instance;
        var app = App.instance;
        if (game == null || app == null)
        {
            onFallback(held, "Game/App instance missing");
            yield break;
        }
        ResolveReflection();
        float t0 = Time.realtimeSinceStartup;   // diagnostics: one summary line at the end

        // ── App.LaunchSinglePlayer + LaunchGame(sync) prologue (App.cs:954-990) ──
        string err = TryStep("app prologue", () =>
        {
            app.startedCheckpoint = 0;
            MenuSystem.instance.HideMenus();
            CheatCodes.cheatMode = false;
            app.SuspendDeltasForLoad();
        });
        if (err != null) { onFallback(held, err); yield break; }

        // Playtime/rating parity for workshop levels (Game.LoadLevel's isBundle
        // block — skipped for local "lvl:" levels exactly as the game does).
        if (held.Type == WorkshopItemSource.Subscription || held.Type == WorkshopItemSource.LocalWorkshop)
            BestEffort("rating init", () => RatingMenu.instance.LoadInit());
        if (held.Type == WorkshopItemSource.Subscription && held.Metadata != null)
            BestEffort("playtime", () =>
            {
                App.StartPlaytimeForItem(held.Metadata.workshopId);
                RatingMenu.instance.QueryRatingStatus(held.Metadata.workshopId, true);
            });
        BestEffort("playtime local", App.StartPlaytimeLocalPlayers);

        // ── LaunchGame coroutine head (App.cs:993-1017) ──
        bool ui = App.state == AppSate.Menu;
        err = TryStep("menu fade", () =>
        {
            if (ui) MenuSystem.instance.FadeOutActive();
            App.state = AppSate.LoadLevel;
        });
        if (err != null) { onFallback(held, err); yield break; }
        if (ui) yield return new WaitForSeconds(0.2f);

        err = TryStep("net pools", NetStream.DiscardPools);
        if (err != null) { onFallback(held, err); yield break; }

        // ── Game.LoadLevel prologue (Game.cs:652-693) ──
        err = TryStep("load prologue", () =>
        {
            NetScope.ClearAllButPlayers();
            game.BeforeLoad();                               // SignalManager.BeginReset
            game.skyboxMaterial = RenderSettings.skybox;     // captured like LoadLevel does;
            if (_skyColorField != null)                      // FixupLoadedBundle restores from these
                _skyColorField.SetValue(game, RenderSettings.ambientLight);
            game.state = GameState.LoadingLevel;             // timer segment-end edge
        });
        if (err != null) { onFallback(held, err); yield break; }

        // ── clear the players' camera water sensors BEFORE anything else ──
        // Until the outgoing scene finishes unloading (LAST step now), its
        // water bodies stay alive and the camera sensor keeps pointing at
        // them — CaveRender.OnPreCull would keep writing UNDERWATER fog into
        // the globals across the whole swap, and Level.OnEnable would adopt
        // it (the water-fog latch: drowning out of a level permanently bakes
        // the underwater fog into every subsequent level). Clearing here makes
        // CaveRender fall back to the (old) level fog for the remaining
        // window frames. WaterBody.ForceLeaveCollider can NOT be used for
        // this: it forwards through OnTriggerExit(sensor.GetComponent
        // <Collider>()) and the camera sensor has no collider (NRE).
        BestEffort("water sensors", () =>
        {
            var listField = AccessTools.Field(typeof(WaterSensor), "waterBodies");
            foreach (var h in Human.all)
            {
                if (h == null || h.player == null || h.player.cameraController == null) continue;
                var sensor = h.player.cameraController.waterSensor;
                if (sensor == null || ReferenceEquals(sensor.waterBody, null)) continue;
                Plugin.Logger.LogInfo($"[Preload] clearing water body reference on '{h.name}' before activation.");
                sensor.waterBody = null;
                var list = listField != null ? listField.GetValue(sensor) as IList : null;
                if (list != null) list.Clear();
            }
        });

        // ── timer-edge waits, BEFORE activation (physics-window rule) ──
        // Guarantee at least one full FixedUpdate samples GameState.LoadingLevel
        // before AfterLoad flips to PlayingLevel, so the polling timer always
        // sees segment-end and segment-start as separate edges. Doing this
        // BEFORE the activation block means the fixed steps simulate exactly
        // the pre-advance world: the held scene's roots are still dormant
        // (inactive objects have zero physics presence on Unity 2017.4), so
        // physics never touches the new scene before AfterLoad initializes
        // and resets it.
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        // ── unload the outgoing scene BEFORE activation (scene-coexistence rule) ──
        // The built-in levels are all laid out around the world origin and
        // OVERLAP heavily in world space. If the incoming level's colliders
        // are live while the outgoing scene is still loaded, the penetration
        // solver fires separation impulses on every machine that happens to
        // sit inside the outgoing level's geometry — dumpsters get launched,
        // the catapult comes up pre-wound, some levers are shoved off their
        // operating range while others (parked clear of the overlap) are fine.
        // Reset cannot fully erase that: it teleports poses, but most
        // ResetState implementations don't zero child rigidbody velocities.
        // The standard path never has this problem — a Single load unloads
        // the old scene and activates the new one atomically, so the two
        // collider sets never coexist. Replicate that invariant: unload the
        // outgoing scene while the incoming one is still dormant (zero risk —
        // nothing from it is needed anymore), THEN activate. The empty-world
        // frames are shown directly (no fade), per real-machine preference.
        // (Later testing proved coexistence was NOT the machine breaker — the
        // joint rebuild below is — but the order is kept for standard-path
        // parity.)
        Scene prevActive = SceneManager.GetActiveScene();
        AsyncOperation unloadOp = null;
        string prevActiveName = prevActive.IsValid() ? prevActive.name : "";
        float tStep = Time.realtimeSinceStartup;
        if (prevActive.IsValid() && prevActiveName != held.SceneName) // 2017.4 Scene has no handle — name compare
        {
            err = TryStep("outgoing scene unload", () => { unloadOp = SceneManager.UnloadSceneAsync(prevActive); });
            if (err != null) { onFallback(held, err); yield break; }
            if (onOutgoingUnload != null && unloadOp != null) onOutgoingUnload(unloadOp, prevActiveName);
            while (unloadOp != null && !unloadOp.isDone) yield return null;
            Plugin.Logger.LogInfo($"[Preload] swap timing: unload '{prevActiveName}' {(Time.realtimeSinceStartup - tStep) * 1000f:0} ms (pre-activation)");
        }

        // ── the "load": one atomic frame — activate, apply, re-enable, register ──
        tStep = Time.realtimeSinceStartup;
        err = TryStep("scene activate", () =>
        {
            // ORDER MATTERS (fog claim). Apply the held scene's probed
            // RenderSettings FIRST, then re-enable the root objects — OnEnable
            // adopts the applied own-scene values. No yield inside this block:
            // CaveRender drives the global fog from Game.currentLevel every
            // frame and must not interleave between Apply and the adoption.
            SceneManager.SetActiveScene(held.Scene);
            held.SceneRS.Apply();
            LightmapSettings.lightmapsMode = held.SceneLMMode;
            // Probe write-back (tinting fix): BAKED scenes only — the hold
            // froze their coefficients into a uniform field; restore the real
            // values now, same sync frame, before render. Unbaked scenes were
            // never written (managed coefficient writes mis-scale in their
            // renderer path — see the pipeline's freeze notes).
            if (held.RealBakedProbes != null)
            {
                var lp = LightmapSettings.lightProbes;
                var live = lp != null ? lp.bakedProbes : null;
                if (live != null && live.Length == held.RealBakedProbes.Length)
                    lp.bakedProbes = held.RealBakedProbes;
                else
                    Plugin.Logger.LogWarning($"[Preload] probe write-back skipped: live count {(live != null ? live.Length.ToString() : "null")} != saved {held.RealBakedProbes.Length}.");
            }
            for (int i = 0; i < held.Roots.Length; i++)
                if (held.RootWasActive[i]) held.Roots[i].SetActive(true);
            // Game fields + currentLevel switch in the same frame: CaveRender
            // drives the global fog from Game.currentLevel every frame, so the
            // first render after activation must already run on the new level.
            game.currentLevelType = held.Type;
            game.currentLevelNumber = held.Number;
            game.workshopLevel = held.Metadata;   // null for built-in/editor-pick, as in the standard path
            game.LevelLoaded(held.Level);         // re-register Game.currentLevel (Game.cs:226)
        });
        if (err != null) { onFallback(held, err); yield break; }
        Plugin.Logger.LogInfo(
            $"[Preload] swap timing: activate+RS {(Time.realtimeSinceStartup - tStep) * 1000f:0} ms; lmMode={held.SceneLMMode}; rs={held.SceneRS.Describe()}");

        // ── runtime-joint rebuild (THE machine fix) ──
        // The HumanAPI joint system (AngularJoint & co.) creates its
        // ConfigurableJoints in Awake with a "rotate the body to mid-range →
        // create the joint → set limits → rotate back to 0" dance — the PhysX
        // constraint's angular reference frame is anchored to the relative
        // pose AT CREATION. A held scene runs that dance at load time, then
        // the whole scene deactivates (dormancy) and reactivates at the swap:
        // PhysX re-creates every constraint at reactivation, and for these
        // runtime-created joints the recreated limits/drives end up operating
        // in a wrong reference frame. Real-machine evidence
        // (ignored/M3遗留问题调查-反编译实证.md §4.6): dumpster lid sags 84°
        // PAST its angular limit (-92 vs min -7.5), Power's latch 11° past
        // (min 0), the catapult arm pins at its min limit while its drive
        // targets -7.3 — while scene-AUTHORED HingeJoints (doors, capsules)
        // are all fine. Destroying and re-creating the joint right after
        // activation re-runs the same EnsureInitialized dance a fresh standard
        // load would run, re-anchoring the frame at the correct pose.
        // (Catapult's own dynamic FixedJoints need no rebuild — Start() and
        // PostResetState destroy/recreate them at the swap anyway. Ropes are
        // NOT covered: their bone chain has no rebuild API — watch for them
        // in testing.)
        err = TryStep("runtime joint rebuild", () =>
        {
            if (_jointInitializedField == null)
                _jointInitializedField = AccessTools.Field(typeof(JointImplementation), "initialized");
            int rebuilt = 0;
            foreach (var root in held.Roots)
            {
                foreach (var ji in root.GetComponentsInChildren<JointImplementation>(true))
                {
                    if (ji == null) continue;
                    ji.DestroyMainJoint();
                    if (_jointInitializedField != null) _jointInitializedField.SetValue(ji, false);
                    ji.EnsureInitialized();
                    rebuilt++;
                }
            }
            Plugin.Logger.LogInfo($"[Preload] rebuilt {rebuilt} runtime joints for '{held.SceneName}'.");
        });
        if (err != null) { onFallback(held, err); yield break; }

        // ── let Start() run before AfterLoad (standard-path frame order) ──
        // The standard path integrates the scene (Awake/OnEnable/Start all
        // fire as the load completes) and only runs FixupLoadedBundle +
        // AfterLoad on the NEXT coroutine frame. A held scene's Awake/OnEnable
        // ran at load time, but Start was deferred by dormancy — it fires in
        // the first Update phase after activation. Running AfterLoad (and its
        // Reset sweep) in the SAME frame as activation inverts that order, and
        // ResetState code that assumes Start-initialized state throws NREs
        // (observed on the real machine: Break/Siege/Halloween swaps failing
        // in AfterLoad). One yield restores the exact standard ordering; the
        // single-frame gap is the same window the standard path itself has.
        yield return null;

        // ── Game.LoadLevel tail (Game.cs:785-821) — SAME FRAME as activation ──

        if (held.IsBundleLevel)
        {
            err = TryStep("bundle fixup", () =>
            {
                if (_fixupLoadedBundle != null)
                {
                    _fixupLoadedBundle.Invoke(game, new object[] { held.Scene });
                }
                else
                {
                    // Manual equivalent of FixupLoadedBundle (Game.cs:549-558) —
                    // every piece it calls is public.
                    if (!held.Level.keepSkybox)
                    {
                        RenderSettings.skybox = game.skyboxMaterial;
                        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                        if (_skyColorField != null)
                            RenderSettings.ambientLight = (Color)_skyColorField.GetValue(game);
                    }
                    BundleRepository.RebindScene(held.Scene);
                }
                held.Bundle.Unload(false);   // objects stay resident (Game.cs:799)
                if (_gameBundleField != null)
                    _gameBundleField.SetValue(game, held.Bundle); // parity: the exit path Unload(true)s it
            });
            if (err != null) { onFallback(held, err); yield break; }
        }
        else
        {
            // Non-bundle levels get the analytics call (Game.cs:800-802).
            BestEffort("analytics", () =>
            {
                if (held.Type != WorkshopItemSource.EditorPick && HumanAnalytics.instance != null
                    && held.Number >= 0 && held.Number < game.levels.Length)
                    HumanAnalytics.instance.LoadLevel(game.levels[held.Number], held.Number, 0, 0f);
            });
        }

        err = TryStep("audio fixup", () => game.FixAssetBundleImport(false));
        if (err != null) { onFallback(held, err); yield break; }

        tStep = Time.realtimeSinceStartup;
        err = TryStep("AfterLoad", () => game.AfterLoad(0, 0));   // state = PlayingLevel — timer segment-start edge
        if (err != null) { onFallback(held, err); yield break; }
        Plugin.Logger.LogInfo($"[Preload] swap timing: AfterLoad {(Time.realtimeSinceStartup - tStep) * 1000f:0} ms");

        // GameSave side effects (Game.cs:805-814) — loading a level WRITES the
        // "resume here" save slot; skipping this would change campaign progress
        // behaviour vs the standard path.
        BestEffort("gamesave", () =>
        {
            if (!NetGame.isLocal) return;
            if (held.Type == WorkshopItemSource.BuiltIn && game.currentLevelNumber < game.levelCount - 1)
                GameSave.PassCheckpointCampaign((uint)game.currentLevelNumber, 0, 0);
            if (held.Type == WorkshopItemSource.EditorPick)
                GameSave.PassCheckpointEditorPick((uint)game.currentLevelNumber, 0, 0);
        });

        // ── App onComplete (App.cs:1035-1052 + LaunchSinglePlayer's callback) ──
        err = TryStep("app tail", () =>
        {
            MenuSystem.instance.ExitMenus();
            NetStream.DiscardPools();
            app.ResumeDeltasAfterLoad();
            MenuCameraEffects.FadeOut(1f);
            App.state = AppSate.PlayLevel;
        });
        if (err != null) { onFallback(held, err); yield break; }

        Plugin.Logger.LogInfo(
            $"[Preload] swap-in complete: '{held.LevelId}' (scene '{held.SceneName}') in {(Time.realtimeSinceStartup - t0) * 1000f:0} ms total.");
    }
}
