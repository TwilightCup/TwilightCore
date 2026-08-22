using HarmonyLib;
using Multiplayer;
using TwilightCore.Chat;
using TwilightCore.Match;

namespace TwilightCore.Ready;

/// <summary>
/// False-start prevention (需求文档 §10.4): while connected and in PREP or
/// COUNTDOWN, manual level starts are blocked — the player may only start a
/// level once the server has fired <c>round_start</c> (phase IN_ROUND).
///
/// Patches <c>App.LaunchSinglePlayer</c> / <c>App.LaunchCustomLevel</c> with a
/// prefix that returns false (skips the launch) when the lock is active. The
/// plugin's own server-driven collection launch happens during IN_ROUND, so it is
/// never blocked. Disabled by config (<c>Features.EnableReadyLock</c>).
/// </summary>
internal static class ReadyLockPatches
{
    public static void Apply()
    {
        var harmony = new Harmony("TwilightCore.ReadyLock");
        Patch(harmony, "LaunchSinglePlayer");
        Patch(harmony, "LaunchCustomLevel");
    }

    private static void Patch(Harmony harmony, string methodName)
    {
        var original = AccessTools.Method(typeof(App), methodName);
        if (original == null)
        {
            Plugin.Logger.LogWarning($"[ReadyLock] App.{methodName} not found — patch skipped.");
            return;
        }
        harmony.Patch(original,
            prefix: new HarmonyMethod(typeof(ReadyLockPatches), nameof(BlockIfLocked)));
        Plugin.Logger.LogInfo($"[ReadyLock] patched App.{methodName}.");
    }

    /// <summary>Block the launch (return false) when the false-start lock is active.</summary>
    private static bool BlockIfLocked()
    {
        if (!TwilightConfig.EnableReadyLock.Value) return true;
        var session = MatchSession.Instance;
        if (session == null || !session.ReadyLockActive) return true;

        Plugin.Logger.LogInfo("[ReadyLock] blocked manual level launch (ready/countdown).");
        ChatView.Instance?.ShowInfo("[System] You've readied up — wait for the round to start.");
        return false;
    }
}
