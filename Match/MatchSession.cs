using TwilightCore.Net;

namespace TwilightCore.Match;

/// <summary>
/// Runtime view of the player's match state. A single shared instance (set by
/// the plugin on startup) read by the match controller, the ready-lock patches
/// and the chat/timer modules. All fields are touched on the main thread.
/// </summary>
internal sealed class MatchSession
{
    public static MatchSession Instance { get; set; }

    // ── Identity / connection ──────────────────────────────────────
    public bool IsAuthenticated;
    public string AccountId;
    public string DisplayName;
    public string Seat;        // PLAYER_A / PLAYER_B
    public string SessionId;
    public string SessionName;

    // ── Match state ────────────────────────────────────────────────
    public MatchPhase Phase = MatchPhase.Idle;
    public bool AReady;
    public bool BReady;
    public string RoundId;     // current / last round id
    public PickSnapshot Pick;  // current / last pick

    public bool IsPlayerSeat => Seat == "PLAYER_A" || Seat == "PLAYER_B";

    /// <summary>My own ready flag (the one for my seat).</summary>
    public bool MyReady =>
        Seat == "PLAYER_A" ? AReady :
        Seat == "PLAYER_B" ? BReady : false;

    /// <summary>
    /// True while manual level starts must be blocked (false-start prevention).
    /// In PREP the player may still practise freely until they <c>!ready</c>; once
    /// ready (or during COUNTDOWN) starts are locked. The plugin's own server-driven
    /// launch happens during IN_ROUND, so it is never blocked.
    /// </summary>
    public bool ReadyLockActive =>
        IsAuthenticated && ((Phase == MatchPhase.Prep && MyReady) || Phase == MatchPhase.Countdown);
}
