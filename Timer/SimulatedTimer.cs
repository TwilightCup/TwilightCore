using System.Collections.Generic;
using TwilightCore.Net;
using UnityEngine;

namespace TwilightCore.Timer;

/// <summary>
/// Stand-in round reporter that drives the server's match flow WITHOUT a real
/// timer. It subscribes to <see cref="CollectionManager"/>'s lifecycle events and
/// sends the progression signals the server needs:
///
///   level_time_upload / attempt_skip (per level/attempt),
///   project_complete (MULTI run finished / SINGLE explicitly ended or last
///   attempt used up — 单关通过本身不再自动完成，twilightcore-single-attempt-complete.md),
///   forfeit_signal (run aborted).
///
/// Times come from a crude real-time stopwatch (level-load → completion) — NOT
/// rules-accurate (no checkpoint/pause handling), but enough for scoring to
/// produce meaningful numbers. Manual <c>twi sim</c> overrides let testers drive
/// the flow without playing. The real timer module will implement the same
/// <see cref="IRoundReporter"/> seam and replace this class.
/// </summary>
internal sealed class SimulatedTimer : IRoundReporter
{
    private readonly TwilightClient _client;
    private bool _wired;

    private string _roundId;
    private PickSnapshot _pick;
    private bool _active;
    private bool _isSingle;
    private int _attemptTotal;

    private float _levelStartRealtime;
    private long _accumulatedMs;     // MULTI running total
    private int _validAttempts;      // SINGLE attempts with a recorded time
    private int _attemptDone;        // SINGLE attempts finished (passed OR skipped)

    // Manual overrides set by `twi sim` (consumed on the next event).
    private long? _simMsOverride;
    private long? _simFinalMsOverride;
    private string _simForfeitReasonOverride;

    public SimulatedTimer(TwilightClient client)
    {
        _client = client;
    }

    public bool IsActive => _active;

    // ── IRoundReporter ─────────────────────────────────────────────

    public void StartRound(string roundId, PickSnapshot pick)
    {
        EnsureWired();
        _roundId = roundId;
        _pick = pick;
        _active = true;
        _isSingle = pick != null && pick.Type == PickType.Single;
        _attemptTotal = pick != null ? pick.RetryCount : 0;
        _accumulatedMs = 0;
        _validAttempts = 0;
        _attemptDone = 0;
        _levelStartRealtime = Time.realtimeSinceStartup;
        Plugin.Logger.LogInfo(
            $"[SimTimer] round {roundId} started ({(_isSingle ? "SINGLE" : "MULTI")}" +
            (_isSingle && _attemptTotal > 0 ? $", {_attemptTotal} attempts" : "") + ").");
    }

    public void Stop()
    {
        if (!_active) return;
        _active = false;
        Plugin.Logger.LogInfo("[SimTimer] stopped.");
    }

    public void FinishRound()
    {
        if (!_active || string.IsNullOrEmpty(_roundId)) return;
        // 主动结束：MULTI 等价整局完成（带累计时长）；SINGLE 按现有尝试成绩计分。
        if (_isSingle)
        {
            SendProjectComplete();
        }
        else
        {
            Send(new Dictionary<string, object>
            {
                { "type", Msg.ProjectComplete },
                { "round_id", _roundId },
                { "final_total_ms", _accumulatedMs },
            });
            Plugin.Logger.LogInfo($"[SimTimer] project_complete (manual, final={_accumulatedMs}ms).");
        }
        Stop();
    }

    // ── Manual sim overrides (called by twi sim commands) ───────────

    internal void PrepareSimLevelMs(long? ms) => _simMsOverride = ms;
    internal void PrepareSimFinalMs(long? ms) => _simFinalMsOverride = ms;
    internal void PrepareSimForfeitReason(string reason) => _simForfeitReasonOverride = reason;

    internal string StatusString()
    {
        if (string.IsNullOrEmpty(_roundId))
            return "[SimTimer] no active round.";
        string type = _isSingle ? "SINGLE" : "MULTI";
        return _isSingle
            ? $"[SimTimer] round={_roundId} {type} attempts={_pick?.RetryCount} valid={_validAttempts}"
            : $"[SimTimer] round={_roundId} {type} total={_accumulatedMs}ms";
    }

    // ── CollectionManager event handlers ───────────────────────────

    private void OnLevelStarted(string levelId, int idx)
    {
        // A fresh level/attempt is starting — reset the stopwatch.
        _levelStartRealtime = Time.realtimeSinceStartup;
    }

    private void OnLevelCompleted(int idx, bool skipped)
    {
        if (!_active || string.IsNullOrEmpty(_roundId)) return;

        long ms;
        if (_simMsOverride.HasValue) { ms = _simMsOverride.Value; _simMsOverride = null; }
        else ms = StopwatchMs();

        if (_isSingle)
        {
            _attemptDone++;

            if (skipped)
            {
                Send(new Dictionary<string, object>
                {
                    { "type", Msg.AttemptSkip },
                    { "round_id", _roundId },
                    { "attempt_index", idx },
                });
                Plugin.Logger.LogInfo($"[SimTimer] attempt_skip idx={idx}.");
            }
            else
            {
                _validAttempts++;
                Send(new Dictionary<string, object>
                {
                    { "type", Msg.LevelTimeUpload },
                    { "round_id", _roundId },
                    { "level_index", idx }, // acts as attempt index for SINGLE
                    { "this_level_ms", ms },
                });
                Plugin.Logger.LogInfo($"[SimTimer] attempt {idx} time={ms}ms.");
            }

            // 方案 A（需求 §4）：最后一次尝试结束（无论通过/跳过）后已无剩余尝试，
            // 语义等价于主动结束 → 自动 project_complete。非最后一次通过只上报成绩。
            if (idx + 1 >= _attemptTotal)
            {
                SendProjectComplete();
                Stop();
            }
        }
        else // MULTI
        {
            _accumulatedMs += ms;
            Send(new Dictionary<string, object>
            {
                { "type", Msg.LevelTimeUpload },
                { "round_id", _roundId },
                { "level_index", idx },
                { "this_level_ms", ms },
                { "total_ms", _accumulatedMs },
            });
            Plugin.Logger.LogInfo($"[SimTimer] level {idx} time={ms}ms total={_accumulatedMs}ms.");
        }
    }

