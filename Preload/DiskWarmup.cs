using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using HumanAPI;
using Multiplayer;
using TwilightCore.Match;
using TwilightCore.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwilightCore.Preload;

/// <summary>
/// Conservative preload — disk cache warmup: during PREP, a single background
/// thread pre-reads the announced collection's upcoming level files into the
/// OS page cache, so the in-round chained additive loads read from memory
/// instead of disk (only the deserialisation/integration CPU remains).
/// Strictly an optimisation: pure file IO (no scene/lightmap/probe systems
/// involved, no Unity API on the worker thread), one fixed reusable read
/// buffer (no process-memory growth — the page cache is kernel-managed and
/// reclaimable), and any failure silently degrades to the status quo.
///
/// <para>Lifecycle, driven by <see cref="ScenePreloadManager"/> on the main
/// thread: <see cref="OnPickAnnounced"/> retains the announced collection's
/// levels AFTER the first (the aggressive hold reads the first level itself);
/// <see cref="MaybeStart"/> runs only from the points where the first-level
/// hold is dormant AND its done report went out — warming never competes with
/// the round_start critical path; <see cref="Cancel"/> fires when the phase
/// leaves PREP/COUNTDOWN (round start or abort) or a new pick_announced
/// arrives. Strategy (<c>Features.DiskWarmupMode</c>): "prep-all" warms the
/// whole collection at PREP and nothing warms mid-round; "follow-chain"
/// (default) head-warms only the levels right after the first, then each
/// completed chained hold triggers <see cref="WarmAfterChainedHold"/> for the
/// level after next — mild background reads inside the disk-idle window
/// (previous hold done, next hold not started), pages used within ~one level,
/// and local <c>lc</c> runs covered too. The standard fallback load path
/// benefits equally in both modes.</para>
///
/// <para>File mapping: built-in/editor-pick levels resolve their scene name
/// through the game's own level tables, then the player's build-settings scene
/// table (<see cref="SceneUtility.GetScenePathByBuildIndex"/> — build index N
/// is serialized in <c>Human_Data/level{N}</c>; <c>Game.levels[]</c> indices
/// are NOT build indices) maps it to the file. If any built-in target fails
/// to resolve, ALL <c>level*</c> files are warmed instead (a bounded,
/// page-cache-only fallback). Workshop items warm exactly the two files their
/// load path reads — <c>metadata.json</c> and the <c>data</c> bundle, via the
/// game's public <see cref="FileTools.ToLocalPath"/>; missing/uninstalled
/// items are skipped and never downloaded.</para>
///
/// <para>Every public entry point is main-thread only. The worker sees an
/// immutable path list plus a per-run volatile cancel flag, and its whole
/// loop sits under a top-level catch — an unhandled exception on a background
/// thread kills the entire Mono runtime.</para>
/// </summary>
internal static class DiskWarmup
{
    // ── state (everything mutable under _gate; Cancelled is volatile for the worker) ──

    private static readonly object _gate = new object();

    private static Thread _thread;        // the one worker: spawned on demand, exits when idle
    private static WarmRun _queued;       // built by the main thread, not yet picked up by the worker
    private static WarmRun _active;       // owned by the worker while it runs
    private static WarmRun _lastFinished; // status display after the worker exits

    // Announced-collection snapshot (PREVIEW only — round_start stays authoritative).
    private static List<string> _armedLevels = new List<string>(); // canonical, first level dropped, deduped in order
    private static string _armedKey = "";
    private static int _armedGeneration;        // bumped by every OnPickAnnounced
    private static int _startedGeneration = -1; // the generation MaybeStart already launched (idempotence)

    // Scene-name → build-index table, built once per process by SceneTable().
    private static Dictionary<string, int> _sceneTable = new Dictionary<string, int>();
    private static bool _sceneTableBuilt;

    private static bool _modeWarned; // one-shot warning for an unknown DiskWarmupMode value

    private enum WarmMode
    {
        PrepAll,      // warm the whole collection during PREP
        FollowChain,  // PREP head-warm + warm the level-after-next after each chained hold
    }

