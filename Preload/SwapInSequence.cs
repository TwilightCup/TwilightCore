using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using HumanAPI;
using Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwilightCore.Preload;

/// <summary>
/// The GO-time swap-in sequence (激进预载held-scene方案调研.md §3.2): turns a
/// dormant held scene into the ACTIVE level without any scene loading — the
/// countdown ends with the player in the level. Replicates, step for step, what
/// <c>App.LaunchSinglePlayer → App.LaunchGame → Game.LoadLevel</c> would have
/// done around the load itself:
///
/// <list type="bullet">
/// <item>App prologue — App.cs:954/975-990 (startedCheckpoint, HideMenus,
/// SuspendDeltasForLoad) and the LaunchGame coroutine head — App.cs:993-1017
/// (FadeOutActive, state=LoadLevel, 0.2s ui fade, DiscardPools);</item>
/// <item>Game.LoadLevel prologue — Game.cs:652-693 (ClearAllButPlayers,
/// BeforeLoad/SignalManager.BeginReset, skybox capture, state=LoadingLevel);</item>
/// <item>the "load" itself — activate the held roots, SetActiveScene, unload the
/// menu ('Empty') scene;</item>
/// <item>Game.LoadLevel tail — Game.cs:785-821 (Game fields, LevelLoaded,
/// FixupLoadedBundle + bundle.Unload(false), analytics, FixAssetBundleImport,
/// AfterLoad, GameSave side effects) and the App onComplete — App.cs:1035-1052
/// (ExitMenus, DiscardPools, ResumeDeltasAfterLoad, FadeOut, PlayLevel).</item>
/// </list>
///
/// Any failed step falls back to the standard launch path (a Single-mode load
/// rebuilds all game state, recovering from any intermediate point). Steps that
/// are pure parity (analytics, playtime, GameSave) only log on failure.
/// </summary>
internal static class SwapInSequence
{
    // Private Game members (stable across the tournament-pinned game version).
    private static MethodInfo _fixupLoadedBundle;
    private static FieldInfo _skyColorField;
    private static FieldInfo _gameBundleField;
    private static bool _reflectionResolved;

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

    public static IEnumerator Run(HeldScene held, Action<HeldScene, string> onFallback)
    {
        var game = Game.instance;
        var app = App.instance;
        if (game == null || app == null)
        {
            onFallback(held, "Game/App instance missing");
            yield break;
        }
        ResolveReflection();

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
            game.state = GameState.LoadingLevel;
        });
        if (err != null) { onFallback(held, err); yield break; }

        // ── the "load": activate the held scene in place of the menu scene ──
        Scene prevActive = SceneManager.GetActiveScene();
        err = TryStep("scene activate", () =>
        {
            for (int i = 0; i < held.Roots.Length; i++)
                if (held.RootWasActive[i]) held.Roots[i].SetActive(true);
            SceneManager.SetActiveScene(held.Scene);
        });
        if (err != null) { onFallback(held, err); yield break; }

        AsyncOperation unloadOp = null;
        if (prevActive.IsValid() && prevActive.name != held.SceneName) // 2017.4 Scene has no handle — name compare
        {
            err = TryStep("menu scene unload", () => { unloadOp = SceneManager.UnloadSceneAsync(prevActive); });
            if (err != null) { onFallback(held, err); yield break; }
            while (unloadOp != null && !unloadOp.isDone) yield return null;
        }

        // Timer-edge hygiene: guarantee at least one full FixedUpdate samples
        // GameState.LoadingLevel before AfterLoad flips to PlayingLevel, so the
        // polling timer always sees segment-end and segment-start as separate
        // edges (matters for future in-round chained swaps; harmless here).
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        // ── Game.LoadLevel tail (Game.cs:785-821) ──
        err = TryStep("game fields", () =>
        {
            game.currentLevelType = held.Type;
            game.currentLevelNumber = held.Number;
            game.workshopLevel = held.Metadata;   // null for built-in/editor-pick, as in the standard path
            game.LevelLoaded(held.Level);         // re-register Game.currentLevel (Game.cs:226)
        });
        if (err != null) { onFallback(held, err); yield break; }

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

        err = TryStep("AfterLoad", () => game.AfterLoad(0, 0));   // state = PlayingLevel — timer segment-start edge
        if (err != null) { onFallback(held, err); yield break; }

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

        Plugin.Logger.LogInfo($"[Preload] swap-in complete: '{held.LevelId}' (scene '{held.SceneName}').");
    }
}
