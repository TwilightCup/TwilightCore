using BepInEx.Configuration;

namespace TwilightCore;

/// <summary>
/// Centralised access to the plugin's BepInEx config bindings.
/// The config file lives at <c>BepInEx/config/TwilightCore.cfg</c> and is
/// auto-created on first launch with these defaults.
/// </summary>
internal static class TwilightConfig
{
    // ── Server ─────────────────────────────────────────────────────
    // NOTE: host/port are NOT in the config — they're passed on the command line
    // (`twi connect <host> [port]`, default port 5173). Only the TLS toggle lives here.
    public static ConfigEntry<bool> UseTLS;

    // ── Account ────────────────────────────────────────────────────
    /// <summary>
    /// Player account username. The plugin logs in via POST /auth/login to obtain
    /// a JWT, then opens the WebSocket as that account's seat.
    /// </summary>
    public static ConfigEntry<string> Username;
    /// <summary>
    /// Player account password (stored in plaintext in the cfg file — acceptable
    /// for an organiser-controlled tournament machine). Used only to obtain a JWT.
    /// </summary>
    public static ConfigEntry<string> Password;
    /// <summary>
    /// Optional explicit seat (PLAYER_A / PLAYER_B). Leave blank to let the server
    /// auto-resolve from the account's assigned seat in its single running session.
    /// </summary>
    public static ConfigEntry<string> Seat;

    // ── Net ────────────────────────────────────────────────────────
    public static ConfigEntry<int> HeartbeatSecs;
    public static ConfigEntry<int> ReconnectMinBackoffSecs;
    public static ConfigEntry<int> ReconnectMaxBackoffSecs;

    // ── Features ───────────────────────────────────────────────────
    /// <summary>Lock manual level starts during PREP/COUNTDOWN (false-start prevention).</summary>
    public static ConfigEntry<bool> EnableReadyLock;
    /// <summary>Use the stand-in simulated timer that reports completion/forfeit (replaced by the real timer later).</summary>
    public static ConfigEntry<bool> EnableSimTimer;
    /// <summary>
    /// Minimum dwell in the 'Empty' scene when a collection run advances to the SAME
    /// level as the one just played (seconds). 0 disables the detour (direct reload).
    /// </summary>
    public static ConfigEntry<float> SameLevelReloadMinDwell;
    /// <summary>Clamp fall speed in the main menu while connected to the match server (anti-fall).</summary>
    public static ConfigEntry<bool> EnableMenuFallLimit;
    /// <summary>
    /// MULTI-round subsegment tracking: sample position/movement once per second
    /// from the first wake-up in each level to the pass zone, and detect crossings
    /// of the opponent's relayed sample planes, for a live time gap.
    /// </summary>
    public static ConfigEntry<bool> EnableSubsegment;

    // ── Subsegment ────────────────────────────────────────────────
    /// <summary>Half-extent (radius, metres) of the virtual crossing-detection plane around each sample point.</summary>
    public static ConfigEntry<float> SubsegmentPlaneRadius;
    /// <summary>Minimum displacement between samples (metres) for the sample to define a detection plane; slower samples are stored with a zero vector.</summary>
    public static ConfigEntry<float> SubsegmentMinMove;
    /// <summary>Sampling interval (seconds).</summary>
    public static ConfigEntry<float> SubsegmentSampleInterval;

