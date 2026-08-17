using System.Collections.Generic;
using TwilightCore.Net;

namespace TwilightCore.Timer
{
    /// <summary>
    /// <see cref="IRoundReporter"/> implementation that delegates to a registered
    /// <see cref="ITimerProvider"/> (the real timer plugin). Translates the
    /// provider's events into the server progression signals — the same set the
    /// simulated timer sends:
    ///
    ///   SegmentCompleted → level_time_upload (MULTI total+level / SINGLE attempt)
    ///   AttemptSkipped   → attempt_skip
    ///   RunCompleted     → project_complete (SINGLE 仅最后一次尝试结束后——方案 A，
    ///                      通过本身不再自动完成，twilightcore-single-attempt-complete.md)
    ///   IncompleteExit   → forfeit_signal OR project_complete (SINGLE ≥1 valid)
    ///
    /// The provider (not this class) owns timing, tag validation and the
    /// "incomplete exit vs skip" distinction; this class only decides forfeit vs
    /// normal end per the rules (需求文档 §10.2) and formats the payloads.
    /// </summary>
    internal sealed class ProviderRoundReporter : IRoundReporter
    {
        private readonly TwilightClient _client;
        private readonly ITimerProvider _provider;

        /// <summary>The provider this reporter was built against (the controller
        /// compares it against the registry to detect re-registration).</summary>
        public ITimerProvider Provider => _provider;

        private string _roundId;
        private PickSnapshot _pick;
        private bool _active;
        private bool _isSingle;

        public ProviderRoundReporter(TwilightClient client, ITimerProvider provider)
        {
            _client = client;
            _provider = provider;
            _provider.SegmentCompleted += OnSegmentCompleted;
            _provider.AttemptSkipped += OnAttemptSkipped;
            _provider.RunCompleted += OnRunCompleted;
            _provider.IncompleteExit += OnIncompleteExit;
            _provider.InvalidMarked += OnInvalidMarked;
        }

        public bool IsActive => _active;

        // ── IRoundReporter ─────────────────────────────────────────────

        public void StartRound(string roundId, PickSnapshot pick)
        {
            _roundId = roundId;
            _pick = pick;
            _active = true;
            _isSingle = pick != null && pick.Type == PickType.Single;

            _provider.SetRoundTags(pick?.TimerTags);
            _provider.StartRound(roundId, new RoundPickInfo
            {
                RoundId = roundId,
                ProjectType = _isSingle ? RoundProjectType.Single : RoundProjectType.Multi,
                RetryCount = pick != null ? pick.RetryCount : 0,
                Tags = pick?.TimerTags,
            });
            Plugin.Logger.LogInfo(
                $"[Timer] round {roundId} started via provider ({(_isSingle ? "SINGLE" : "MULTI")}, " +
                $"tags=[{(pick?.TimerTags == null ? "" : string.Join(",", pick.TimerTags))}]).");
        }

        public void Stop()
        {
            if (!_active) return;
            _active = false;
            _provider.StopRound();
            Plugin.Logger.LogInfo("[Timer] round stopped.");
        }

        public void FinishRound()
        {
            if (!_active || string.IsNullOrEmpty(_roundId)) return;
            // 主动结束：MULTI 等价整局完成（带 provider 累计时长）；SINGLE 按
            // 现有尝试成绩计分（服务端把剩余尝试记 N/A）。
            if (_isSingle)
            {
                SendSingleProjectComplete();
            }
            else
            {
                Send(new Dictionary<string, object>
                {
                    { "type", Msg.ProjectComplete },
                    { "round_id", _roundId },
                    { "final_total_ms", _provider.RoundTotalMs },
                });
                Plugin.Logger.LogInfo($"[Timer] project_complete (manual, final={_provider.RoundTotalMs}ms).");
            }
            Stop();
        }

        // ── Provider events → server messages ──────────────────────────

        private void OnSegmentCompleted(SegmentResult seg)
        {
            if (!_active || string.IsNullOrEmpty(_roundId)) return;
            var payload = new Dictionary<string, object>
            {
                { "type", Msg.LevelTimeUpload },
                { "round_id", _roundId },
                { "level_index", seg.Index }, // acts as attempt index for SINGLE
                { "this_level_ms", seg.DurationMs },
            };
            if (!_isSingle) payload["total_ms"] = seg.TotalMs;

            // 完成时刻的有效性判定（INVALID_ATTEMPT_REQ §3.1）：必须在事件处理器
            // 内同步查询——顺序契约是 TwilightTimer 在 SegmentCompleted 返回之后
            // 才清尝试标记，延迟到帧末/队列会看到已清除的空集。非空 = 该次
            // 尝试无效（SINGLE 不计分 / MULTI informational 供裁判仲裁）。
            // 元素格式 "<Reason>"，不可原谅原因带 "!" 前缀（如 "!CheatCode"）。
            var marks = _provider.GetActiveInvalidMarks();
            if (marks != null && marks.Count > 0)
                payload["invalid_reasons"] = new List<string>(FormatReasons(marks));

            Send(payload);
            Plugin.Logger.LogInfo(
                $"[Timer] segment {seg.Index} done: {seg.DurationMs}ms" +
                (!_isSingle ? $" total={seg.TotalMs}ms" : "") +
                (marks != null && marks.Count > 0
                    ? $" invalid=[{string.Join(",", FormatReasons(marks))}]"
                    : "") + ".");
        }

