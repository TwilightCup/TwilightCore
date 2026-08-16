using System.Collections.Generic;
using TwilightCore.Leaderboard;
using TwilightCore.Match;
using TwilightCore.Net;

namespace TwilightCore.LeaderboardInternal;

/// <summary>
/// Accumulates the server-fed round state into the immutable snapshots served
/// by <see cref="LeaderboardApi"/> (LEADERBOARD_REQ v1). All entry points run
/// on the main thread (inbound messages arrive via MainThreadDispatcher).
///
/// <para><b>Source of truth is <c>player_status</c></b>: the server broadcasts
/// it after EVERY progress event (level_time_upload, attempt_skip,
/// project_complete, forfeit) carrying the seat's COMPLETE completed_levels /
/// attempts lists, and answers reconnect_resync with one player_status per
/// seat — so a full rebuild from a single message yields a correct snapshot in
/// all cases, reconnect included. <c>level_time_update</c> is applied on top
/// as an incremental pre-update (it lands just before its player_status).</para>
///
/// <para>Server status values (1=IN_GAME, 2=COMPLETED, 3=FORFEITED) are mapped
/// to the contract's 0-based PlayerStatus. DisplayName comes from auth_ok's
/// player_a_name / player_b_name (the server sends both seats' names to every
/// connection, including mid-round reconnects). Scoring: pick.single_scoring
/// ("fastest"/"average") once the server starts sending it (req §4.2-B);
/// absent → Fastest, matching the server's own ScoringMethod.FASTEST
/// fallback.</para>
/// </summary>
internal static class LeaderboardTracker
{
    private static readonly string[] EmptyLevelIds = new string[0];
    private static readonly List<object> EmptyList = new List<object>();

    // Round-scoped state (valid while _roundId != null).
    private static string _roundId;
    private static bool _isSingle;
    private static SingleScoring _scoring;
    private static int _retryCount;
    private static string[] _levelIds = EmptyLevelIds;
    private static readonly PlayerAccumulator A = new PlayerAccumulator();
    private static readonly PlayerAccumulator B = new PlayerAccumulator();

    // Seat display names, captured at auth (persist across rounds).
    private static string _nameA;
    private static string _nameB;

    /// <summary>Capture both seats' display names from auth_ok (present on
    /// every connection, including mid-round reconnects). Local seat publishes
    /// only for the two player seats — the contract says spectators get null.</summary>
    public static void OnAuthenticated(Dictionary<string, object> authOk)
    {
        _nameA = authOk.GetString("player_a_name");
        _nameB = authOk.GetString("player_b_name");
        string seat = authOk.GetString("seat");
        LeaderboardApi.PublishLocalSeat(
            seat == "PLAYER_A" || seat == "PLAYER_B" ? seat : null);
    }

    /// <summary>
    /// Create the round snapshot (round_start). levelIds is the parsed levels
    /// list PRE-SINGLE-expansion (retry_count copies collapse back to one id):
    /// MULTI indexes then match collection indexes, and SINGLE's attempt
    /// indexes are 0..retry-1 regardless — the consumer only looks up MULTI
    /// indexes, so the id array stays the honest collection shape.
    /// </summary>
    public static void OnRoundStart(
        string roundId,
        Dictionary<string, object> pick,
        List<string> parsedLevelIds)
    {
        _roundId = roundId;
        _isSingle = RoundIngestion.IsSinglePick(pick);
        _scoring = ParseScoring(pick);
        _retryCount = pick.GetInt("retry_count", 0);

        int n = parsedLevelIds != null ? parsedLevelIds.Count : 0;
        _levelIds = new string[n];
        for (int i = 0; i < n; i++)
            _levelIds[i] = parsedLevelIds[i] ?? "";

        A.Reset("PLAYER_A", _nameA);
        B.Reset("PLAYER_B", _nameB);
        Publish();
    }