    /// <summary>
    /// Held-scene preload: after !ready, additively load the announced MULTI pick's
    /// first level during PREP and swap it in at round_start (near-instant round start).
    /// SINGLE picks are unaffected and report preload "na". Any preload failure falls
    /// back to the standard load path.
    /// </summary>
    public static ConfigEntry<bool> EnableScenePreload;
    /// <summary>
    /// M3 chained preload: while a collection run is playing a level, additively
    /// preload the NEXT level in the background (dormant, low priority) and swap
    /// it in at level advance — frame-level transitions instead of multi-second
    /// loads. Adjacent same levels never preload (existing Empty-dwell path).
    /// Disable if the in-round loading hurts framerate on target hardware.
    /// </summary>
    public static ConfigEntry<bool> EnableChainedPreload;
    /// <summary>
    /// Tinting fix for held-scene preloads: overwrite the active light-probe
    /// set's coefficients with a uniform field (sampled at the player before the
    /// load) for the hold window, and write the held scene's real coefficients
    /// back at the swap. Without it, dynamic objects are tinted by the next
    /// level's probes (ambient + baked lights) from preload completion until the
    /// swap. Kill-switch in case the coefficient write misbehaves on some
    /// Unity build (the hold then just keeps the cosmetic tinting).
    /// </summary>
    public static ConfigEntry<bool> EnableProbeFreeze;
    /// <summary>
    /// Experimental: extend the probe-coefficient freeze to UNBAKED held scenes
    /// (every level except Halloween/Steam — their probe sets carry no readable
    /// coefficients, so during the hold window dynamic objects sample zeros:
    /// the "everything loses ambient and goes dark" artifact, including the
    /// player model). The uniform's value source adapts: a sample from a BAKED
    /// playing structure when available (verified correct), else the playing
    /// scene's flat ambient light with an empirically calibrated scale. The
    /// swap writes nothing back — scene activation re-applies the held scene's
    /// own values. Off = hold windows of unbaked levels stay dark.
    /// </summary>
    public static ConfigEntry<bool> ProbeFreezeUnbakedHolds;
    /// <summary>
    /// After each swap-in, run Resources.UnloadUnusedAssets() (and wait it out).
    /// The additive preload path leaks native memory per DISTINCT scene until
    /// the process dies at a later scene integration (OOM); the sweep collects
    /// the residue. Adds a short hitch per swap — toggle off to A/B.
    /// </summary>
    public static ConfigEntry<bool> PreloadUnloadUnusedAfterSwap;
    /// <summary>
    /// Conservative preload — disk cache warmup: during PREP, after the
    /// first-level hold completes, a single background thread pre-reads the
    /// collection's remaining level files into the OS page cache so the
    /// in-round chained additive loads read from memory instead of disk.
    /// Pure file IO (no Unity objects, one fixed read buffer, no process-
    /// memory growth); any failure silently degrades. Hot-reloadable —
    /// turning it off stops NEW warmups (an active one just finishes).
    /// </summary>
    public static ConfigEntry<bool> EnableDiskCacheWarmup;
    /// <summary>
    /// Warm the shared asset files alongside the level files — the trios
    /// adjacent to the warmed scenes' build indices (sharedassets{N}.* ↔
    /// level{N}; ~600MB for a full collection instead of ~4.1GB for every
    /// shared file) plus resources.assets. Scene loads fault these in on
    /// demand, so this closes the remaining I/O. The per-file debug log shows
    /// the total; toggle off if the machine's RAM headroom is tight (the page
    /// cache is kernel-reclaimable either way).
    /// </summary>
    public static ConfigEntry<bool> DiskWarmupWarmSharedFiles;
    /// <summary>
    /// Disk-cache warmup strategy. "follow-chain" (default): PREP head-warms
    /// only the levels right after the first, then each completed chained
    /// hold warms the level after next — pages get used within ~one level of
    /// warming (low eviction risk, small steady page-cache footprint), and
    /// local `lc` runs benefit too; the cost is mild background reads during
    /// the round, landing in the disk-idle window between a completed hold
    /// and the next hold starting. "prep-all": warm the whole collection
    /// during PREP (zero in-round IO).
    /// </summary>
    public static ConfigEntry<string> DiskWarmupMode;
    // ── HUD ───────────────────────────────────────────────────────
    /// <summary>Show the two-line collection info HUD (top-right) during collection runs.</summary>
    public static ConfigEntry<bool> HudEnabled;
    /// <summary>HUD text colour as hex (RRGGBB or RRGGBBAA, optional #). Single colour, no gradient.</summary>
    public static ConfigEntry<string> HudTextColor;
    /// <summary>HUD font size (same default as TwilightTimer's timer rows).</summary>
    public static ConfigEntry<int> HudFontSize;