        /// <summary>标记 → 上报格式："<Reason>"，不可原谅带 "!" 前缀。</summary>
        private static IEnumerable<string> FormatReasons(IList<InvalidMarkInfo> marks)
        {
            foreach (var m in marks) yield return (m.Unforgivable ? "!" : "") + m.Reason;
        }

        private void OnAttemptSkipped(int index)
        {
            if (!_active || string.IsNullOrEmpty(_roundId)) return;
            Send(new Dictionary<string, object>
            {
                { "type", Msg.AttemptSkip },
                { "round_id", _roundId },
                { "attempt_index", index },
            });
            Plugin.Logger.LogInfo($"[Timer] attempt {index} skipped.");

            // 方案 A：跳过的是最后一次尝试 → 已无剩余尝试，自动 project_complete
            // （跳过不会触发 RunCompleted，必须在此收尾；retry 缺失时视同无剩余）。
            int retry = _pick != null ? _pick.RetryCount : 0;
            if (_isSingle && (retry <= 0 || index + 1 >= retry))
            {
                SendSingleProjectComplete();
                Stop();
            }
        }

        private void OnRunCompleted(long totalMs)
        {
            if (!_active || string.IsNullOrEmpty(_roundId)) return;

            if (_isSingle)
            {
                // SINGLE 通过 ≠ 放弃剩余尝试（FASTEST 可刷成绩 / AVERAGE 需用满）。
                // 本地合集到最后一关必然触发 RunCompleted；只有该次确为最后一次
                // 尝试（方案 A，无剩余尝试；retry 缺失时视同无剩余）才自动
                // project_complete，其余情况由选手「结束本回合」显式触发
                // （twilightcore-single-attempt-complete.md §3/§4）。
                // 「是否最后一次」按尝试序号判定（本次通关的序号 = segments 末位
                // 的 Index，段按完成顺序追加），不能用通关计数：含跳过时通关数
                // 永远追不上次数上限，最后一次通关会被误判为还有剩余尝试
                // （twilightcore-single-complete-after-skip.md §2）。
                var segments = _provider.GetCompletedSegments();
                int lastIndex = segments != null && segments.Count > 0
                    ? segments[segments.Count - 1].Index
                    : -1;
                int retry = _pick != null ? _pick.RetryCount : 0;
                if (retry > 0 && lastIndex + 1 < retry)
                {
                    Plugin.Logger.LogInfo(
                        $"[Timer] single run completed at attempt {lastIndex + 1}/{retry} — NOT completing (attempts remain).");
                    return;
                }
                SendSingleProjectComplete();
            }
            else
            {
                Send(new Dictionary<string, object>
                {
                    { "type", Msg.ProjectComplete },
                    { "round_id", _roundId },
                    { "final_total_ms", totalMs },
                });
                Plugin.Logger.LogInfo($"[Timer] project_complete (final={totalMs}ms).");
            }
            Stop();
        }

        private void OnIncompleteExit(int index)
        {
            if (!_active || string.IsNullOrEmpty(_roundId)) return;

            // SINGLE with ≥1 valid attempt + exit → normal end (server marks the
            // rest skipped); 0 valid → forfeit. MULTI mid-exit → always forfeit.
            if (_isSingle && _provider.ValidAttemptCount > 0)
            {
                // 期望行为 §3 行 3：退出的若是最后一次尝试，该次本身也要计 N/A
                // （attempt_skip），不能只发 project_complete 让裁判端漏掉明细。
                // 判据按尝试序号（事件参数 index），与 OnAttemptSkipped/OnRunCompleted
                // 同口径——通关计数在含跳过时欠计数、序号也错。
                int retry = _pick != null ? _pick.RetryCount : 0;
                if (retry > 0 && index + 1 >= retry)
                {
                    Send(new Dictionary<string, object>
                    {
                        { "type", Msg.AttemptSkip },
                        { "round_id", _roundId },
                        { "attempt_index", index },
                    });
                    Plugin.Logger.LogInfo($"[Timer] last attempt abandoned → attempt_skip idx={index}.");
                }
                SendSingleProjectComplete();
                Plugin.Logger.LogInfo("[Timer] incomplete exit with valid attempts → project_complete.");
            }
            else
            {
                Send(new Dictionary<string, object>
                {
                    { "type", Msg.ForfeitSignal },
                    { "round_id", _roundId },
                    { "reason", _isSingle ? ForfeitReason.SingleExit0Valid : ForfeitReason.MultiExit },
                });
                Plugin.Logger.LogInfo($"[Timer] forfeit (incomplete exit at {index}).");
            }
            Stop();
        }

        private void OnInvalidMarked(InvalidMarkInfo mark)
        {
            // Log-only for now: server-side arbitration of unforgivable marks
            // (cheats / timeScale) is manual (referee reviews); there is no
            // dedicated upstream message type yet.
            if (!_active) return;
            Plugin.Logger.LogWarning($"[Timer] invalid mark: {mark.Reason}" +
                (mark.Unforgivable ? " (unforgivable)" : "") + ".");
        }

        // ── Helpers ────────────────────────────────────────────────────

        /// <summary>SINGLE 结束（无 final_total_ms——服务端按各次尝试成绩计分）。</summary>
        private void SendSingleProjectComplete()
        {
            Send(new Dictionary<string, object>
            {
                { "type", Msg.ProjectComplete },
                { "round_id", _roundId },
            });
            Plugin.Logger.LogInfo("[Timer] project_complete (single).");
        }

        private void Send(Dictionary<string, object> msg) => _client.Send(msg);
    }
}
