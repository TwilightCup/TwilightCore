using System.Collections.Generic;
using System.Text;
using TwilightCore.Leaderboard;
using TwilightCore.Match;
using TwilightCore.Net;
using TwilightCore.Timer;
using UnityEngine;

namespace TwilightCore.Subsegment;

/// <summary>
/// MULTI-round live time-gap tracking ("subsegment"). Two jobs, both driven
/// from authoritative state every frame (no event toggling to undo):
///
/// 1. <b>Recording</b>: in each level of a match collection run, from the FIRST
///    wake-up (the character leaving the limp states Spawning/Unconscious/Dead —
///    later limp episodes from the manual play-dead key or checkpoint respawns
///    do NOT restart it) until the level ACTUALLY completes, sample once per
///    interval: the player's own accumulated time, position and movement
///    vector, sent to the server which relays them to the opponent.
///    Completion is the death-triggered advance (CollectionManager's
///    LevelCompleted — the same edge the timer's segment ends on): touching
///    the pass zone alone is NOT a pass in this game, and the walk/fall to
///    the exit after it is part of the measured time.
/// 2. <b>Detection</b>: the opponent's relayed samples become virtual planes
///    (perpendicular to their movement vector, large lateral radius). Each
///    frame while recording we compute the signed distance of our position to
///    each plane of the CURRENT level; a negative→non-negative flip within the
///    lateral radius is a crossing and is reported with our own current time.
///    Pure math — no Unity colliders, so nothing perturbs level physics and a
///    fast-moving body cannot tunnel through (a half-space flip is always seen).
///
/// The time source is the REGISTERED real timer (TwilightTimer) via
/// <see cref="TimerProviderRegistry.Current"/>: every sample and hit carries
/// the provider's <c>RoundTotalMs</c> — the same timeline the official level
/// times are measured on, so gaps line up with scoring. The provider is
/// resolved lazily per use (the timer plugin registers after our Awake).
/// The <c>SimulatedTimer</c> fallback is deliberately NEVER consulted — it is
/// an early debug stand-in slated for removal; without a registered provider
/// the tracker stays idle (logged once per round).
///
/// Data-only by design: no in-game display — the server stores samples and
/// hit times per round (transient, never persisted) and broadcasts
/// <c>subsegment_gap</c> for referees/director overlays. Inspect locally with
/// <c>twi subseg status</c>.
/// </summary>
internal sealed class SubsegmentTracker : MonoBehaviour
{
    private enum SegState
    {
        Idle,      // no round / not in a MULTI collection run
        Armed,     // level started, waiting for the first wake-up
        Recording, // awake → pass zone: sample + detect crossings
        Done,      // level finalised (pass/skip/run end); keeps planes until round end
    }

    private sealed class Plane
    {
        public int LevelIndex;
        public int Seq;
        public Vector3 Pos;
        public Vector3 Normal; // normalised sample displacement
        public bool Armed;     // previous signed distance initialised?
        public float PrevD;
        public bool Hit;       // crossing already reported (one report per plane)
    }

    public static SubsegmentTracker Instance { get; private set; }

    private TwilightClient _client;
    private string _roundId;

    private readonly Dictionary<int, List<Plane>> _planesByLevel = new Dictionary<int, List<Plane>>();
    private readonly Dictionary<int, HashSet<int>> _seenSeqByLevel = new Dictionary<int, HashSet<int>>();
    // Opponent's latest sample seq per level — every sample counts (including
    // zero-displacement ones), because the completion sync targets their
    // touch-time final sample, which may legitimately be stationary.
    private readonly Dictionary<int, int> _lastOppSeqByLevel = new Dictionary<int, int>();

    private SegState _state = SegState.Idle;
    private int _levelIndex = -1;
    private int _seq;                    // next sample sequence number within the level
    private float _nextSampleAt;         // realtime of the next periodic sample
    private Vector3 _lastSamplePos;
    private bool _hasLastSamplePos;

    private readonly Queue<string> _recentGaps = new Queue<string>(8);
    private bool _disabledByServer;      // old server: 400 on our subsegment sends
    private bool _providerMissingLogged; // log the idle reason once per round
    private float _lastSubsegSendRealtime = -1f;