    /// <summary>Full rebuild of one seat's row from a player_status message
    /// (complete lists — also the reconnect_resync reply shape).</summary>
    public static void OnPlayerStatus(Dictionary<string, object> msg)
    {
        if (_roundId == null) return;
        var acc = SeatAccumulator(msg.GetString("seat"));
        if (acc == null) return;

        acc.Status = MapStatus(msg.GetInt("status", 1));            // server: 1/2/3
        acc.CurrentLevelIndex = msg.GetInt("current_level_index", -1);

        // MULTI: completed_levels = [{level_index, time_ms, total_ms}, …]
        // (server upserts by level_index → index-unique). Sorted by index so
        // out-of-order arrival still yields an ordered list. TotalMs is the
        // cumulative at the LAST completed level (= the arrival-total the HUD
        // shows; final_total_ms only exists in round_result, out of scope).
        // SINGLE: attempts = [{index, status(1=VALID, 2=SKIPPED,
        // 3=UNFINISHED), time_ms}, …]; LevelTimesMs takes VALID times only,
        // SkippedIndexes the SKIPPED indexes (UNFINISHED is in neither).
        if (_isSingle)
        {
            var skips = new List<int>();
            var times = new List<long>();
            foreach (var o in msg.GetList("attempts") ?? EmptyList)
            {
                if (!(o is Dictionary<string, object> a)) continue;
                int idx = a.GetInt("index", -1);
                int st = a.GetInt("status", 3);
                if (st == 2) skips.Add(idx);
                else if (st == 1) times.Add(a.GetLong("time_ms", 0));
            }
            skips.Sort();
            acc.SetProgress(times, skips);
        }
        else
        {
            var levels = new List<Dictionary<string, object>>();
            foreach (var o in msg.GetList("completed_levels") ?? EmptyList)
                if (o is Dictionary<string, object> d) levels.Add(d);
            levels.Sort((x, y) => x.GetInt("level_index", 0).CompareTo(y.GetInt("level_index", 0)));
            var times = new List<long>(levels.Count);
            long total = 0;
            foreach (var l in levels)
            {
                times.Add(l.GetLong("time_ms", 0));
                long t = l.GetLong("total_ms", 0);
                if (t > 0) total = t;
            }
            acc.SetProgress(times, null);
            acc.TotalMs = total;
        }

        Publish();
    }

    /// <summary>
    /// Incremental pre-update from level_time_update (lands just before its
    /// player_status): MULTI upserts the segment + arrival total; SINGLE
    /// upserts the attempt time. Idempotent by index, same as the server's
    /// own upsert semantics.
    /// </summary>
    public static void OnLevelTimeUpdate(Dictionary<string, object> msg)
    {
        if (_roundId == null) return;
        var acc = SeatAccumulator(msg.GetString("seat"));
        if (acc == null) return;

        int idx = msg.GetInt("level_index", -1);
        if (idx < 0) return;
        long thisMs = msg.GetLong("this_level_ms", 0);

        if (_isSingle)
        {
            acc.Upsert(idx, thisMs);
            acc.RemoveSkip(idx);
        }
        else
        {
            acc.Upsert(idx, thisMs);
            long total = msg.GetLong("total_ms", 0);
            if (total > acc.TotalMs) acc.TotalMs = total;
        }
        Publish();
    }

    /// <summary>Drop the snapshot (leaving IN_ROUND / match_end / ingestion
    /// failure). The next round_start rebuilds from scratch.</summary>
    public static void Clear()
    {
        _roundId = null;
        _levelIds = EmptyLevelIds;
        LeaderboardApi.Clear();
    }

    // ── Internals ──────────────────────────────────────────────────

    /// <summary>Publish a fresh immutable snapshot. ScoreMs is computed from
    /// the valid-attempt times (min / mean — identical rules to the server's
    /// scoring.single_score), null when there are none yet.</summary>
    private static void Publish()
    {
        LeaderboardApi.Publish(new LeaderboardSnapshot
        {
            RoundId = _roundId,
            IsSingleProject = _isSingle,
            Scoring = _scoring,
            RetryCount = _isSingle ? _retryCount : 0,
            LevelIds = _levelIds,
            PlayerA = A.Build(_isSingle ? _scoring : SingleScoring.Fastest),
            PlayerB = B.Build(_isSingle ? _scoring : SingleScoring.Fastest),
        });
    }

    private static PlayerAccumulator SeatAccumulator(string seat)
        => seat == "PLAYER_A" ? A : seat == "PLAYER_B" ? B : null;

