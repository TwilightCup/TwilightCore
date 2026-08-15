using HarmonyLib;
using HumanAPI;
using Multiplayer;

namespace TwilightCore;

/// <summary>
/// Harmony patches for methods that CAN be reliably matched.
/// Button injection is handled by Bootstrapper (no Harmony dependency)
/// because private/override method matching is unreliable in this environment.
/// </summary>
internal static class HarmonyPatches
{
    public static void Apply()
    {
        var harmony = new Harmony("LevelCollections");

        // ── App.StartNextLevel prefix ──────────────────────────────
        // Intercepts built-in level progression to advance collection runs.

        var snlOriginal = AccessTools.Method(typeof(App), nameof(App.StartNextLevel));
        var snlPrefix = AccessTools.Method(typeof(HarmonyPatches), nameof(StartNextLevel_Prefix));

        if (snlOriginal != null && snlPrefix != null)
        {
            harmony.Patch(snlOriginal, new HarmonyMethod(snlPrefix));
            Plugin.Logger.LogInfo("Harmony: patched App.StartNextLevel.");
        }
        else
        {
            Plugin.Logger.LogError(
                $"Harmony: failed to find StartNextLevel. original={snlOriginal != null}, prefix={snlPrefix != null}");
        }

        // ── Game.Fall prefix ───────────────────────────────────────
        // Workshop and EditorPick levels call PauseLeave() directly
        // when passed — our StartNextLevel patch never fires for them.
        // This prefix intercepts that path during collection runs.

        var fallOriginal = AccessTools.Method(typeof(Game), nameof(Game.Fall));
        var fallPrefix = AccessTools.Method(typeof(HarmonyPatches), nameof(Fall_Prefix));

        if (fallOriginal != null && fallPrefix != null)
        {
            harmony.Patch(fallOriginal, new HarmonyMethod(fallPrefix));
            Plugin.Logger.LogInfo("Harmony: patched Game.Fall.");
        }
        else
        {
            Plugin.Logger.LogError(
                $"Harmony: failed to find Game.Fall. original={fallOriginal != null}, prefix={fallPrefix != null}");
        }

        // ── App.PauseLeave prefix ──────────────────────────────────
        // Quitting a level mid-run (Esc → Exit → ConfirmExitMenu →
        // PauseLeave, or GameOver) bypasses our completion patches and
        // used to leave the collection run state lingering, corrupting
        // later normal single-player play and lc commands. This prefix
        // aborts the run whenever a level is left while one is active.

        var plOriginal = AccessTools.Method(typeof(App), nameof(App.PauseLeave));
        var plPrefix = AccessTools.Method(typeof(HarmonyPatches), nameof(PauseLeave_Prefix));

        if (plOriginal != null && plPrefix != null)
        {
            harmony.Patch(plOriginal, new HarmonyMethod(plPrefix));
            Plugin.Logger.LogInfo("Harmony: patched App.PauseLeave.");
        }
        else
        {
            Plugin.Logger.LogError(
                $"Harmony: failed to find App.PauseLeave. original={plOriginal != null}, prefix={plPrefix != null}");
        }
    }

    // ── Patches ────────────────────────────────────────────────────

    private static bool StartNextLevel_Prefix(ulong level, int checkpoint)
    {
        var mgr = CollectionManager.Instance;
        if (mgr == null || !mgr.IsInCollectionRun)
            return true;

        Plugin.Logger.LogInfo($"Collection run: intercepting StartNextLevel({level}, {checkpoint}), advancing.");
        mgr.AdvanceToNextLevel();
        return false;
    }

    /// <summary>
    /// Intercept Game.Fall for workshop/EditorPick level completion.
    /// When a workshop or EditorPick level is passed, the game calls
    /// PauseLeave() directly instead of StartNextLevel(), so we must
    /// hook here to advance the collection run.
    /// </summary>
    private static bool Fall_Prefix(Game __instance, HumanBase humanBase, bool drown, bool fallAchievement)
    {
        var mgr = CollectionManager.Instance;
        if (mgr == null || !mgr.IsInCollectionRun)
            return true;

        // Only intercept the level-completion path
        if (!__instance.passedLevel)
            return true;

        if (__instance.workshopLevel != null || __instance.currentLevelType == WorkshopItemSource.EditorPick)
        {
            Plugin.Logger.LogInfo($"Collection run: intercepting Fall for {__instance.currentLevelType} level, advancing.");
            __instance.passedLevel = false;
            PlayerManager.SetSingle();
            mgr.AdvanceToNextLevel();
            return false; // skip original — prevents PauseLeave() call
        }

        return true;
    }

    /// <summary>
    /// Abort the collection run whenever the player leaves a level via
    /// App.PauseLeave() (Esc → Exit → ConfirmExitMenu, GameOver, etc.).
    /// Those paths never reach our completion patches, so without this the
    /// run state would linger and corrupt later normal play / lc commands.
    /// No-op when no run is active (normal play, or EndCollectionRun's own
    /// PauseLeave call — that one clears the state before invoking it).
    /// </summary>
    private static void PauseLeave_Prefix()
    {
        var mgr = CollectionManager.Instance;
        if (mgr == null || !mgr.IsInCollectionRun)
            return;

        Plugin.Logger.LogInfo("Collection run: player left the level via PauseLeave; aborting run.");
        mgr.AbortCollectionRun();
    }
}