    // ── Wiring ───────────────────────────────────────────────────────

    public void Init(TwilightClient client) => _client = client;

    private void Awake() => Instance = this;

    private void OnDestroy() => Instance = null;

    private void Start()
    {
        var mgr = CollectionManager.Instance;
        if (mgr == null) return;
        mgr.LevelStarted += OnLevelStarted;
        mgr.LevelCompleted += OnLevelCompleted;
        mgr.RunCompleted += OnRunEnded;
        mgr.RunAborted += OnRunEnded;
    }

    // CollectionManager invokes its events from its own progression paths;
    // keep each handler cheap and never let one throw into the engine.
    private void OnLevelStarted(string levelId, int idx)
    {
        try { NotifyLevelStarted(idx); }
        catch (System.Exception ex) { Log($"level-start handler failed: {ex.Message}"); }
    }

    private void OnLevelCompleted(int idx, bool skipped)
    {
        try { NotifyLevelCompleted(idx, skipped); }
        catch (System.Exception ex) { Log($"level-complete handler failed: {ex.Message}"); }
    }

    private void OnRunEnded()
    {
        try { NotifyRunEnded(); }
        catch (System.Exception ex) { Log($"run-end handler failed: {ex.Message}"); }
    }

    // ── Public notify API (called by MatchController / the Harmony postfix) ──

    /// <summary>Round began (round_start ingested): full reset, then remember the round id.</summary>
    public void OnRoundStart(string roundId)
    {
        ResetRound();
        _roundId = roundId;
        _disabledByServer = false;
        _providerMissingLogged = false;
        Log($"round {roundId}: tracking armed (MULTI).");
    }

    /// <summary>Drop everything (round over / match end / terminal disconnect / ingestion failure).</summary>
    public void ResetRound()
    {
        _state = SegState.Idle;
        _levelIndex = -1;
        _seq = 0;
        _hasLastSamplePos = false;
        _nextSampleAt = 0f;
        _planesByLevel.Clear();
        _seenSeqByLevel.Clear();
        _lastOppSeqByLevel.Clear();
        _recentGaps.Clear();
        _lastSubsegSendRealtime = -1f;
    }

    /// <summary>A level of the collection launched: arm for its first wake-up.</summary>
    public void NotifyLevelStarted(int levelIndex)
    {
        if (!Enabled) return;
        _levelIndex = levelIndex;
        _state = SegState.Armed;
        _seq = 0;
        _hasLastSamplePos = false;
    }

    /// <summary>
    /// Level over (the death-triggered pass, or a skip). On a real pass this is
    /// the TRUE completion edge the timer's segment ends on — emit the final
    /// sample and the completion sync here (touching the pass zone alone is
    /// not a pass; the walk/fall to the exit after it is measured time).
    /// Skipped levels just stop recording.
    /// </summary>
    public void NotifyLevelCompleted(int levelIndex, bool skipped)
    {
        if (!Enabled) return;
        bool wasRecording = _state == SegState.Recording;
        if (wasRecording && !skipped)
        {
            var human = Human.Localplayer;
            if (human != null && human)
            {
                Vector3 pos = human.transform.position;
                EmitSample(pos, DisplacementSinceLastSample(pos));
            }
            SendCompletionSync();
        }
        FinaliseSegment();
        if (wasRecording)
            Log($"level {levelIndex} segment closed{(!skipped ? "" : " (skipped)")}.");
    }

    public void NotifyRunEnded() => FinaliseSegment();

    /// <summary>Server error routed from MatchController — trips the old-server breaker on a 400.</summary>
    public void NotifyServerError(int code)
    {
        // A 400 shortly after a subsegment send means the server rejected the
        // message itself (unknown type = not upgraded); other 400s (e.g. a bad
        // ! command) never follow a subsegment send and must not trip this.
        if (code != 400 || _disabledByServer || _lastSubsegSendRealtime < 0f) return;
        if (Time.realtimeSinceStartup - _lastSubsegSendRealtime > 3f) return;
        _disabledByServer = true;
        Log("server rejected subsegment messages (not upgraded?) — sampling disabled until next round.");
    }

