using System.Collections.Generic;
using TwilightCore.Chat;
using TwilightCore.LeaderboardInternal;
using TwilightCore.Net;
using TwilightCore.Timer;

namespace TwilightCore.Match;

/// <summary>
/// The brain of the player client: dispatches inbound server messages to the
/// chat view / collection engine / round reporter, and builds outbound messages
/// (chat, reconnect_resync, …). Created once by the plugin and wired to a
/// <see cref="TwilightClient"/>.
///
/// The round reporter is resolved lazily per use: a registered
/// <see cref="TwilightCore.Timer.ITimerProvider"/> (real timer plugin) wins;
/// without one the fallback (simulated timer / no-op) is used. Lazy resolution
/// matters because the provider registers AFTER our Awake — the BepInEx
/// dependency chain loads TwilightCore first, so <c>TimerProviderRegistry.Current</c>
/// is still null at construction time (ITimerProvider接口需求.md §1.2).
/// </summary>
internal sealed class MatchController
{
    private readonly TwilightClient _client;
    private readonly MatchSession _session;
    private readonly IChatView _chat;
    private readonly IRoundReporter _fallbackReporter;
    private ProviderRoundReporter _providerReporter;

    public MatchController(TwilightClient client, MatchSession session, IChatView chat, IRoundReporter fallbackReporter)
    {
        _client = client;
        _session = session;
        _chat = chat;
        _fallbackReporter = fallbackReporter;
        _client.OnAuthenticated += OnAuthenticated;
        _client.OnMessage += Handle;
        _client.OnDisconnected += OnDisconnected;
    }

    /// <summary>
    /// The reporter for the current moment: a real timer provider if one has
    /// registered (re-created if the registration changed), else the fallback.
    /// </summary>
    private IRoundReporter Reporter
    {
        get
        {
            var p = TimerProviderRegistry.Current;
            if (p != null)
            {
                if (_providerReporter == null || _providerReporter.Provider != p)
                    _providerReporter = new ProviderRoundReporter(_client, p);
                return _providerReporter;
            }
            return _fallbackReporter;
        }
    }

    /// <summary>
    /// Align the provider's match mode with the current session state
    /// (比赛模式锁定时机需求 §2): lock iff ready-locked-or-beyond —
    /// (PREP &amp; MyReady) / COUNTDOWN / IN_ROUND, mirroring ReadyLockActive
    /// extended through the round itself. PREP before <c>!ready</c> stays free
    /// so the player can practise with their own setup.
    /// </summary>
    private void UpdateMatchModeFromSession()
    {
        var p = _session.Phase;
        bool locked = _session.MyReady && p == MatchPhase.Prep
                      || p == MatchPhase.Countdown
                      || p == MatchPhase.InRound;
        SetMatchMode(locked);
    }

    /// <summary>
    /// Drive the provider's match mode (HSRTimer适配需求 T2/T5.1: tag pushes and
    /// match-behaviour constraints are only honoured in match mode). Entered when
    /// this player authenticates into a match seat, exited at match end or
    /// disconnect — outside a match the player's local setup is untouched (T2.6).
    /// </summary>
    private void SetMatchMode(bool on)
    {
        var p = TimerProviderRegistry.Current;
        if (p == null) return;
        try
        {
            if (on && !p.InMatchMode) p.EnterMatchMode();
            else if (!on && p.InMatchMode) p.ExitMatchMode();
        }
        catch (System.Exception ex)
        {
            Plugin.Logger.LogWarning($"[Twilight] match-mode toggle failed: {ex.Message}");
        }
    }

    // ── Outbound helpers ───────────────────────────────────────────

    /// <summary>Send a chat line (the server relays it and parses any ! command).</summary>
    public void SendChat(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // !finish — 选手主动「结束本回合」：本地消费，不上行。SINGLE 语义是
        // 放弃剩余尝试按现有成绩计分（project_complete 只能由此或最后一次
        // 尝试结束触发，twilightcore-single-attempt-complete.md §3/§4）。
        if (text.Trim() == "!finish")
        {
            FinishRound();
            return;
        }

        _client.Send(new Dictionary<string, object> { { "type", Msg.Chat }, { "text", text } });
    }

