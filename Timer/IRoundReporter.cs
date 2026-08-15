using TwilightCore.Net;

namespace TwilightCore.Timer;

/// <summary>
/// Reports per-round progress to the server (level times, completion, forfeit).
/// Implemented today by the <b>simulated</b> timer (placeholder times, drivable
/// via <c>twi sim</c>); the real timer module will implement the same interface
/// later and swap in without touching call sites.
/// </summary>
internal interface IRoundReporter
{
    bool IsActive { get; }

    /// <summary>Begin tracking a round. pick carries type (MULTI/SINGLE) + retry_count.</summary>
    void StartRound(string roundId, PickSnapshot pick);

    /// <summary>Stop tracking (round ended / left IN_ROUND / disconnected).</summary>
    void Stop();

    /// <summary>
    /// 选手主动「结束本回合」（SINGLE 语义：不再进行剩余尝试，按现有成绩计分）。
    /// 上报 project_complete 并停止跟踪。必须在回合进行中（IsActive）调用才有意义；
    /// 实现方自行忽略非活跃状态（twilightcore-single-attempt-complete.md §3）。
    /// </summary>
    void FinishRound();
}

/// <summary>No-op reporter (used when the simulated timer is disabled).</summary>
internal sealed class NullRoundReporter : IRoundReporter
{
    public static readonly NullRoundReporter Instance = new NullRoundReporter();
    public bool IsActive => false;
    public void StartRound(string roundId, PickSnapshot pick) { }
    public void Stop() { }
    public void FinishRound() { }
}