    // ── Inbound (routed from MatchController.Handle, main thread) ────

    /// <summary>Opponent sample relayed by the server: add its virtual plane.</summary>
    public void OnOpponentSample(Dictionary<string, object> msg)
    {
        if (!Enabled || msg == null) return;
        if (_roundId == null || msg.GetString("round_id") != _roundId) return;

        int level = msg.GetInt("level_index", -1);
        int seq = msg.GetInt("seq", -1);
        if (level < 0 || seq < 0) return;

        var seen = _seenSeqByLevel.TryGetValue(level, out var set) ? set : _seenSeqByLevel[level] = new HashSet<int>();
        if (!seen.Add(seq)) return; // reconnect replay / duplicate relay
        _lastOppSeqByLevel[level] = seq;

        float dx = msg.GetFloat("dx"), dy = msg.GetFloat("dy"), dz = msg.GetFloat("dz");
        var dir = new Vector3(dx, dy, dz);
        if (dir.sqrMagnitude < 1e-8f) return; // stationary sample: stored server-side, no plane

        var planes = _planesByLevel.TryGetValue(level, out var list) ? list : _planesByLevel[level] = new List<Plane>();
        planes.Add(new Plane
        {
            LevelIndex = level,
            Seq = seq,
            Pos = new Vector3(msg.GetFloat("px"), msg.GetFloat("py"), msg.GetFloat("pz")),
            Normal = dir.normalized,
        });
    }

    /// <summary>Live gap broadcast (our crossing or the opponent's): log for status/debug.</summary>
    public void OnGap(Dictionary<string, object> msg)
    {
        if (msg == null) return;
        string line = $"L{msg.GetInt("level_index")} seq{msg.GetInt("seq")} " +
                      $"{msg.GetString("hit_seat")} hit @ {msg.GetLong("hit_ms")} ms " +
                      $"(owner {msg.GetString("seat")} @ {msg.GetLong("sample_ms")} ms) " +
                      $"gap {msg.GetLong("gap_ms")} ms";
        Plugin.Logger.LogInfo($"[Subsegment] {line}");
        _recentGaps.Enqueue(line);
        while (_recentGaps.Count > 8) _recentGaps.Dequeue();
    }

    /// <summary>Human-readable state for <c>twi subseg status</c>.</summary>
    public string StatusString()
    {
        var sb = new StringBuilder();
        sb.Append("subsegment: ").Append(Enabled ? "enabled" : "DISABLED (Features.EnableSubsegment=false)");
        if (_disabledByServer) sb.Append(" | halted by server (old backend?)");
        sb.Append("\nround: ").Append(_roundId ?? "(none)")
          .Append("  state: ").Append(_state)
          .Append("  level: ").Append(_levelIndex)
          .Append("  next seq: ").Append(_seq);
        var p = TimerProviderRegistry.Current;
        if (p != null)
            sb.Append("\ntimer: round=").Append(p.InRound)
              .Append(" total=").Append(p.RoundTotalMs).Append(" ms");
        else
            sb.Append("\ntimer: none registered — tracking idle");
        if (_planesByLevel.Count > 0)
        {
            sb.Append("\nplanes: ");
            bool first = true;
            foreach (var kv in _planesByLevel)
            {
                if (!first) sb.Append(", ");
                sb.Append("L").Append(kv.Key).Append('=').Append(kv.Value.Count);
                first = false;
            }
        }
        foreach (var g in _recentGaps)
            sb.Append("\n  ").Append(g);
        return sb.ToString();
    }

    // ── Per-frame driver ────────────────────────────────────────────