    /// <summary>
    /// 选手主动结束当前回合：上报 project_complete（SINGLE 剩余尝试服务端记
    /// N/A）并停止本地跟踪。非 IN_ROUND 时提示拒绝。
    /// </summary>
    public void FinishRound()
    {
        if (_session.Phase != MatchPhase.InRound || !Reporter.IsActive)
        {
            _chat.ShowInfo("[System] No active round to finish.");
            return;
        }
        _chat.ShowInfo("[System] Finishing round — remaining attempts (if any) count as N/A.");
        Reporter.FinishRound();
        // Reporter is stopped now, so aborting the local collection run fires
        // RunAborted into an inactive reporter (no forfeit) and returns the
        // player to the menu instead of launching the next (dead) attempt.
        var mgr = CollectionManager.Instance;
        if (mgr != null && mgr.IsInCollectionRun)
            mgr.AbortCollectionRun();
    }

    // ── Connection lifecycle ───────────────────────────────────────

    private void OnAuthenticated(Dictionary<string, object> authOk)
    {
        _session.IsAuthenticated = true;
        _session.AccountId = authOk.GetString("account_id");
        _session.DisplayName = authOk.GetString("display_name");
        _session.Seat = authOk.GetString("seat");
        // Server contract (SrvAuthOk) sends match_id / match_name, not session_*.
        _session.SessionId = authOk.GetString("match_id");
        _session.SessionName = authOk.GetString("match_name");
        LeaderboardTracker.OnAuthenticated(authOk); // both seats' display names + local seat
        _chat.ShowInfo($"[System] Connected: {_session.DisplayName} ({_session.Seat}) — {_session.SessionName ?? "(unnamed match)"}");

        // Align match mode with the (possibly mid-round) session state we're
        // reconnecting into (比赛模式锁定时机需求 §2.3): IN_ROUND → immediately
        // locked; PREP and not ready → free to practise.
        UpdateMatchModeFromSession();

        // Reconnect mid-round: a transient network drop keeps the reporter
        // running locally (see OnDisconnected), so the timer data collected
        // while offline is still intact. If some path did stop it (e.g. an
        // auth_error after a previous reconnect attempt), re-activate it
        // without resetting its completed segments.
        if (_session.Phase == MatchPhase.InRound && !string.IsNullOrEmpty(_session.RoundId))
        {
            if (!Reporter.IsActive && _session.Pick != null)
            {
                Reporter.ResumeRound(_session.RoundId, _session.Pick);
                Plugin.Logger.LogInfo($"[Twilight] resumed round reporter for {_session.RoundId} after reconnect.");
            }

            // Ask the server for the authoritative snapshot (own + opponent);
            // buffered reports were flushed by TwilightClient before this
            // callback, so the snapshot already reflects the backfill.
            _client.Send(new Dictionary<string, object>
            {
                { "type", Msg.ReconnectResync },
                { "round_id", _session.RoundId },
            });
        }
    }

    private void OnDisconnected(string reason, bool willReconnect)
    {
        _session.IsAuthenticated = false;

        // A transient disconnect is NOT a round end: keep the reporter active
        // and stay in match mode so timing continues and in-round constraints
        // (no reset/retry keys) remain enforced while the socket is down.
        // Terminal disconnects (auth_error / explicit Stop / match end) still
        // stop the timer and restore the player's local setup.
        if (!willReconnect)
        {
            Reporter.Stop();
            SetMatchMode(false); // out of the match — player's local setup restored (T2.4)
        }

        if (!string.IsNullOrEmpty(reason))
            _chat.ShowInfo("[System] Disconnected: " + reason + (willReconnect ? " (will reconnect automatically)" : ""));
    }

    // ── Inbound dispatch ───────────────────────────────────────────