    /// <summary>Current strategy from <c>Features.DiskWarmupMode</c> ("prep-all" | "follow-chain"; anything else warns once and defaults to follow-chain). Main thread only.</summary>
    private static WarmMode Mode
    {
        get
        {
            var entry = TwilightConfig.DiskWarmupMode;
            if (entry != null && string.Equals(entry.Value, "prep-all", StringComparison.OrdinalIgnoreCase))
                return WarmMode.PrepAll;
            if (entry != null && string.Equals(entry.Value, "follow-chain", StringComparison.OrdinalIgnoreCase))
                return WarmMode.FollowChain;
            if (entry != null && !_modeWarned && !string.IsNullOrEmpty(entry.Value))
            {
                _modeWarned = true;
                Plugin.Logger.LogWarning($"[Preload] disk warm: unknown Features.DiskWarmupMode '{entry.Value}' — using follow-chain");
            }
            return WarmMode.FollowChain;
        }
    }

    // ── match-flow entry points (main thread, called from ScenePreloadManager) ──

    /// <summary>
    /// A <c>pick_announced</c> arrived: retain (or, for a SINGLE pick, drop)
    /// the warming snapshot. The first level is excluded — the aggressive
    /// hold reads it during PREP, so re-reading it here would be pure
    /// redundancy. Any run in flight is cancelled; a changed pick restarts
    /// under the new list, and re-reading already-warm pages is effectively
    /// free, so no effort is made to preserve work across announcements.
    /// </summary>
    internal static void OnPickAnnounced(List<object> rawLevels, bool multiPick)
    {
        lock (_gate)
        {
            _armedGeneration++;
            _armedLevels = new List<string>();
            _armedKey = "";
            _queued = null;
            if (_active != null && !_active.Cancelled)
            {
                _active.EndReason = "pick_announced";
                _active.Cancelled = true;
            }
        }

        if (!multiPick || rawLevels == null) return;

        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var levels = new List<string>();
            bool droppedFirst = false;
            for (int i = 0; i < rawLevels.Count; i++)
            {
                if (rawLevels[i] == null) continue;
                string id = CollectionManager.CanonicalizeLevelId(rawLevels[i].ToString());
                if (string.IsNullOrEmpty(id)) continue;
                if (!droppedFirst)
                {
                    droppedFirst = true; // the aggressive hold owns the first level's IO
                    continue;
                }
                if (!seen.Add(id)) continue;
                levels.Add(id);
            }

            lock (_gate)
            {
                _armedLevels = levels;
                _armedKey = string.Join("|", levels.ToArray());
            }
            if (TwilightConfig.PreloadDebugLogging && levels.Count > 0)
                Plugin.Logger.LogInfo($"[Preload] disk warm armed: {levels.Count} levels after the first ({_armedKey})");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Preload] disk warm arm failed (ignored): {e.Message}");
        }
    }

    /// <summary>
    /// Idempotent start — called only from the three points where the
    /// first-level hold is dormant and its done report went out, so the
    /// warming never competes with the round_start load. Gates mirror
    /// <c>MaybeStartPreload</c>, except the phase deliberately includes
    /// COUNTDOWN: the hold pipeline can land dormant after a force-started
    /// countdown began, and warming through a 3-second countdown is exactly
    /// the intended use of that window.
    /// </summary>
    internal static void MaybeStart()
    {
        try
        {
            if (TwilightConfig.EnableDiskCacheWarmup == null || !TwilightConfig.EnableDiskCacheWarmup.Value) return;
            var session = MatchSession.Instance;
            if (session == null || !session.IsAuthenticated || !session.IsPlayerSeat) return;
            if (session.Phase != MatchPhase.Prep && session.Phase != MatchPhase.Countdown) return;
            if (!session.MyReady) return;
            // Never warm while a level is running (the menu check doubles as a
            // defense for debug-hold flows that bypass the match gates).
            if (App.state != AppSate.Menu || !NetGame.isLocal) return;

            List<string> levels;
            string key;
            lock (_gate)
            {
                if (_armedLevels.Count == 0) return;
                if (_startedGeneration == _armedGeneration) return; // already launched for this announcement
                _startedGeneration = _armedGeneration;              // mark before building — a build failure must not retry-loop
                levels = _armedLevels;
                key = _armedKey;
            }

            // follow-chain only head-warms here (the levels right after the
            // first); prep-all warms the whole announced collection upfront.
            if (Mode == WarmMode.FollowChain && levels.Count > 2)
            {
                var head = levels.GetRange(0, 2);
                WarmLevels(head, "pick-head " + string.Join("|", head.ToArray()));
            }
            else
            {
                WarmLevels(levels, "pick " + key);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Preload] disk warm start failed (ignored): {e.Message}");
        }
    }

    /// <summary>
    /// A CHAINED hold just went dormant while playing level N — warm level N+2
    /// now, in the disk-idle window before the N+1 swap starts the next hold.
    /// follow-chain mode only; deliberately NO match gates, because chained
    /// holds also run in local <c>lc</c> collection runs without a server.
    /// Stays stateless: re-warming already-hot pages is free, so repeated
    /// calls (self-heal re-holds) cost a warm re-read at worst.
    /// </summary>
    internal static void WarmAfterChainedHold()
    {
        try
        {
            if (TwilightConfig.EnableDiskCacheWarmup == null || !TwilightConfig.EnableDiskCacheWarmup.Value) return;
            if (Mode != WarmMode.FollowChain) return;
            var mgr = CollectionManager.Instance;
            var col = mgr != null ? mgr.CurrentCollection : null;
            if (col == null || col.Levels == null) return;
            int afterNext = mgr.CurrentLevelIndex + 2;
            if (afterNext < 0 || afterNext >= col.Levels.Count) return; // final stretch — nothing left ahead
            // A repeat of the level that was just held reads the same files the
            // hold already loaded — skip the pointless run.
            if (afterNext > 0 && col.Levels[afterNext] == col.Levels[afterNext - 1]) return;
            string id = CollectionManager.CanonicalizeLevelId(col.Levels[afterNext]);
            if (string.IsNullOrEmpty(id)) return;
            WarmLevels(new List<string> { id }, "chain " + id);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Preload] disk warm chain start failed (ignored): {e.Message}");
        }
    }

    /// <summary>Build + start a warming run (main thread); returns the queued file count. Shared by the PREP entry, the follow-chain entry and the manual commands.</summary>
    private static int WarmLevels(List<string> levels, string key)
    {
        var files = BuildFileList(levels);
        if (files.Count == 0)
        {
            Plugin.Logger.LogInfo("[Preload] disk warm: nothing to warm (empty file list).");
            return 0;
        }
        Start(files, key);
        return files.Count;
    }

    /// <summary>
    /// Cooperatively cancel: the current file finishes, then the run stops.
    /// A queued (not yet started) run is dropped whole. Safe no-op.
    /// </summary>
    internal static void Cancel(string reason)
    {
        lock (_gate)
        {
            _queued = null;
            if (_active != null && !_active.Cancelled)
            {
                // EndReason first: the volatile Cancelled write publishes it to the worker.
                _active.EndReason = reason;
                _active.Cancelled = true;
            }
        }
    }

    /// <summary>
    /// Cancel only a PREP-started run (key "pick…"). Used at the round-start
    /// swap-in so lingering PREP warming stops before the round's own loads
    /// begin — while follow-chain runs (key "chain…", launched after chained
    /// holds mid-round) pass through untouched.
    /// </summary>
    internal static void CancelPrepWarmup(string reason)
    {
        lock (_gate)
        {
            if (_queued != null && IsPrepRun(_queued)) _queued = null;
            if (_active != null && !_active.Cancelled && IsPrepRun(_active))
            {
                _active.EndReason = reason;
                _active.Cancelled = true;
            }
        }
    }

    private static bool IsPrepRun(WarmRun run)
    {
        return run.Key.StartsWith("pick", StringComparison.Ordinal);
    }

    // ── manual / debug entry points (`twi preload warm …`, main thread) ──

    /// <summary>Warm one level's file set plus the shared companions (bypasses the match gates).</summary>
    internal static void WarmLevel(string levelId)
    {
        try
        {
            string canonical = CollectionManager.CanonicalizeLevelId(levelId);
            // Pre-validate: an unknown id would otherwise take the warm-ALL
            // fallback inside BuildFileList — a typo should not warm the game.
            if (!CollectionManager.ValidateLevelId(canonical, out bool isMissing))
            {
                TwilightLog.Print(isMissing
                    ? $"twi preload warm: '{canonical}' is a workshop level that is not subscribed/installed."
                    : $"twi preload warm: unknown level id '{canonical}'.");
                return;
            }
            int queued = WarmLevels(new List<string> { canonical }, "manual " + canonical);
            if (queued > 0)
                TwilightLog.Print($"twi preload warm: queued {queued} files for '{canonical}'.");
            else
                TwilightLog.Print($"twi preload warm: nothing to warm for '{canonical}'.");
        }
        catch (Exception e)
        {
            TwilightLog.Print("twi preload warm failed: " + e.Message);
        }
    }

    /// <summary>
    /// Warm every scene file in the build-settings table (glob fallback when
    /// the table is unusable) plus the shared companions — also the field
    /// experiment for the scene-table mapping: the per-file log shows exactly
    /// what the table resolved.
    /// </summary>
    internal static void WarmAllLevels()
    {
        try
        {
            string dataPath = Application.dataPath;
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var table = SceneTable();
            List<int> indices = table.Count > 0 ? new List<int>(table.Values) : null;
            if (indices != null)
            {
                indices.Sort();
                foreach (int idx in indices)
                    AddIfExists(files, seen, Path.Combine(dataPath, "level" + idx));
            }
            if (files.Count == 0)
            {
                // Table unusable → warm every scene file the old way.
                indices = null;
                foreach (string f in Directory.GetFiles(dataPath, "level*"))
                    AddIfExists(files, seen, f);
            }
            AppendSharedFiles(files, seen, dataPath, indices);

            if (files.Count == 0)
            {
                TwilightLog.Print("twi preload warm all: no files found.");
                return;
            }
            Start(files, "manual all");
            TwilightLog.Print($"twi preload warm: queued {files.Count} files (all scenes).");
        }
        catch (Exception e)
        {
            TwilightLog.Print("twi preload warm all failed: " + e.Message);
        }
    }

    /// <summary>Status line for `twi preload status` (R4.2): armed / queued / running / done / cancelled.</summary>
    internal static string StatusLine()
    {
        try
        {
            lock (_gate)
            {
                if (TwilightConfig.EnableDiskCacheWarmup == null || !TwilightConfig.EnableDiskCacheWarmup.Value)
                    return "warm: off (Features.EnableDiskCacheWarmup=false)";

                string armed = _armedLevels.Count > 0 ? $"'{_armedKey}' ({_armedLevels.Count} levels)" : "(none)";

                string run;
                if (_active != null)
                    run = $"running {_active.Done}/{_active.Files.Length} files, {_active.BytesDone / 1048576.0:0} MB, cur='{FileName(_active.CurrentFile)}'";
                else if (_queued != null)
                    run = $"queued {_queued.Files.Length} files";
                else if (_lastFinished != null)
                {
                    var last = _lastFinished;
                    if (last.State == WarmState.Cancelled)
                        run = $"cancelled after {last.Done}/{last.Files.Length} files ({last.EndReason})";
                    else
                        run = $"done {last.Done}/{last.Files.Length} files, {last.BytesDone / 1048576.0:0.0} MB in {last.ElapsedSec:0.0} s (skipped {last.Skipped})";
                }
                else
                    run = "never run";

                return $"warm: {run}; armed: {armed}; mode: {(Mode == WarmMode.PrepAll ? "prep-all" : "follow-chain")}";
            }
        }
        catch (Exception e)
        {
            return "warm: status failed: " + e.Message;
        }
    }

    // ── file-list construction (main thread) ──

    /// <summary>
    /// Resolve level ids to physical files. Whole body degrades to an empty
    /// (or partial) list on any error — a warmup failure must never affect
    /// the match flow. Missing workshop files are skipped, never downloaded.
    /// </summary>
    private static List<string> BuildFileList(List<string> levels)
    {
        var files = new List<string>();
        try
        {
            string dataPath = Application.dataPath;
            var table = SceneTable();

            var sceneFiles = new List<string>();
            var sceneSeen = new HashSet<string>(StringComparer.Ordinal);
            var sceneIndices = new List<int>(); // resolved build indices → adjacent companions
            var seen = new HashSet<string>(StringComparer.Ordinal); // workshop files + companions
            bool sceneFallback = false;

            foreach (string id in levels)
            {
                var type = CollectionManager.ResolveLevelType(id);
                if (type == WorkshopItemSource.BuiltIn || type == WorkshopItemSource.EditorPick)
                {
                    // ValidateLevelId gates the index lookups: both Find*LevelIndex
                    // helpers fall back to index 0 (Intro) for unknown names.
                    if (!CollectionManager.ValidateLevelId(id, out _))
                    {
                        Plugin.Logger.LogInfo($"[Preload] disk warm: '{id}' failed validation (skipped).");
                        sceneFallback = true;
                        continue;
                    }
                    string sceneName = SceneNameFor(type, id);
                    if (sceneName == null)
                    {
                        Plugin.Logger.LogInfo($"[Preload] disk warm: '{id}' has no scene name (skipped).");
                        sceneFallback = true;
                        continue;
                    }
                    if (!table.TryGetValue(sceneName, out int buildIndex))
                    {
                        Plugin.Logger.LogInfo($"[Preload] disk warm: scene '{sceneName}' not in the build-settings table — falling back to ALL level files.");
                        sceneFallback = true;
                        continue;
                    }
                    AddIfExists(sceneFiles, sceneSeen, Path.Combine(dataPath, "level" + buildIndex));
                    sceneIndices.Add(buildIndex);
                }
                else
                {
                    // Workshop load reads exactly two files: metadata.json and the
                    // data bundle. ResolveWorkshopMetadata only ever sees installed
                    // items (the startup scan registers those) — never call
                    // levelRepo.LoadLevel here, that is the download path.
                    var meta = CollectionManager.ResolveWorkshopMetadata(id);
                    if (meta == null)
                    {
                        Plugin.Logger.LogInfo($"[Preload] disk warm: workshop '{id}' not installed (skipped, not downloaded).");
                        continue;
                    }
                    AddLocalFile(files, seen, meta.metaPath, id);
                    AddLocalFile(files, seen, meta.dataPath, id);
                }
            }

            if (sceneFallback)
            {
                // Any unresolved built-in target invalidates the precise mapping —
                // warm every scene file instead (bounded; page-cache only). The
                // companions follow: adjacent indices are meaningless when the
                // scene set is "all of them", so they glob too.
                sceneFiles.Clear();
                sceneSeen.Clear();
                sceneIndices = null;
                foreach (string f in Directory.GetFiles(dataPath, "level*"))
                    AddIfExists(sceneFiles, sceneSeen, f);
            }
            files.AddRange(sceneFiles);
            AppendSharedFiles(files, seen, dataPath, sceneIndices);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Preload] disk warm file-list build failed (ignored): {e.Message}");
        }
        return files;
    }

    /// <summary>
    /// The player's scene-name → build-index table, built once per process
    /// from the engine's own build-settings list. Build index N's serialized
    /// scene lives in <c>Human_Data/level{N}</c> (standard player layout).
    /// </summary>
    private static Dictionary<string, int> SceneTable()
    {
        if (_sceneTableBuilt) return _sceneTable;
        _sceneTableBuilt = true;
        _sceneTable = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int count = SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < count; i++)
            {
                string path = SceneUtility.GetScenePathByBuildIndex(i);
                if (string.IsNullOrEmpty(path)) continue;
                string name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
                if (name.Length == 0 || _sceneTable.ContainsKey(name)) continue;
                _sceneTable.Add(name, i);
            }
            Plugin.Logger.LogInfo(
                $"[Preload] disk warm: build-settings scene table: {count} build scenes, {_sceneTable.Count} mapped");
            if (TwilightConfig.PreloadDebugLogging)
                Plugin.Logger.LogInfo("[Preload] disk warm scene table: " + string.Join(", ", Keys(_sceneTable)));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Preload] disk warm scene-table build failed: {e.Message}");
        }
        return _sceneTable;
    }

    /// <summary>Scene name via the game's own level tables (index lookups pre-gated by ValidateLevelId).</summary>
    private static string SceneNameFor(WorkshopItemSource type, string levelId)
    {
        if (Game.instance == null) return null;
        if (type == WorkshopItemSource.BuiltIn)
        {
            int idx = (int)CollectionManager.FindBuiltInLevelIndex(levelId);
            var levels = Game.instance.levels;
            if (idx < 0 || idx >= levels.Length) return null;
            return levels[idx];
        }
        int eidx = (int)CollectionManager.FindEditorPickLevelIndex(levelId);
        var picks = Game.instance.editorPickLevels;
        if (picks == null || eidx < 0 || eidx >= picks.Length) return null;
        return picks[eidx];
    }

    /// <summary>
    /// Shared companions: the player lays out one sharedassets trio per build
    /// scene (sharedassets{N}.* ↔ level{N}), so warming the trios ADJACENT to
    /// the warmed scenes covers what their loads fault in — roughly 600MB for
    /// a full 13-level collection instead of the ~4.1GB of every shared file
    /// (real-machine inventory). A null index list (scene-table fallback /
    /// glob mode) warms all shared files. Adjacent misses are quiet: a scene
    /// without shared assets legitimately has no trio. resources.assets rides
    /// along (tiny); globalgamemanagers is excluded — read once at process
    /// startup and stays resident.
    /// </summary>
    private static void AppendSharedFiles(List<string> files, HashSet<string> seen, string dataPath, List<int> sceneIndices)
    {
        if (TwilightConfig.DiskWarmupWarmSharedFiles == null || !TwilightConfig.DiskWarmupWarmSharedFiles.Value) return;
        try
        {
            if (sceneIndices != null)
            {
                foreach (int idx in sceneIndices)
                {
                    AddIfExists(files, seen, Path.Combine(dataPath, "sharedassets" + idx + ".assets"), quiet: true);
                    AddIfExists(files, seen, Path.Combine(dataPath, "sharedassets" + idx + ".assets.resS"), quiet: true);
                    AddIfExists(files, seen, Path.Combine(dataPath, "sharedassets" + idx + ".resource"), quiet: true);
                }
            }
            else
            {
                foreach (string f in Directory.GetFiles(dataPath, "sharedassets*"))
                    AddIfExists(files, seen, f, quiet: true);
            }
            AddIfExists(files, seen, Path.Combine(dataPath, "resources.assets"), quiet: true);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Preload] disk warm shared-file enumeration failed (ignored): {e.Message}");
        }
    }

    /// <summary>Map a logical workshop path (<c>ws:</c>/<c>lvl:</c> prefixed) through the game's own resolver; skip+log when absent.</summary>
    private static void AddLocalFile(List<string> files, HashSet<string> seen, string logicalPath, string levelId)
    {
        try
        {
            string physical = FileTools.ToLocalPath(logicalPath);
            if (string.IsNullOrEmpty(physical) || !File.Exists(physical))
            {
                Plugin.Logger.LogInfo($"[Preload] disk warm: workshop file missing for '{levelId}' (skipped): {logicalPath}");
                return;
            }
            AddIfExists(files, seen, physical);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogInfo($"[Preload] disk warm: workshop path resolve failed for '{levelId}' (skipped): {e.Message}");
        }
    }

    /// <summary>
    /// Dedupe + existence check. Loud by default (a missing levelN is the
    /// mapping-failure signal); <paramref name="quiet"/> for best-effort
    /// companion files whose absence can be legitimate.
    /// </summary>
    private static void AddIfExists(List<string> files, HashSet<string> seen, string path, bool quiet = false)
    {
        if (seen.Contains(path)) return;
        if (!File.Exists(path))
        {
            if (!quiet)
                Plugin.Logger.LogInfo($"[Preload] disk warm: file missing (skipped): {path}");
            return;
        }
        seen.Add(path);
        files.Add(path);
    }

    private static string FileName(string path)
    {
        return string.IsNullOrEmpty(path) ? "-" : Path.GetFileName(path);
    }

    private static string[] Keys(Dictionary<string, int> table)
    {
        var keys = new string[table.Count];
        table.Keys.CopyTo(keys, 0);
        Array.Sort(keys);
        return keys;
    }

    // ── worker (the ONLY background thread; sees nothing but paths) ──

    private static void Start(List<string> files, string key)
    {
        var run = new WarmRun(key, files.ToArray())
        {
            Verbose = TwilightConfig.PreloadDebugLogging,
        };
        lock (_gate)
        {
            // Single-slot queue: a newer request replaces a queued one; an
            // active run is cancelled cooperatively (it checks between files).
            _queued = run;
            if (_active != null && !_active.Cancelled)
            {
                _active.EndReason = "superseded";
                _active.Cancelled = true;
            }
            if (_thread == null || !_thread.IsAlive)
            {
                _thread = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "TwilightCore-DiskWarmup",
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                };
                _thread.Start();
            }
        }
    }

    private static void WorkerLoop()
    {
        // Top-level guard — an unhandled exception on a background thread
        // kills the whole Mono runtime; this must never take the game down.
        try
        {
            var buffer = new byte[1024 * 1024]; // the single fixed reusable read buffer
            while (true)
            {
                WarmRun run;
                lock (_gate)
                {
                    if (_queued == null)
                    {
                        _thread = null; // idle → exit; at most one worker ever exists
                        return;
                    }
                    run = _queued;
                    _queued = null;
                    _active = run;
                }

                try
                {
                    RunOne(run, buffer);
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning($"[Preload] disk warm run crashed (ignored): {e.Message}");
                }
                finally
                {
                    lock (_gate)
                    {
                        if (_active == run) _active = null;
                        _lastFinished = run;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[Preload] disk warm worker crashed (ignored): {e.Message}");
            lock (_gate)
            {
                _active = null;
                _queued = null;
                _thread = null;
            }
        }
    }

    private static void RunOne(WarmRun run, byte[] buffer)
    {
        run.State = WarmState.Running;
        Plugin.Logger.LogInfo($"[Preload] disk warm start ({run.Key}): {run.Files.Length} files");
        var watch = Stopwatch.StartNew();

        for (int i = 0; i < run.Files.Length; i++)
        {
            if (run.Cancelled)
            {
                // Cancel checks happen between files only — the current file
                // always finishes (cooperative, bounded stop).
                lock (_gate) { run.State = WarmState.Cancelled; }
                Plugin.Logger.LogInfo(
                    $"[Preload] disk warm aborted ({run.Key}): {run.Done}/{run.Files.Length} files, {run.BytesDone / 1048576.0:0.0} MB ({run.EndReason})");
                if (run.Verbose)
                    Plugin.Logger.LogInfo("[Preload] disk warm aborted: remaining: " + RemainingList(run, i));
                return;
            }

            string path = run.Files[i];
            long bytes = 0;
            var fileWatch = Stopwatch.StartNew();
            bool ok = true;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 14, FileOptions.SequentialScan))
                {
                    int n;
                    while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                        bytes += n;
                }
            }
            catch (Exception e)
            {
                ok = false; // single-file IO failure → skip and continue
                if (run.Verbose)
                    Plugin.Logger.LogInfo($"[Preload] disk warm [{i + 1}/{run.Files.Length}] {path}: FAILED {e.Message} (skipped)");
            }

            double secs = fileWatch.Elapsed.TotalSeconds;
            lock (_gate)
            {
                run.Done++;
                if (!ok) run.Skipped++;
                run.BytesDone += bytes;
                run.CurrentFile = path;
            }
            if (ok && run.Verbose)
            {
                double mbps = secs > 0.001 ? bytes / 1048576.0 / secs : 0;
                Plugin.Logger.LogInfo($"[Preload] disk warm [{i + 1}/{run.Files.Length}] {path}: {bytes / 1048576.0:0.0} MB in {secs:0.00} s ({mbps:0} MB/s)");
            }
        }

        lock (_gate)
        {
            run.State = WarmState.Done;
            run.ElapsedSec = watch.Elapsed.TotalSeconds;
        }
        Plugin.Logger.LogInfo(
            $"[Preload] disk warm done ({run.Key}): {run.Done} files, {run.BytesDone / 1048576.0:0.0} MB in {run.ElapsedSec:0.0} s (skipped {run.Skipped})");
    }

    private static string RemainingList(WarmRun run, int fromIndex)
    {
        var names = new List<string>();
        for (int i = fromIndex; i < run.Files.Length; i++)
            names.Add(FileName(run.Files[i]));
        return names.Count == 0 ? "(none)" : string.Join(", ", names.ToArray());
    }

    private enum WarmState
    {
        Queued,
        Running,
        Done,
        Cancelled,
    }

    /// <summary>One warming run: an immutable file list plus its progress. Progress fields are read/written under <see cref="_gate"/>.</summary>
    private sealed class WarmRun
    {
        public readonly string Key;
        public readonly string[] Files;
        public volatile bool Cancelled;

        public bool Verbose;                        // DebugPreloadLogger captured at start (no config reads on the worker)
        public WarmState State = WarmState.Queued;
        public string EndReason = "";
        public int Done;
        public int Skipped;
        public long BytesDone;
        public string CurrentFile = "";
        public double ElapsedSec;

        public WarmRun(string key, string[] files)
        {
            Key = key;
            Files = files;
        }
    }
}
