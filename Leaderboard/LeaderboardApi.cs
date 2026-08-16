namespace TwilightCore.Leaderboard
{
    /// <summary>单关计分方式（服务端建赛时指定）。跨 API 以底层 int 传递：0=Fastest, 1=Average。</summary>
    public enum SingleScoring { Fastest = 0, Average = 1 }

    /// <summary>选手回合状态（镜像服务端 player_status.status）。0=Playing 1=Finished 2=Forfeited。</summary>
    public enum PlayerStatus { Playing = 0, Finished = 1, Forfeited = 2 }

    /// <summary>一位选手的回合状态快照。</summary>
    public sealed class LeaderboardPlayerState
    {
        public string Seat;              // "PLAYER_A" / "PLAYER_B"
        public string DisplayName;       // 该席位选手显示名（含本地席位——TwilightTimer 无从自取）
        public PlayerStatus Status;      // 游戏中 / 已完成 / 已弃权
        public int CurrentLevelIndex;    // 0 基；MULTI=合集索引，SINGLE=当前尝试序号；未知 -1
        public long[] LevelTimesMs;      // 已完成分段时长按序（SINGLE=有效尝试；跳过(N/A)不在此列）
        public int[] SkippedIndexes;     // SINGLE 被跳过(记 N/A)的尝试序号
        public long TotalMs;             // MULTI：截至最近完成关卡的累计（= level_time_upload.total_ms 语义）；SINGLE 恒 0
        public long? ScoreMs;            // SINGLE：服务端权威最终成绩；尚不可计（0 有效尝试）为 null
    }

    /// <summary>整回合排行榜快照（不可变对象）。</summary>
    public sealed class LeaderboardSnapshot
    {
        public string RoundId;           // 与 round_start.round_id 一致
        public bool IsSingleProject;     // true=SINGLE, false=MULTI
        public SingleScoring Scoring;    // SINGLE 计分方式；MULTI 忽略
        public int RetryCount;           // SINGLE 允许的尝试次数
        public string[] LevelIds;        // 回合合集的关卡 id 按索引（自包含，重连不依赖本地合集状态）
        public LeaderboardPlayerState PlayerA;
        public LeaderboardPlayerState PlayerB;
    }

    /// <summary>已知静态入口。无注册、无事件——消费方轮询。</summary>
    public static class LeaderboardApi
    {
        public const int ApiVersion = 1;

        /// <summary>回合内返回最后构建的不可变快照；回合外/无数据返回 null。线程安全、从不抛异常。</summary>
        public static LeaderboardSnapshot GetSnapshot() => _snapshot;

        /// <summary>
        /// 本机选手的座席："PLAYER_A" / "PLAYER_B"；未认证/未知/观战为 null。
        /// 消费方据此判定快照中哪一行是本地选手（着色、排序 tiebreak）。
        /// </summary>
        public static string GetLocalSeat() => _localSeat;

        // ── Internal publish seam (main thread only) ──────────────────
        // The match controller publishes here; consumers only poll. The
        // volatile read keeps GetSnapshot cheap and thread-safe even though
        // the real consumer (TwilightTimer) polls on the main thread too.
        private static volatile LeaderboardSnapshot _snapshot;
        private static volatile string _localSeat;

        internal static void Publish(LeaderboardSnapshot snapshot) => _snapshot = snapshot;
        internal static void PublishLocalSeat(string seat) => _localSeat = seat;
        internal static void Clear() => _snapshot = null;
    }
}