    private void OnRunCompleted()
    {
        if (!_active || string.IsNullOrEmpty(_roundId)) return;

        long? final = _simFinalMsOverride;
        _simFinalMsOverride = null;

        if (_isSingle)
        {
            // SINGLE 通过 ≠ 结束（FASTEST 可刷成绩、AVERAGE 需用满尝试）。运行到
            // 最后一次尝试时 OnLevelCompleted 已按方案 A 自动上报；这里只剩
            // 「本地合集被外部提前结束」（twi sim complete / 主动结束回合命令）
            // 的路径 —— 即选手明确操作，照发。
            SendProjectComplete();
        }
        else
        {
            // MULTI: 一次通关即整局结束，final_total_ms 随 project_complete 上报（需求 §3 注）。
            Send(new Dictionary<string, object>
            {
                { "type", Msg.ProjectComplete },
                { "round_id", _roundId },
                { "final_total_ms", final ?? _accumulatedMs },
            });
            Plugin.Logger.LogInfo($"[SimTimer] project_complete (final={((final ?? _accumulatedMs)).ToString()}ms).");
        }
        Stop();
    }

    private void OnRunAborted()
    {
        if (!_active || string.IsNullOrEmpty(_roundId)) return;

        // 期望行为 §3：最后一次尝试「中途退出/放弃」= attempt_skip + 自动
        // project_complete（该次已无通关可能，等价跳过且无剩余尝试）。中途退出
        // 不触发 LevelCompleted，_attemptDone 只计已结束的尝试——正在进行的
        // 尝试序号即 _attemptDone（0 基），它是最后一次 ⇔ _attemptDone + 1 >=
        // 上限。lc skip 路径在 OnLevelCompleted 已覆盖；主动结束（!finish）先
        // Stop() 再 AbortCollectionRun，走不到这里。0 次有效成绩仍按 §3 行 4
        // 走 forfeit_signal，不发本分支。
        if (_isSingle && _validAttempts > 0 && _attemptDone + 1 >= _attemptTotal)
        {
            Send(new Dictionary<string, object>
            {
                { "type", Msg.AttemptSkip },
                { "round_id", _roundId },
                { "attempt_index", _attemptDone },
            });
            Plugin.Logger.LogInfo($"[SimTimer] last attempt abandoned → attempt_skip idx={_attemptDone}.");
            SendProjectComplete();
            Stop();
            return;
        }

        if (_simForfeitReasonOverride != null)
        {
            string reason = _simForfeitReasonOverride;
            _simForfeitReasonOverride = null;
            SendForfeit(reason);
            Stop();
            return;
        }

        // Computed reason per the rules.
        if (_isSingle && _validAttempts > 0)
        {
            // ≥1 valid attempt + exit → normal end (server marks rest skipped).
            var payload = new Dictionary<string, object>
            {
                { "type", Msg.ProjectComplete },
                { "round_id", _roundId },
            };
            Send(payload);
            Plugin.Logger.LogInfo("[SimTimer] abort with valid attempts → project_complete.");
        }
        else
        {
            SendForfeit(_isSingle ? ForfeitReason.SingleExit0Valid : ForfeitReason.MultiExit);
        }
        Stop();
    }

    private void SendForfeit(string reason)
    {
        Send(new Dictionary<string, object>
        {
            { "type", Msg.ForfeitSignal },
            { "round_id", _roundId },
            { "reason", reason },
        });
        Plugin.Logger.LogInfo($"[SimTimer] forfeit_signal reason={reason}.");
    }

    /// <summary>SINGLE 结束（无 final_total_ms——服务端按各次尝试成绩计分）。</summary>
    private void SendProjectComplete()
    {
        Send(new Dictionary<string, object>
        {
            { "type", Msg.ProjectComplete },
            { "round_id", _roundId },
        });
        Plugin.Logger.LogInfo("[SimTimer] project_complete (single).");
    }

    // ── Helpers ────────────────────────────────────────────────────

    private long StopwatchMs()
    {
        long ms = (long)((Time.realtimeSinceStartup - _levelStartRealtime) * 1000f);
        return ms < 0 ? 0 : ms;
    }

    private void Send(Dictionary<string, object> msg) => _client.Send(msg);

    private void EnsureWired()
    {
        if (_wired) return;
        var mgr = CollectionManager.Instance;
        if (mgr == null) return;
        mgr.LevelStarted += OnLevelStarted;
        mgr.LevelCompleted += OnLevelCompleted;
        mgr.RunCompleted += OnRunCompleted;
        mgr.RunAborted += OnRunAborted;
        _wired = true;
    }
}