    public void Handle(Dictionary<string, object> msg)
    {
        if (msg == null) return;
        switch (msg.GetString("type"))
        {
            case Msg.Chat:
                _chat.DisplayChat(msg.GetString("sender_name"), msg.GetString("seat"), msg.GetString("text"));
                break;
            case Msg.System:
                // sender = display prefix: "Twilight" for match-wide broadcasts
                // (absent on older servers — default matches the broadcast rule).
                _chat.DisplaySystem(
                    msg.GetString("text"), msg.GetString("kind", "info"),
                    msg.GetString("sender", "Twilight"));
                break;
            case Msg.ReadyState:
                _session.AReady = msg.GetBool("a_ready");
                _session.BReady = msg.GetBool("b_ready");
                // PREP-side !ready / cancel-ready flips the lock (§2.1).
                UpdateMatchModeFromSession();
                break;
            case Msg.PhaseChange:
                HandlePhase(msg);
                break;
            case Msg.CountdownTick:
                // The server also emits a system message with the number — no extra UI needed.
                break;
            case Msg.RoundStart:
                HandleRoundStart(msg);
                break;
            case Msg.PlayerStatus:
                LeaderboardTracker.OnPlayerStatus(msg);
                break;
            case Msg.LevelTimeUpdate:
                LeaderboardTracker.OnLevelTimeUpdate(msg);
                break;
            case Msg.MatchEnd:
                // The server's match.ended system message already covers this.
                Reporter.Stop();
                LeaderboardTracker.Clear();
                SetMatchMode(false); // match over — restore the player's local setup (T2.4)
                break;
            case Msg.CountdownAbort:
            case Msg.RoundStartedBroadcast:
            case Msg.RoundResult:
            case Msg.CumulativeScore:
            case Msg.CounterState:
            case Msg.CounterAlert:
            case Msg.DraftState:
            case Msg.VerdictEdit:
                // The server already emits human-readable system messages for these;
                // no local echo, so Twilight lines stay verbatim identical on every client.
                break;
            case Msg.Error:
                Plugin.Logger.LogWarning($"[Twilight] server error {msg.GetInt("code")}: {msg.GetString("msg")}");
                // Targeted reply — only this client sees it → System prefix.
                _chat.DisplaySystem($"Error {msg.GetInt("code")}: {msg.GetString("msg")}", "error", "System");
                break;
            default:
                Plugin.Logger.LogInfo("[Twilight] unhandled message type: " + msg.GetString("type"));
                break;
        }
    }

    private void HandlePhase(Dictionary<string, object> msg)
    {
        var prev = _session.Phase;
        _session.Phase = (MatchPhase)msg.GetInt("phase", (int)MatchPhase.Idle);
        string rid = msg.GetString("round_id");
        if (!string.IsNullOrEmpty(rid)) _session.RoundId = rid;

        Plugin.Logger.LogInfo($"[Twilight] phase {prev} → {_session.Phase}" + (string.IsNullOrEmpty(rid) ? "" : $" round={rid}"));

        if (_session.Phase != MatchPhase.InRound && prev == MatchPhase.InRound)
        {
            Reporter.Stop();
            LeaderboardTracker.Clear(); // round over — GetSnapshot() returns null from here
        }
        // Reporter StartRound is driven by round_start (which carries pick + collection).

        // Every phase transition re-aligns the lock (§2.2): PREP→COUNTDOWN→
        // IN_ROUND lock; ROUND_JUDGING/ROUND_END/MATCH_END/→PREP unlock.
        UpdateMatchModeFromSession();
        // No chat line on →PREP: live transitions get the server's prep.started
        // broadcast (Twilight), and reconnects into PREP get a targeted
        // sender=System hint in the handshake (both arrive as Msg.System).
    }

    private void HandleRoundStart(Dictionary<string, object> msg)
    {
        _session.RoundId = msg.GetString("round_id");
        var pickDict = msg.GetDict("pick");
        var colDict = msg.GetDict("collection");
        _session.Pick = PickSnapshot.From(pickDict);
        Plugin.Logger.LogInfo(
            $"[Twilight] round_start: {_session.Pick.Code} - {_session.Pick.Name} type={_session.Pick.Type} retry={_session.Pick.RetryCount}");
        // No chat line here: the server's round.started system message already
        // announces the pick match-wide.

        // The server sends round_start BEFORE the phase_change→IN_ROUND, so flip the
        // phase ourselves: this also releases the false-start lock for our own launch.
        _session.Phase = MatchPhase.InRound;
        UpdateMatchModeFromSession(); // align immediately; the later phase_change re-aligns idempotently

        // StartRound BEFORE ingesting the collection: the provider's full reset
        // must happen before the first level's PlayingLevel edge, or the first
        // segment's timing start races the load (ITimerProvider接口需求.md §2.2).
        Reporter.StartRound(_session.RoundId, _session.Pick);

        if (!RoundIngestion.TryStart(pickDict, colDict, out string err))
        {
            Plugin.Logger.LogError("[Twilight] failed to start server collection: " + err);
            _chat.DisplaySystem("Failed to load collection: " + err, "error", "System");
            Reporter.Stop();
            LeaderboardTracker.Clear(); // the round never really started here
            return;
        }

        // Leaderboard snapshot is created here — the first PlayingLevel edge
        // (and the first player_status) may follow immediately.
        LeaderboardTracker.OnRoundStart(_session.RoundId, pickDict, RoundIngestion.LastResolvedLevels);
    }
}