    /// <summary>Server PlayerStatus (1=IN_GAME, 2=COMPLETED, 3=FORFEITED) →
    /// contract (0=Playing, 1=Finished, 2=Forfeited).</summary>
    private static PlayerStatus MapStatus(int serverStatus)
    {
        switch (serverStatus)
        {
            case 2: return PlayerStatus.Finished;
            case 3: return PlayerStatus.Forfeited;
            default: return PlayerStatus.Playing;
        }
    }

    /// <summary>
    /// Gap-B seam: read the scoring method from the raw pick dict once the
    /// server starts sending pick.single_scoring ("fastest"/"average"; ints
    /// 1/2 = server enum also tolerated). Absent/unknown → Fastest.
    /// </summary>
    private static SingleScoring ParseScoring(Dictionary<string, object> pick)
    {
        if (pick == null || !pick.TryGetValue("single_scoring", out var o) || o == null)
            return SingleScoring.Fastest;
        switch (o)
        {
            case long l: return l == 2 ? SingleScoring.Average : SingleScoring.Fastest;
            case int i: return i == 2 ? SingleScoring.Average : SingleScoring.Fastest;
            case double dd: return (int)dd == 2 ? SingleScoring.Average : SingleScoring.Fastest;
        }
        var s = o.ToString().Trim().ToLowerInvariant();
        return s == "average" || s == "2" ? SingleScoring.Average : SingleScoring.Fastest;
    }

    /// <summary>
    /// Mutable per-seat accumulator; <see cref="Build"/> freezes it into the
    /// immutable contract DTO. Segment times are kept sorted by index
    /// (binary-search upsert), so LevelTimesMs is ordered even when messages
    /// arrive out of order (reconnect backfill).
    /// </summary>
    private sealed class PlayerAccumulator
    {
        public string Seat;
        public string DisplayName;
        public PlayerStatus Status;
        public int CurrentLevelIndex = -1;
        public long TotalMs;

        private readonly List<int> _indexes = new List<int>(16);
        private readonly List<long> _ms = new List<long>(16);
        private readonly List<int> _skips = new List<int>(8);

        public void Reset(string seat, string displayName)
        {
            Seat = seat;
            DisplayName = displayName;
            Status = PlayerStatus.Playing;
            CurrentLevelIndex = -1;
            TotalMs = 0;
            _indexes.Clear();
            _ms.Clear();
            _skips.Clear();
        }

        /// <summary>Replace all progress from a complete player_status payload.</summary>
        public void SetProgress(List<long> times, List<int> skips)
        {
            _indexes.Clear();
            _ms.Clear();
            for (int i = 0; i < times.Count; i++) { _indexes.Add(i); _ms.Add(times[i]); }
            _skips.Clear();
            if (skips != null) _skips.AddRange(skips);
        }

        public void Upsert(int idx, long ms)
        {
            int lo = 0, hi = _indexes.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                int c = _indexes[mid].CompareTo(idx);
                if (c == 0) { _ms[mid] = ms; return; }
                if (c < 0) lo = mid + 1; else hi = mid;
            }
            _indexes.Insert(lo, idx);
            _ms.Insert(lo, ms);
        }

        public void RemoveSkip(int idx) => _skips.Remove(idx);

        public LeaderboardPlayerState Build(SingleScoring scoring)
        {
            var times = new long[_ms.Count];
            _ms.CopyTo(times);
            var skips = new int[_skips.Count];
            _skips.CopyTo(skips);

            long? score = null;
            if (times.Length > 0)
            {
                if (scoring == SingleScoring.Average)
                {
                    long sum = 0;
                    foreach (var v in times) sum += v;
                    score = sum / times.Length;
                }
                else
                {
                    long min = long.MaxValue;
                    foreach (var v in times) if (v < min) min = v;
                    score = min;
                }
            }

            return new LeaderboardPlayerState
            {
                Seat = Seat,
                DisplayName = DisplayName ?? "",
                Status = Status,
                CurrentLevelIndex = CurrentLevelIndex,
                LevelTimesMs = times,
                SkippedIndexes = skips,
                TotalMs = TotalMs,
                ScoreMs = score,
            };
        }
    }
}
