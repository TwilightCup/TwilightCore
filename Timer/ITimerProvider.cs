using System;
using System.Collections.Generic;

namespace TwilightCore.Timer
{
    /// <summary>回合项目类型（来自服务端 pick）。</summary>
    public enum RoundProjectType { Multi = 0, Single = 1 }

    /// <summary>
    /// 回合 pick 信息（服务端 round_start pick 载荷的子集；黄昏杯词条已按
    /// 映射表转换为提供方标签 id：Checkpoint / NoCheckpoint / Jumpless 透传，
    /// Glitchless / Pinch / No EC / Achievement 丢弃）。
    /// </summary>
    public sealed class RoundPickInfo
    {
        public string RoundId;               // 服务端回合 id（对提供方不透明）
        public RoundProjectType ProjectType; // MULTI / SINGLE
        public int RetryCount;               // SINGLE：允许的尝试次数
        public IList<string> Tags;           // 要启用的标签 id（未知 id 由提供方忽略）
    }

    /// <summary>一个已完成的分段（MULTI=关卡，SINGLE=尝试）。</summary>
    public sealed class SegmentResult
    {
        public int Index;       // 回合内序号（MULTI：合集索引；SINGLE：尝试序号）
        public long DurationMs; // 本段时长（游戏时间，毫秒）
        public long TotalMs;    // 截至本段结束的回合累计（SINGLE 与 DurationMs 相同）
        public bool Passed;
        public bool Skipped;
    }

    /// <summary>当前回合上活跃的无效标记。</summary>
    public sealed class InvalidMarkInfo
    {
        public string Reason;     // 稳定字符串 id，如 "CheatCode"、"TimeScale"、"Drift"
        public bool Unforgivable; // true：作弊/变速类（供服务端仲裁）
    }

    /// <summary>
    /// 计时器提供方契约。由 TwilightCore 拥有，计时器插件（HSRTimer
    /// TwilightTimer 分支）实现并在加载时经 <see cref="TimerProviderRegistry"/>
    /// 自注册——依赖恒为计时器 → TwilightCore 单向（见
    /// docs/ITimerProvider接口需求.md / docs/HSRTimer黄昏杯适配需求.md §2）。
    /// 变更方法可从任意线程调用（实现方负责编组到主线程）；查询返回时点快照、
    /// 从不抛异常；事件在主线程触发，提供方容忍订阅方异常。
    /// </summary>
    public interface ITimerProvider
    {
        int ApiVersion { get; }              // 当前版本：1

        // ── 比赛模式 ──
        bool InMatchMode { get; }
        void EnterMatchMode();               // 快照用户标签/设置；锁定冲突 UI
        void ExitMatchMode();                // 恢复快照

        // ── 回合生命周期 ──
        bool InRound { get; }
        void StartRound(string roundId, RoundPickInfo pick); // 完整重置；计时等待首个分段边沿
        void StopRound();                                    // 停止计时；数据保留可查至下次 StartRound

        // ── 标签推送（回合内权威；未知 id 记日志忽略）──
        void SetRoundTags(IList<string> tagIds);

        // ── 查询（随时安全）──
        bool IsInSegment { get; }
        long CurrentSegmentMs { get; }       // 当前分段累计（游戏时间 ms）
        long RoundTotalMs { get; }           // 本回合累计（游戏时间 ms）
        int ValidAttemptCount { get; }       // SINGLE：有效（通关）尝试数
        IList<SegmentResult> GetCompletedSegments();    // 返回快照副本
        IList<InvalidMarkInfo> GetActiveInvalidMarks(); // 返回快照副本

        // ── 事件（主线程；实现方容忍订阅方异常）──
        event Action<SegmentResult> SegmentCompleted; // 关卡通关（分段完成）
        event Action<int> AttemptSkipped;             // SINGLE 尝试被跳过（携带尝试序号）
        event Action<long> RunCompleted;              // 整局完成（游戏时间总时长 ms）
        event Action<int> IncompleteExit;             // 未通关退出且之后无下一段（携带序号）
        event Action<InvalidMarkInfo> InvalidMarked;  // 回合内新增无效标记
    }

    /// <summary>
    /// 可选扩展（断线重连恢复）：<see cref="ITimerProvider"/> 实现本接口即声明
    /// 支持在不重置既有回合数据的前提下重新激活计时。旧版提供方不实现该接口
    /// 仍可工作（重连恢复退化为重置，见 ProviderRoundReporter）。
    /// </summary>
    public interface IResumableTimerProvider
    {
        /// <summary>
        /// 恢复同一回合的计时：不得清空已完成分段/累计时长/有效尝试数；若当前
        /// 已处于分段中，恢复计时累加。roundId 与既有回合不一致时实现方忽略。
        /// </summary>
        void ResumeRound(string roundId, RoundPickInfo pick);
    }

    /// <summary>
    /// 静态注册入口。后注册者胜；卸载时注销。线程安全。
    /// <c>Register(null)</c> 无操作。计时器插件在自身加载完成后注册、卸载前注销；
    /// TwilightCore 在每个回合开始时读取 <see cref="Current"/>（注册晚于
    /// TwilightCore 启动是常态——BepInEx 依赖链让 TwilightCore 先 Awake）。
    /// </summary>
    public static class TimerProviderRegistry
    {
        private static readonly object Gate = new object();
        private static ITimerProvider _current;

        /// <summary>当前注册的提供方；未注册时为 null（调用方降级模拟计时器）。</summary>
        public static ITimerProvider Current
        {
            get { lock (Gate) { return _current; } }
        }

        public static void Register(ITimerProvider provider)
        {
            if (provider == null) return;
            lock (Gate) { _current = provider; }
            Plugin.Logger.LogInfo($"[Twilight] timer provider registered (api v{provider.ApiVersion}).");
        }

        /// <summary>注销。仅当当前提供方即参数所指时清空（避免旧实例注销掉新注册者）。</summary>
        public static bool Unregister(ITimerProvider provider)
        {
            if (provider == null) return false;
            lock (Gate)
            {
                if (!ReferenceEquals(_current, provider)) return false;
                _current = null;
            }
            Plugin.Logger.LogInfo("[Twilight] timer provider unregistered.");
            return true;
        }
    }
}