    // ── Chat ───────────────────────────────────────────────────────
    /// <summary>Briefly show the chat log (no input box) when a message arrives, then fade out.</summary>
    public static ConfigEntry<bool> ChatPopupEnabled;
    /// <summary>How long the passive chat popup stays visible before fading (seconds).</summary>
    public static ConfigEntry<float> ChatPopupSecs;
    /// <summary>Hotkey that opens/closes the chat console (e.g. "Ctrl+T", "Ctrl+Shift+Y", "F8").</summary>
    public static ConfigEntry<string> ChatToggleHotkey;

    // ── Debug ──────────────────────────────────────────────────────
    public static ConfigEntry<bool> VerboseNetLog;
    /// <summary>
    /// Verbose preload diagnostics: the per-hold/per-swap state dumps from the
    /// lighting/OOM investigations — load-start table signatures, the
    /// lighting-freeze detail (table IDs, mode, probes), probe-freeze
    /// sampling/verification, the hold-sampling comparison, post-swap table
    /// and memory signatures, sweep timings, and the engine-applied-rs suffix
    /// on the activate timing line. Pure logging — behavior is identical
    /// either way; warnings/errors always log, and `twi preload rs` (an
    /// explicit command) always dumps.
    /// </summary>
    public static ConfigEntry<bool> DebugPreloadLogger;
    /// <summary>
    /// Verbose subsegment diagnostics: tracker state transitions, wake-up
    /// detection, samples sent and opponent samples/planes received, crossing
    /// checks/hits, completion sync decisions, and reasons a round is idle.
    /// Pure logging — behavior is identical either way; warning/error and the
    /// always-on subsegment lines still log.
    /// </summary>
    public static ConfigEntry<bool> DebugSubsegmentLogger;

    /// <summary>Whether DebugPreloadLogger is on (null-safe before Init).</summary>
    internal static bool PreloadDebugLogging => DebugPreloadLogger != null && DebugPreloadLogger.Value;

    /// <summary>Whether DebugSubsegmentLogger is on (null-safe before Init).</summary>
    internal static bool SubsegmentDebugLogging => DebugSubsegmentLogger != null && DebugSubsegmentLogger.Value;

    /// <summary>The BepInEx config file, kept so <c>twi reload</c> can hot-reload it.</summary>
    private static ConfigFile _config;

