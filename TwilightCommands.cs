using System;
using Multiplayer;
using TwilightCore.Match;
using TwilightCore.Net;
using TwilightCore.Timer;

namespace TwilightCore;

/// <summary>
/// The <c>twi</c> developer-console command group (open with BackQuote/F1).
///
///   twi connect &lt;host&gt; [port]    — log in &amp; connect to the server (host/port on the command line, not the cfg)
///   twi disconnect              — close the connection (no reconnect)
///   twi disconnect simulate     — simulate an unexpected drop (auto-reconnect follows)
///   twi status                  — show connection / match state
///   twi reload                  — hot-reload TwilightCore.cfg + LevelCollections.json from disk
///   twi finish                  — end the current round (project_complete; same as chat !finish)
///   twi sim level_done [ms]     — report current level/attempt complete (optional ms)
///   twi sim skip                — skip current level/attempt (N/A)
///   twi sim complete [final_ms] — force-complete the project
///   twi sim forfeit [reason]    — force-forfeit (multi_exit | single_exit_0_valid)
///   twi sim status              — simulated-timer state
///   twi preload hold &lt;levelId&gt;  — M1: additively hold a level's scene dormant (from the main menu)
///   twi preload swap            — M1: swap the held scene in (the GO sequence, no match flow)
///   twi preload drop            — discard the held scene
///   twi preload status          — held-scene preloader state
///
/// Connection never happens automatically — run <c>twi connect &lt;host&gt; [port]</c>
/// after setting <c>Account.Username/Password</c> in the cfg. Progress (login → WS →
/// auth_ok) is echoed to the console by <see cref="TwilightClient"/>.
/// </summary>
internal static class TwilightCommands
{
    private const int DefaultPort = 8443;

    private static TwilightClient _client;
    private static MatchSession _session;
    private static SimulatedTimer _timer;
    private static MatchController _match;

    private const string HelpText =
        "twi <command> [args]\r\n" +
        "\tconnect <host> [port] - log in & connect (host/port given here, not in the cfg)\r\n" +
        "\tdisconnect - close the connection (no reconnect)\r\n" +
        "\tdisconnect simulate - simulate an unexpected drop (auto-reconnect follows)\r\n" +
        "\tstatus - show connection / match state\r\n" +
        "\treload - hot-reload TwilightCore.cfg + LevelCollections.json from disk\r\n" +
        "\tfinish - end the current round (same as chat !finish)\r\n" +
        "\tsim level_done [ms] - report current level/attempt complete\r\n" +
        "\tsim skip - skip current level/attempt (N/A)\r\n" +
        "\tsim complete [final_ms] - force-complete the project\r\n" +
        "\tsim forfeit [multi_exit|single_exit_0_valid] - force-forfeit\r\n" +
        "\tsim status - simulated timer state\r\n" +
        "\tpreload hold <levelId> - M1: hold a level scene dormant (main menu only)\r\n" +
        "\tpreload swap - M1: swap the held scene in (no match flow)\r\n" +
        "\tpreload drop - discard the held scene\r\n" +
        "\tpreload status - held-scene preloader state";

    public static void Init(TwilightClient client, MatchSession session, SimulatedTimer timer, MatchController match)
    {
        _client = client;
        _session = session;
        _timer = timer;
        _match = match;
        Shell.RegisterCommand("twi", OnCommand, HelpText);
        Plugin.Logger.LogInfo("TwilightCore: registered 'twi' console commands.");
    }