    private void Update()
    {
        if (!Enabled || _disabledByServer) return;

        // Re-evaluate from authoritative state every frame: the tracker is
        // alive only inside a MULTI round of a live collection run. A transient
        // disconnect keeps MatchSession.Phase == InRound, so sampling and plane
        // detection continue while offline (sends are dropped by TwilightClient;
        // the server replays the opponent's samples on reconnect).
        var session = MatchSession.Instance;
        bool active = session != null
                      && session.Phase == MatchPhase.InRound
                      && session.Pick != null
                      && session.Pick.Type == PickType.Multi
                      && CollectionManager.Instance != null
                      && CollectionManager.Instance.IsInCollectionRun;
        if (!active)
        {
            if (_state != SegState.Idle) ResetRound(); // self-heal (explicit resets also exist)
            return;
        }

        var human = Human.Localplayer;
        if (human == null || !human) return; // menu / loading — Unity fake-null after scene changes

        // Sample and hit timestamps come from the real timer's timeline; with
        // no provider registered there is nothing valid to report (the
        // SimulatedTimer fallback is never used for subsegments).
        if (TimerProviderRegistry.Current == null)
        {
            if (!_providerMissingLogged)
            {
                _providerMissingLogged = true;
                Log("no timer provider registered — tracking idle (SimulatedTimer is not used).");
            }
            return;
        }

        switch (_state)
        {
            case SegState.Armed:
                DetectWakeUp(human);
                break;
            case SegState.Recording:
                Vector3 pos = human.transform.position;
                CheckPlaneCrossings(pos);
                SampleIfDue(pos);
                break;
        }
    }

    private void DetectWakeUp(Human human)
    {
        // Guard on the game state so the menu ragdoll (or the 'Empty' transition
        // dwell) can't arm a segment; the wake itself is the character leaving
        // the limp states (Spawning/Unconscious/Dead) — the spawn fall lands,
        // 3 s of unconsciousness elapse, then state flips to Fall.
        if (Game.instance == null || Game.instance.state != GameState.PlayingLevel) return;
        if (IsLimp(human.state)) return;

        _state = SegState.Recording;
        Vector3 pos = human.transform.position;
        _lastSamplePos = pos;
        _hasLastSamplePos = true;
        _nextSampleAt = Time.realtimeSinceStartup + Interval;
        _seq = 0;
        EmitSample(pos, Vector3.zero); // first sample: no displacement interval yet
        Log($"level {_levelIndex}: wake-up detected — recording.");
    }

    private void SampleIfDue(Vector3 pos)
    {
        float now = Time.realtimeSinceStartup;
        if (now < _nextSampleAt) return;
        EmitSample(pos, DisplacementSinceLastSample(pos));
        // Fixed-interval schedule, but after a hitch skip the backlog instead
        // of burst-sending stale samples.
        _nextSampleAt += Interval;
        if (_nextSampleAt < now - Interval) _nextSampleAt = now + Interval;
    }

    private void CheckPlaneCrossings(Vector3 pos)
    {
        if (!_planesByLevel.TryGetValue(_levelIndex, out var planes) || planes.Count == 0) return;
        float radius = TwilightConfig.SubsegmentPlaneRadius.Value;
        float radiusSq = radius * radius;

        for (int i = 0; i < planes.Count; i++)
        {
            var p = planes[i];
            float d = Vector3.Dot(pos - p.Pos, p.Normal);
            if (!p.Armed)
            {
                // Activation frame: just seed the signed distance — a plane we
                // are already past (or inside) never fires.
                p.PrevD = d;
                p.Armed = true;
                continue;
            }
            if (!p.Hit && p.PrevD < 0f && d >= 0f)
            {
                Vector3 lateral = (pos - p.Pos) - p.Normal * d;
                if (lateral.sqrMagnitude <= radiusSq)
                {
                    p.Hit = true;
                    SendHit(p);
                }
            }
            p.PrevD = d;
        }
    }

    // ── Internals ───────────────────────────────────────────────────

    private static bool IsLimp(HumanState s) =>
        s == HumanState.Spawning || s == HumanState.Unconscious || s == HumanState.Dead;

    private bool Enabled => TwilightConfig.EnableSubsegment.Value;

    private static float Interval => TwilightConfig.SubsegmentSampleInterval.Value;

    private Vector3 DisplacementSinceLastSample(Vector3 pos) =>
        _hasLastSamplePos ? MaybeZeroed(pos - _lastSamplePos) : Vector3.zero;

    private static Vector3 MaybeZeroed(Vector3 dir) =>
        dir.magnitude < TwilightConfig.SubsegmentMinMove.Value ? Vector3.zero : dir;