    public static void Init(ConfigFile config)
    {
        _config = config;
        UseTLS = config.Bind("Server", "UseTLS", true, "Connect via wss/https (the public nginx endpoint exposes the server over TLS). Set false for a plain local dev server.");

        Username = config.Bind("Account", "Username", "", "Player account username (created by the organiser).");
        Password = config.Bind("Account", "Password", "", "Player account password.");
        Seat = config.Bind("Account", "Seat", "", "Explicit seat (PLAYER_A/PLAYER_B); blank = auto-resolve.");

        HeartbeatSecs = config.Bind("Net", "HeartbeatSecs", 20, "WebSocket heartbeat interval (seconds).");
        ReconnectMinBackoffSecs = config.Bind("Net", "ReconnectMinBackoffSecs", 1, "Initial reconnect backoff (seconds).");
        ReconnectMaxBackoffSecs = config.Bind("Net", "ReconnectMaxBackoffSecs", 30, "Maximum reconnect backoff (seconds).");

        EnableReadyLock = config.Bind("Features", "EnableReadyLock", true, "Block manual level starts during PREP/COUNTDOWN.");
        EnableSimTimer = config.Bind("Features", "EnableSimTimer", true, "Send simulated completion/forfeit reports so the match flow can be tested without the real timer.");
        SameLevelReloadMinDwell = config.Bind("Features", "SameLevelReloadMinDwell", 1f,
            "When a collection run advances to the SAME level as the one just played, " +
            "detour through the 'Empty' scene for at least this many seconds before reloading it " +
            "(clear visual gap between consecutive plays of one level). 0 = disable the detour.");
        EnableMenuFallLimit = config.Bind("Features", "EnableMenuFallLimit", true,
            "While connected to the match server, clamp the main-menu ragdoll's fall speed (anti-fall).");
        EnableSubsegment = config.Bind("Features", "EnableSubsegment", true,
            "MULTI rounds: sample position/movement 1x/sec from each level's first wake-up to the pass zone, " +
            "and detect crossings of the opponent's sample planes, for a live time gap (server relays + broadcasts).");

        SubsegmentPlaneRadius = config.Bind("Subsegment", "PlaneRadius", 50f,
            "Half-extent (metres) of each virtual crossing-detection plane (perpendicular to the sampled movement vector).");
        SubsegmentMinMove = config.Bind("Subsegment", "MinMove", 0.5f,
            "Minimum displacement between samples (metres) for the sample to define a detection plane; slower samples are stored with a zero vector.");
        SubsegmentSampleInterval = config.Bind("Subsegment", "SampleInterval", 1f,
            "Subsegment sampling interval (seconds).");

        EnableScenePreload = config.Bind("Features", "EnableScenePreload", true,
            "Held-scene preload of the announced MULTI pick's first level during PREP (additive, dormant); " +
            "round_start swaps it in instead of loading. Requires server-side pick_announced; SINGLE picks never preload.");
        EnableChainedPreload = config.Bind("Features", "EnableChainedPreload", true,
            "M3 chained preload: while playing a collection level, preload the NEXT level additively (dormant, " +
            "low priority) and swap it in at level advance (frame-level transitions). Works for local lc runs too; " +
            "adjacent same levels never preload (Empty-dwell path). Disable if in-round loading hurts framerate.");
        EnableProbeFreeze = config.Bind("Features", "EnableProbeFreeze", true,
            "Tinting fix: freeze the active light-probe coefficients to a uniform field (sampled at the player " +
            "before the preload load) for the hold window; the swap writes the held scene's real coefficients back. " +
            "Off = dynamic objects are tinted by the next level's probes during the hold (cosmetic, resolves at swap).");
        ProbeFreezeUnbakedHolds = config.Bind("Features", "ProbeFreezeUnbakedHolds", true,
            "Experimental: also freeze UNBAKED held scenes (all levels except Halloween/Steam). Without it their " +
            "hold windows leave dynamic objects sampling zero coefficients — everything with light probes goes " +
            "dark, player model included. The uniform value comes from the playing scene's structure (pre-load " +
            "sample); the swap writes nothing back (scene activation re-applies the held scene's own values). " +
            "Slight brightness offset possible; off = hold windows of unbaked levels stay dark.");
        PreloadUnloadUnusedAfterSwap = config.Bind("Features", "PreloadUnloadUnusedAfterSwap", true,
            "After each swap-in, run Resources.UnloadUnusedAssets() and wait it out. The additive preload path " +
            "leaks memory per distinct scene until OOM; the sweep collects the residue. " +
            "Adds a short hitch per swap — toggle off to A/B.");
        EnableDiskCacheWarmup = config.Bind("Features", "EnableDiskCacheWarmup", true,
            "Conservative preload: during PREP (after the first-level hold completes), a background thread pre-reads " +
            "the collection's remaining level files into the OS page cache, so in-round chained loads read from memory " +
            "instead of disk. Pure file IO — no scene/lighting interaction, no process-memory growth; any failure " +
            "silently degrades. Hot via `twi reload`: off stops new warmups, an active run finishes.");
        DiskWarmupWarmSharedFiles = config.Bind("Features", "DiskWarmupWarmSharedFiles", true,
            "Warm the warmed scenes' adjacent sharedassets{N}.* trios + resources.assets alongside the level files " +
            "(scene loads fault them in on demand; ~600MB for a full collection vs ~4.1GB for all shared files). " +
            "Per-file sizes visible in the debug preload log; toggle off on machines with tight RAM headroom.");
        DiskWarmupMode = config.Bind("Features", "DiskWarmupMode", "follow-chain",
            "Disk-cache warmup strategy: 'follow-chain' = PREP head-warms the levels right after the first, then each " +
            "completed chained hold warms the level after next (fresher pages, smaller steady footprint, also covers local " +
            "lc runs; mild in-round background reads in the disk-idle window); 'prep-all' = warm the whole collection " +
            "during PREP (zero in-round IO). Hot via `twi reload`.");

        ChatPopupEnabled = config.Bind("Chat", "PopupEnabled", true, "Briefly show the chat log (no input box) when a message arrives, then fade out.");
        ChatPopupSecs = config.Bind("Chat", "PopupSecs", 5f, "How long the passive chat popup stays visible before fading (seconds).");
        ChatToggleHotkey = config.Bind("Chat", "ToggleHotkey", "Ctrl+T",
            "Hotkey to open/close the chat console. Modifiers joined by '+', main key last, e.g. \"Ctrl+T\", \"Ctrl+Shift+Y\", \"Alt+F8\", \"F8\". " +
            "\"Ctrl\" also matches the Cmd (⌘) key on macOS. Restart not required — also re-read by `twi reload`.");

        HudEnabled = config.Bind("HUD", "Enabled", true, "Show the collection info HUD (top-right, two lines) while a collection run is active.");
        HudTextColor = config.Bind("HUD", "TextColor", "FFD94C", "HUD text colour as hex (RRGGBB or RRGGBBAA, optional leading #). Single colour, no gradient.");
        HudFontSize = config.Bind("HUD", "FontSize", 18, "HUD font size.");

        VerboseNetLog = config.Bind("Debug", "VerboseNetLog", false, "Log every sent/received WebSocket frame.");
        DebugPreloadLogger = config.Bind("Debug", "DebugPreloadLogger", false,
            "Verbose preload diagnostic logging (per-hold/per-swap lighting, probe, lightmap-table and memory state " +
            "dumps, sweep timings). Pure logging — behavior is identical either way; warnings/errors always log, " +
            "and `twi preload rs` always dumps. Hot-reloadable via `twi reload`.");
        DebugSubsegmentLogger = config.Bind("Debug", "DebugSubsegmentLogger", false,
            "Verbose subsegment diagnostic logging (tracker state, wake-up detection, samples sent/received, " +
            "plane creation and crossing checks, hits, completion sync decisions, and idle reasons). Pure logging — " +
            "behavior is identical either way; warnings/errors and the normal subsegment status lines always log. " +
            "Hot-reloadable via `twi reload`.");
    }

    /// <summary>
    /// Hot-reload the BepInEx config from disk (<c>BepInEx/config/TwilightCore.cfg</c>).
    /// Re-reads every entry's value into the existing <see cref="ConfigEntry{T}"/> bindings,
    /// so anything holding a binding sees the new value immediately. Returns a short status
    /// string for console echo. Entries newly added to the file since last save are bound too.
    /// </summary>
    public static string Reload()
    {
        if (_config == null) return "config not initialised";
        try
        {
            _config.Reload();
            Plugin.Logger.LogInfo("[TwilightConfig] reloaded TwilightCore.cfg.");
            return "TwilightCore.cfg reloaded.";
        }
        catch (System.Exception ex)
        {
            Plugin.Logger.LogError("[TwilightConfig] reload failed: " + ex.Message);
            return "reload failed: " + ex.Message;
        }
    }
}
