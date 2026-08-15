using System;
using Multiplayer;
using UnityEngine;

namespace TwilightCore;

/// <summary>
/// Registers the "lc" command group in the game's developer console
/// (opened with BackQuote / F1).
///
///     lc random [seconds]  — start a run with a random collection drawn from
///                             the config's level pool (RandomLevelCount /
///                             RandomLevelPool in LevelCollections.json).
///                             The generated collection is transient: it is
///                             never written to the config file.
///     lc restart [seconds]  — restart the current collection from its first level
///     lc skip [seconds]     — skip the current level and load the next one
///     lc abort              — cancel a pending delayed command
///
/// [seconds] is an optional positive integer: the command is executed after that
/// many seconds.  A delayed command is cancelled if the collection run ends
/// (or switches collection) before the delay elapses.  While a delayed command
/// is pending, new restart/skip/random commands are refused (use "lc abort" first).
///
/// "lc random" works without an active run (e.g. from the main menu); it starts
/// its own run.  restart/skip only work while a collection run is in progress
/// (single player).
/// </summary>
internal static class ConsoleCommands
{
    private const string HelpText =
        "lc <command> [seconds]\r\n" +
        "\trandom - start a run with a random collection drawn from the config level pool\r\n" +
        "\trestart - restart the current collection from level 1\r\n" +
        "\tskip - skip the current level and advance to the next one\r\n" +
        "\tabort - cancel a pending delayed command\r\n" +
        "\t[seconds] - optional delay in seconds; cancelled if the collection run ends first";

    public static void Register()
    {
        // Shell is the game's dev console (BackQuote/F1). RegisterCommand only
        // touches Shell's static command table, so it is safe to call before
        // the Shell scene object exists (Shell.Awake() registers "?"/"help" too).
        Shell.RegisterCommand("lc", OnLcCommand, HelpText);
        Plugin.Logger.LogInfo("LevelCollections: registered 'lc' console commands (random, restart, skip, abort).");
    }

    private static void OnLcCommand(string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            PrintHelp();
            return;
        }

        var mgr = CollectionManager.Instance;
        if (mgr == null || !mgr)
        {
            Print("lc: CollectionManager is not available yet.");
            return;
        }

        string[] parts = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant();

        // abort takes no arguments and must work any time a delay may be
        // pending (e.g. "lc random 10" scheduled from the main menu, with no
        // active run).
        if (cmd == "abort")
        {
            if (parts.Length > 1)
            {
                Print($"lc: invalid argument '{args}'. Usage: lc abort");
                return;
            }
            Abort(mgr);
            return;
        }

        int delaySeconds = 0;
        if (parts.Length > 1)
        {
            if (parts.Length != 2 || !int.TryParse(parts[1], out delaySeconds) || delaySeconds < 0)
            {
                Print($"lc: invalid argument '{args}'. Usage: lc {cmd} [seconds]");
                return;
            }
        }

        // random works without an active run — it generates a random collection
        // and starts the run itself.
        if (cmd == "random")
        {
            RandomCollection(mgr, delaySeconds);
            return;
        }

        // restart / skip require an active run.
        if (!mgr.IsInCollectionRun)
        {
            Print("lc: no collection run in progress. Start one from the Collections menu or use 'lc random'.");
            return;
        }

        if (cmd != "restart" && cmd != "skip")
        {
            PrintHelp();
            return;
        }

        // Refuse new commands while a delayed command is running.
        if (mgr.IsDelayedCommandPending)
        {
            Print("lc: a delayed command is already pending; use 'lc abort' to cancel it first.");
            return;
        }

        if (cmd == "restart")
            Restart(mgr, delaySeconds);
        else
            Skip(mgr, delaySeconds);
    }

    private static void Abort(CollectionManager mgr)
    {
        Print(mgr.CancelDelayedCommand()
            ? "lc: delayed command cancelled."
            : "lc: no delayed command pending.");
    }

    private static void RandomCollection(CollectionManager mgr, int delaySeconds)
    {
        void DoRandom()
        {
            if (!mgr.GenerateAndStartRandomCollection(out string message))
            {
                Print($"lc: {message}");
                return;
            }
            Print($"lc: starting random collection: {message}");
        }

        if (delaySeconds > 0)
        {
            if (!mgr.ScheduleStartAfterDelay(delaySeconds, DoRandom, "random"))
            {
                Print("lc: a delayed command is already pending; use 'lc abort' to cancel it first.");
                return;
            }
            Print($"lc: starting a random collection in {delaySeconds}s.");
            return;
        }

        DoRandom();
    }

    private static void Restart(CollectionManager mgr, int delaySeconds)
    {
        int collectionIndex = mgr.CurrentCollectionIndex;

        void DoRestart()
        {
            var col = mgr.CurrentCollection;
            Print($"lc: restarting collection '{(col != null ? col.Name : "?")}' from level 1.");
            if (mgr.IsTransientRun)
                mgr.StartTransientCollectionRun(col);
            else
                mgr.StartCollectionRun(collectionIndex, 0);
        }

        if (delaySeconds > 0)
        {
            if (!mgr.ScheduleAfterDelay(delaySeconds, collectionIndex, DoRestart, "restart"))
            {
                Print("lc: a delayed command is already pending; use 'lc abort' to cancel it first.");
                return;
            }
            var col = mgr.CurrentCollection;
            Print($"lc: restarting collection '{(col != null ? col.Name : "?")}' from level 1 in {delaySeconds}s.");
            return;
        }

        DoRestart();
    }

    private static void Skip(CollectionManager mgr, int delaySeconds)
    {
        int collectionIndex = mgr.CurrentCollectionIndex;

        void DoSkip()
        {
            var col = mgr.CurrentCollection;
            var curLevel = mgr.CurrentLevelId;
            int curIndex = mgr.CurrentLevelIndex;
            int total = col != null && col.Levels != null ? col.Levels.Count : 0;

            if (mgr.IsLastLevel)
            {
                Print($"lc: '{curLevel}' is the last level ({curIndex + 1}/{total}); completing the run.");
                mgr.AdvanceToNextLevel(true); // skipped=true → reporter marks N/A; ends the run
                return;
            }

            Print($"lc: skipping '{curLevel}' (level {curIndex + 1}/{total}).");
            mgr.AdvanceToNextLevel(true);
        }

        if (delaySeconds > 0)
        {
            if (!mgr.ScheduleAfterDelay(delaySeconds, collectionIndex, DoSkip, "skip"))
            {
                Print("lc: a delayed command is already pending; use 'lc abort' to cancel it first.");
                return;
            }
            Print($"lc: skipping '{mgr.CurrentLevelId}' in {delaySeconds}s.");
            return;
        }

        DoSkip();
    }

    private static void PrintHelp() => Print(HelpText);

    /// <summary>
    /// Shell.Print requires Shell.instance to be alive; fall back to the plugin
    /// logger if the console object isn't available yet (Unity fake-null check).
    /// </summary>
    private static void Print(string message)
    {
        if (Shell.instance != null && Shell.instance)
            Shell.Print(message);
        else
            Plugin.Logger.LogInfo(message);
    }
}
