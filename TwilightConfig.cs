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
        EnableScenePreload = config.Bind("Features", "EnableScenePreload", true,
            "Held-scene preload of the announced MULTI pick's first level during PREP (additive, dormant); " +
            "round_start swaps it in instead of loading. Requires server-side pick_announced; SINGLE picks never preload.");
        EnableChainedPreload = config.Bind("Features", "EnableChainedPreload", true,
            "M3 chained preload: while playing a collection level, preload the NEXT level additively (dormant, " +
            "low priority) and swap it in at level advance (frame-level transitions). Works for local lc runs too; " +
            "adjacent same levels never preload (Empty-dwell path). Disable if in-round loading hurts framerate.");

        ChatPopupEnabled = config.Bind("Chat", "PopupEnabled", true, "Briefly show the chat log (no input box) when a message arrives, then fade out.");
        ChatPopupSecs = config.Bind("Chat", "PopupSecs", 5f, "How long the passive chat popup stays visible before fading (seconds).");
        ChatToggleHotkey = config.Bind("Chat", "ToggleHotkey", "Ctrl+T",
            "Hotkey to open/close the chat console. Modifiers joined by '+', main key last, e.g. \"Ctrl+T\", \"Ctrl+Shift+Y\", \"Alt+F8\", \"F8\". " +
            "\"Ctrl\" also matches the Cmd (⌘) key on macOS. Restart not required — also re-read by `twi reload`.");

        HudEnabled = config.Bind("HUD", "Enabled", true, "Show the collection info HUD (top-right, two lines) while a collection run is active.");
        HudTextColor = config.Bind("HUD", "TextColor", "FFD94C", "HUD text colour as hex (RRGGBB or RRGGBBAA, optional leading #). Single colour, no gradient.");
        HudFontSize = config.Bind("HUD", "FontSize", 18, "HUD font size.");

        VerboseNetLog = config.Bind("Debug", "VerboseNetLog", false, "Log every sent/received WebSocket frame.");

        // Persist immediately so the cfg file appears on disk on first launch
        // (BepInEx otherwise only writes it on shutdown) — lets the user edit it
        // without having to quit first.
        try { config.Save(); } catch (System.Exception ex) { Plugin.Logger.LogWarning("[TwilightConfig] failed to save cfg: " + ex.Message); }
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
