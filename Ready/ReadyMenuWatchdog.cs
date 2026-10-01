using Multiplayer;
using TwilightCore.Chat;
using TwilightCore.Match;
using TwilightCore.Net;
using UnityEngine;

namespace TwilightCore.Ready;

/// <summary>
/// Own <c>!ready</c> only makes sense from the main menu: the auto countdown,
/// the held-scene preload (an additive load while a level is running hijacks
/// <c>Game.currentLevel</c> through the dormant Level's <c>Awake</c>) and the
/// round's own launch all assume the player is sitting in the menu. A player may
/// still be practising inside a level when they type <c>!ready</c> (allowed in
/// PREP), so this watchdog returns them to the menu before the follow-up match
/// logic runs.
///
/// <para>Why poll instead of reacting to an event:</para>
///
/// <list type="bullet">
/// <item><c>SceneManager.sceneLoaded</c> (which <see cref="Preload.ScenePreloadManager"/>
/// already hooks) fires too early — <c>Game.LoadLevel</c> waits on
/// <c>HasSceneLoaded</c>, then runs <c>AfterLoad</c>, and only afterwards calls
/// <c>onComplete()</c>, which is what sets <c>App.state = PlayLevel</c>. A handler
/// there still reads <c>LoadLevel</c>.</item>
/// <item>Reacting while <c>App.state == LoadLevel</c> is not an option:
/// <c>App.PauseLeave()</c> runs <c>ExitGame()</c> (which unloads the level) but
/// only branches back to the menu for <c>PlayLevel</c> / <c>ServerPlayLevel</c> /
/// <c>ClientPlayLevel</c> — from <c>LoadLevel</c> it would unload the level and
/// never <c>EnterMenu()</c>, leaving a broken in-between state.</item>
/// <item>The exact transition is observable via the <c>App.state</c> property
/// setter, but the game invokes it inside <c>onComplete()</c> while holding
/// <c>App.stateLock</c> and mid-way through the load-completion callback; acting
/// there would tear the level down re-entrantly. The safe point is the next
/// frame, i.e. exactly this poll.</item>
/// </list>
///
/// <para>Idempotent: <c>PauseLeave</c> flips <c>App.state</c> to <c>Menu</c>
/// synchronously, and the menu scene load re-runs the preload gates
/// (<see cref="Preload.ScenePreloadManager"/>'s <c>OnSceneLoaded</c>), which is
/// what actually starts the preload once the player is back at the menu.</para>
/// </summary>
internal sealed class ReadyMenuWatchdog : MonoBehaviour
{
    private void Update()
    {
        // Only a fully loaded single-player level matters; lobby / loading
        // states are either not ours to leave or not yet a level.
        if (App.state != AppSate.PlayLevel) return;

        var session = MatchSession.Instance;
        if (session == null || !session.IsAuthenticated || !session.IsPlayerSeat) return;
        if (!session.MyReady) return;
        // IN_ROUND legitimately has the player inside the round's level.
        if (session.Phase != MatchPhase.Prep && session.Phase != MatchPhase.Countdown) return;

        var app = App.instance;
        if (app == null) return;

        Plugin.Logger.LogInfo(
            $"[Twilight] !ready while inside a level (phase={session.Phase}) — returning to the main menu first.");
        ChatView.Instance?.ShowInfo("[System] You've readied up — returning to the main menu.");
        app.PauseLeave();
    }
}