    /// <summary>
    /// Completion sync at the true level-completion edge: the finish is the one
    /// comparison point BOTH players are guaranteed to share, so when routes
    /// diverged and skipped every remaining plane, force one comparison here.
    /// The opponent's LAST sample of this level is their own completion-time
    /// sample (periodic sampling stops at their completion edge), so reporting
    /// our finish time against it yields exactly the level-completion gap —
    /// but only once the opponent has provably left this level (their
    /// player_status advances past a level only when its time is uploaded).
    /// Whichever player finishes second sends the sync, so each level gets
    /// exactly one; a genuine earlier crossing of that sample wins (the server
    /// keeps first hits only). A level abandoned via a mid-round `lc skip`
    /// leaves the opponent's last sample mid-level — the sync then compares
    /// against that point instead (rare debug-path edge).
    /// </summary>
    private void SendCompletionSync()
    {
        if (!_lastOppSeqByLevel.TryGetValue(_levelIndex, out int oppSeq))
            return; // opponent never sampled this level

        var snap = LeaderboardApi.GetSnapshot();
        string localSeat = LeaderboardApi.GetLocalSeat();
        if (snap == null || localSeat == null) return;
        var opp = snap.PlayerA != null && snap.PlayerA.Seat == localSeat ? snap.PlayerB : snap.PlayerA;
        if (opp == null || opp.CurrentLevelIndex <= _levelIndex) return;

        // Skip locally if we already crossed that plane for real — the server
        // would drop the duplicate hit anyway.
        if (_planesByLevel.TryGetValue(_levelIndex, out var planes))
            for (int i = planes.Count - 1; i >= 0; i--)
                if (planes[i].Seq == oppSeq && planes[i].Hit) return;

        _client?.Send(new Dictionary<string, object>
        {
            { "type", Msg.SubsegmentHit },
            { "round_id", _roundId },
            { "level_index", _levelIndex },
            { "seq", oppSeq },
            { "t_ms", CurrentTotalMs() },
        });
        _lastSubsegSendRealtime = Time.realtimeSinceStartup;
        Plugin.Logger.LogInfo(
            $"[Subsegment] completion sync L{_levelIndex} vs opponent seq{oppSeq} at t={CurrentTotalMs()} ms.");
    }

    /// <summary>Current total time on the real timer's timeline (0 if no provider).</summary>
    private long CurrentTotalMs()
    {
        var p = TimerProviderRegistry.Current;
        return p != null ? p.RoundTotalMs : 0L;
    }

    private void FinaliseSegment()
    {
        if (_state == SegState.Recording) _state = SegState.Done;
    }

    private void EmitSample(Vector3 pos, Vector3 dir)
    {
        _lastSamplePos = pos;
        _hasLastSamplePos = true;
        _client?.Send(new Dictionary<string, object>
        {
            { "type", Msg.SubsegmentSample },
            { "round_id", _roundId },
            { "level_index", _levelIndex },
            { "seq", _seq },
            { "t_ms", CurrentTotalMs() },
            { "px", pos.x }, { "py", pos.y }, { "pz", pos.z },
            { "dx", dir.x }, { "dy", dir.y }, { "dz", dir.z },
        });
        _seq++;
        _lastSubsegSendRealtime = Time.realtimeSinceStartup;
        Plugin.Logger.LogDebug($"[Subsegment] sample L{_levelIndex} seq{_seq - 1} t={CurrentTotalMs()} ms dir={dir}");
    }

    private void SendHit(Plane p)
    {
        _client?.Send(new Dictionary<string, object>
        {
            { "type", Msg.SubsegmentHit },
            { "round_id", _roundId },
            { "level_index", p.LevelIndex },
            { "seq", p.Seq },
            { "t_ms", CurrentTotalMs() },
        });
        _lastSubsegSendRealtime = Time.realtimeSinceStartup;
        Plugin.Logger.LogInfo(
            $"[Subsegment] crossed opponent plane L{p.LevelIndex} seq{p.Seq} at t={CurrentTotalMs()} ms.");
    }

    private static void Log(string text) => Plugin.Logger.LogInfo("[Subsegment] " + text);
}