    private static void OnCommand(string args)
    {
        if (string.IsNullOrEmpty(args)) { PrintHelp(); return; }
        var parts = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "connect":
                if (_client == null) { TwilightLog.Print("twi: client not initialised."); return; }
                string host = parts.Length > 1 ? parts[1] : null;
                int port = DefaultPort;
                if (parts.Length > 2 && int.TryParse(parts[2], out int p) && p > 0) port = p;
                _client.StartConnect(host, port);   // echoes login/WS/auth progress to the console
                return;
            case "disconnect":
                if (_client == null) return;
                if (parts.Length > 1 && parts[1].ToLowerInvariant() == "simulate")
                {
                    _client.SimulateDisconnect();
                    return;
                }
                _client.Stop();
                TwilightLog.Print("twi: disconnected.");
                return;
            case "status":
                PrintStatus();
                return;
            case "reload":
                HandleReload();
                return;
            case "finish":
                if (_match == null) { TwilightLog.Print("twi: match controller not initialised."); return; }
                _match.FinishRound();
                return;
            case "sim":
                HandleSim(parts);
                return;
            case "preload":
                HandlePreload(parts);
                return;
            default:
                PrintHelp();
                return;
        }
    }

    private static void HandleSim(string[] parts)
    {
        if (_timer == null) { TwilightLog.Print("twi sim: timer not available."); return; }
        if (parts.Length < 2) { TwilightLog.Print("twi sim <level_done|skip|complete|forfeit|status>"); return; }
        var mgr = CollectionManager.Instance;
        string sub = parts[1].ToLowerInvariant();

        switch (sub)
        {
            case "level_done":
            {
                long? ms = null;
                if (parts.Length >= 3 && long.TryParse(parts[2], out long v)) ms = v;
                _timer.PrepareSimLevelMs(ms);
                if (mgr == null || !mgr.IsInCollectionRun)
                    TwilightLog.Print("twi sim: no active collection run.");
                else if (mgr.IsSameLevelTransitionPending)
                    TwilightLog.Print("twi sim: a same-level transition is in progress.");
                else
                    mgr.AdvanceToNextLevel(false);
                return;
            }
            case "skip":
                if (mgr == null || !mgr.IsInCollectionRun)
                    TwilightLog.Print("twi sim: no active collection run.");
                else if (mgr.IsSameLevelTransitionPending)
                    TwilightLog.Print("twi sim: a same-level transition is in progress.");
                else
                    mgr.AdvanceToNextLevel(true);
                return;
            case "complete":
            {
                long? ms = null;
                if (parts.Length >= 3 && long.TryParse(parts[2], out long v)) ms = v;
                _timer.PrepareSimFinalMs(ms);
                if (mgr != null && mgr.IsInCollectionRun) mgr.EndCollectionRun();
                else TwilightLog.Print("twi sim: no active collection run.");
                return;
            }
            case "forfeit":
            {
                string reason = parts.Length >= 3 ? parts[2] : null;
                _timer.PrepareSimForfeitReason(reason);
                if (mgr != null && mgr.IsInCollectionRun) mgr.AbortCollectionRun();
                else TwilightLog.Print("twi sim: no active collection run.");
                return;
            }
            case "status":
                var p = TimerProviderRegistry.Current;
                if (p != null)
                {
                    TwilightLog.Print(
                        $"provider: api={p.ApiVersion} match={p.InMatchMode} round={p.InRound} " +
                        $"segment={p.IsInSegment} cur={p.CurrentSegmentMs}ms total={p.RoundTotalMs}ms " +
                        $"valid={p.ValidAttemptCount} invalid={p.GetActiveInvalidMarks().Count}");
                }
                if (_timer != null)
                    TwilightLog.Print(_timer.StatusString());
                return;
            default:
                TwilightLog.Print("twi sim <level_done|skip|complete|forfeit|status>");
                return;
        }
    }

    /// <summary>
    /// M1 prototype commands for the held-scene preloader (激进预载held-scene
    /// 方案调研.md §8 M1): manually exercise hold → swap → drop from the main
    /// menu without a match flow, to verify the HumanAPI side-effect / flash-frame
    /// / additive-semantics risks (调研 §7 1-3) before wiring M2 into real rounds.
    /// </summary>
    private static void HandlePreload(string[] parts)
    {
        var pre = Preload.ScenePreloadManager.Instance;
        if (pre == null) { TwilightLog.Print("twi preload: preloader not initialised."); return; }
        if (parts.Length < 2) { TwilightLog.Print("twi preload <hold <levelId>|swap|drop|status>"); return; }

        switch (parts[1].ToLowerInvariant())
        {
            case "hold":
                if (parts.Length < 3) TwilightLog.Print("twi preload hold <levelId>   (e.g. Aztec or a workshop id)");
                else pre.DebugHold(parts[2]);
                return;
            case "swap":
                pre.DebugSwap();
                return;
            case "drop":
                pre.ForceDrop("twi preload drop");
                TwilightLog.Print("twi preload: drop requested.");
                return;
            case "status":
                TwilightLog.Print(pre.StatusString());
                return;
            default:
                TwilightLog.Print("twi preload <hold <levelId>|swap|drop|status>");
                return;
        }
    }

    private static void PrintStatus()
    {
        if (_session == null) { TwilightLog.Print("twi: no session."); return; }
        TwilightLog.Print(
            $"config: target={(_client != null ? (_client.ActiveTarget ?? "(not set)") : "(no client)")} " +
            $"user={(string.IsNullOrEmpty(TwilightConfig.Username.Value) ? "(empty)" : "set")} " +
            $"seat={(string.IsNullOrEmpty(TwilightConfig.Seat.Value) ? "auto" : TwilightConfig.Seat.Value)}");
        TwilightLog.Print($"connected={(_client != null && _client.IsConnected)} auth={_session.IsAuthenticated} " +
              $"seat={_session.Seat ?? "-"} phase={_session.Phase} round={_session.RoundId ?? "-"}");
        if (_session.Pick != null)
            TwilightLog.Print($"pick={_session.Pick.Code} {_session.Pick.Name} type={_session.Pick.Type} retry={_session.Pick.RetryCount}");
    }

    private static void PrintHelp() => TwilightLog.Print(HelpText);

    /// <summary>
    /// Hot-reload both config sources from disk:
    /// <list type="bullet">
    /// <item><c>BepInEx/config/TwilightCore.cfg</c> — server/account/net/features/chat/debug bindings.</item>
    /// <item><c>BepInEx/config/LevelCollections.json</c> — the LevelCollections pool (via ConfigLoader.Reload).</item>
    /// </list>
    /// Lets the user edit either file and apply it without restarting the game. The collection
    /// menu (if open) keeps its already-built list; reopening it reflects the reloaded pool.
    /// </summary>
    private static void HandleReload()
    {
        string cfgStatus = TwilightConfig.Reload();
        TwilightLog.Print("twi reload: " + cfgStatus);

        try
        {
            ConfigLoader.Reload();
            TwilightLog.Print($"twi reload: LevelCollections.json reloaded ({ConfigLoader.Collections.Count} collection[s]).");
        }
        catch (System.Exception ex)
        {
            Plugin.Logger.LogError("[twi reload] LevelCollections reload failed: " + ex.Message);
            TwilightLog.Print("twi reload: LevelCollections.json reload failed — " + ex.Message);
        }
    }
}
