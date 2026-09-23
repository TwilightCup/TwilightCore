using System;

namespace TwilightCore.Net;

/// <summary>
/// Shared UTC wall-clock helpers for outbound timestamps.
///
/// The backend requires a Unix-UTC-millisecond stamp on every node event
/// (level_time_upload / attempt_skip / project_complete / forfeit_signal):
/// pydantic <c>extra="forbid"</c> + a required <c>utc_ms</c> field means a
/// missing stamp is rejected with 400 "Malformed message". The same value is
/// used by the periodic <c>utc_timestamp</c> heartbeat.
/// </summary>
internal static class UtcClock
{
    private static readonly DateTime UnixEpoch =
        new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Current time as Unix UTC milliseconds.</summary>
    public static long NowMs() => (long)(DateTime.UtcNow - UnixEpoch).TotalMilliseconds;
}
