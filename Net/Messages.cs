namespace TwilightCore.Net;

/// <summary>
/// WebSocket message <c>type</c> discriminator literals and shared enums,
/// mirroring <c>TwilightCupBackend/src/twilightcupbackend/protocol.py</c>.
/// </summary>
internal static class Msg
{
    // ── Client → Server (only the ones this plugin sends) ───────────
    public const string Chat = "chat";
    public const string Heartbeat = "heartbeat";
    public const string ReconnectResync = "reconnect_resync";
    // Timer/reporter messages (sent by the simulated timer):
    public const string LevelTimeUpload = "level_time_upload";
    public const string AttemptSkip = "attempt_skip";
    public const string ProjectComplete = "project_complete";
    public const string ForfeitSignal = "forfeit_signal";

    // ── Server → Client ─────────────────────────────────────────────
    public const string AuthOk = "auth_ok";
    public const string AuthError = "auth_error";
    public const string System = "system";
    public const string ReadyState = "ready_state";
    public const string PhaseChange = "phase_change";
    public const string CountdownTick = "countdown_tick";
    public const string CountdownAbort = "countdown_abort";
    public const string RoundStart = "round_start";
    public const string RoundStartedBroadcast = "round_started_broadcast";
    public const string PlayerStatus = "player_status";
    public const string LevelTimeUpdate = "level_time_update";
    public const string RoundResult = "round_result";
    public const string CumulativeScore = "cumulative_score";
    public const string MatchEnd = "match_end";
    public const string CounterState = "counter_state";
    public const string CounterAlert = "counter_alert";
    public const string VerdictEdit = "verdict_edit";
    public const string DraftState = "draft_state";
    public const string Error = "error";
}

/// <summary>Match lifecycle phase (datatypes.MatchPhase).</summary>
internal enum MatchPhase
{
    Idle = 0,
    Prep = 1,
    Countdown = 2,
    InRound = 3,
    RoundJudging = 4,
    RoundEnd = 5,
    MatchEnd = 6,
}

/// <summary>Pick project type (datatypes.PickType).</summary>
internal enum PickType
{
    Multi = 1,
    Single = 2,
}

/// <summary>Forfeit reason literals (protocol.ClientForfeitSignal.reason).</summary>
internal static class ForfeitReason
{
    public const string MultiExit = "multi_exit";
    public const string SingleExit0Valid = "single_exit_0_valid";
}
