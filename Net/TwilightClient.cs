using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace TwilightCore.Net;

/// <summary>
/// Orchestrates the player-client connection to the TwilightCup backend:
///
///   login (REST) → open WebSocket → heartbeat loop → reconnect on drop.
///
/// It owns a <see cref="WebSocketClient"/> whose receive callbacks fire on a
/// background thread; those are marshalled to the main thread via
/// <see cref="MainThreadDispatcher"/> before <see cref="OnMessage"/> is raised,
/// so subscribers can touch Unity state freely.
///
/// Outbound messages are built by the caller as a dict (strict — see
/// <see cref="JsonCodec"/>) and handed to <see cref="Send"/>.
/// </summary>
public class TwilightClient : MonoBehaviour
{
    // ── Connection lifecycle events (all raised on the MAIN thread) ──
    /// <summary>Raised after auth_ok, carrying the auth_ok payload (seat/session/etc.).</summary>
    public event Action<Dictionary<string, object>> OnAuthenticated;
    /// <summary>Raised when a parsed server message is ready to handle.</summary>
    public event Action<Dictionary<string, object>> OnMessage;
    /// <summary>Raised on disconnect, carrying the reason and whether this
    /// client will try to reconnect automatically.</summary>
    public event Action<string, bool> OnDisconnected;
    /// <summary>Raised when the WS is open but before auth_ok.</summary>
    public event Action OnSocketOpen;

    public bool IsConnected => _ws != null && _ws.State == WebSocketClient.ConnState.Connected;
    public bool IsAuthenticated { get; private set; }

    private const int MaxOutboxMessages = 256;

    private WebSocketClient _ws;
    private string _token;
    private bool _intentionalStop;
    private int _backoffSecs;

    // Timer/report messages produced while the socket is down. They are
    // replayed (in order) as soon as the next connection authenticates, so a
    // transient disconnect does not lose level/attempt/terminal reports.
    // Server-side upserts are idempotent by level/attempt index.
    private readonly List<Dictionary<string, object>> _outbox = new List<Dictionary<string, object>>();

    // Active connection target — set by StartConnect (from the `twi connect <host> [port]`
    // command-line args, NOT the config file) and reused on reconnect. TLS follows the
    // config toggle (Server.UseTLS, default true — the public nginx endpoint is HTTPS).
    private string _host;
    private int _port;
    private bool _useTls;
    private bool _targetSet;

    /// <summary>The active target as "host:port", or null before the first connect.</summary>
    public string ActiveTarget => _targetSet ? $"{_host}:{_port}" + (_useTls ? " (tls)" : "") : null;

    private string Host => _targetSet ? _host : "";
    private int Port => _targetSet ? _port : 0;
    private bool UseTls => _targetSet ? _useTls : TwilightConfig.UseTLS.Value;
    private string Scheme => UseTls ? "https" : "http";
    private string WsScheme => UseTls ? "wss" : "ws";

    private void Awake()
    {
        _ws = new WebSocketClient();
        _ws.OnOpen += Ws_OnOpen;             // bg
        _ws.OnText += Ws_OnText;             // bg
        _ws.OnClosed += Ws_OnClosed;         // bg
        _ws.OnError += Ws_OnError;           // bg
    }

    private void OnDestroy()
    {
        _intentionalStop = true;
        try { _ws?.Dispose(); } catch { }
    }

    /// <summary>
    /// Begin the connect sequence (login → WS). Host/port come from the command
    /// line (not the config file); port defaults to 8443 (the public nginx HTTPS
    /// entrypoint, which proxies /api/ → backend REST and /ws/ → backend WebSocket);
    /// TLS follows the config toggle (default on). The target is reused on reconnect.
    /// Returns false (with a console message) if the arguments are missing/invalid or
    /// Username isn't configured.
    /// </summary>
    public bool StartConnect(string host, int port = 8443)
    {
        host = (host ?? "").Trim();
        if (port <= 0) port = 8443;
        if (string.IsNullOrEmpty(host))
        {
            TwilightLog.Print("twi: usage: twi connect <host> [port]");
            return false;
        }
        if (string.IsNullOrEmpty(TwilightConfig.Username.Value))
        {
            TwilightLog.Print("[Twilight] Account.Username not configured — edit BepInEx/config/TwilightCore.cfg.");
            return false;
        }
        _host = host;
        _port = port;
        _useTls = TwilightConfig.UseTLS.Value;
        _targetSet = true;
        _intentionalStop = false;
        _backoffSecs = TwilightConfig.ReconnectMinBackoffSecs.Value;
        _outbox.Clear(); // a new manual target must never inherit the old target's reports
        StartCoroutine(LoginAndConnect());
        return true;
    }

    public void Stop()
    {
        _intentionalStop = true;
        IsAuthenticated = false;
        _outbox.Clear();
        try { _ws?.Close(); } catch { }
        // WebSocketClient.Close() deliberately does not raise OnClosed, so the
        // terminal-disconnect bookkeeping must be driven explicitly here.
        OnDisconnected?.Invoke("stopped", false);
    }

    /// <summary>
    /// Debug/testing hook (<c>twi disconnect simulate</c>): drop the transport
    /// WITHOUT setting the intentional-stop flag, so the normal unexpected-
    /// disconnect path (OnDisconnected → backoff reconnect) runs.
    /// </summary>
    public void SimulateDisconnect()
    {
        if (_ws == null || _ws.State != WebSocketClient.ConnState.Connected)
        {
            TwilightLog.Print("[Twilight] simulate disconnect: not connected.");
            return;
        }
        Plugin.Logger.LogInfo("[Twilight] simulating unexpected WebSocket drop (auto-reconnect remains enabled).");
        TwilightLog.Print("[Twilight] Simulating unexpected disconnect — automatic reconnect will follow.");
        _ws.SimulateUnexpectedDrop();
    }

    // ── Login + connect ─────────────────────────────────────────────

    private IEnumerator LoginAndConnect()
    {
        TwilightLog.Print($"[Twilight] Logging in as '{TwilightConfig.Username.Value}' …");
        string token = null;
        string error = null;
        yield return LoginClient.Login(
            Scheme, Host, Port,
            TwilightConfig.Username.Value, TwilightConfig.Password.Value,
            t => token = t,
            e => error = e);

        if (!string.IsNullOrEmpty(error))
        {
            Plugin.Logger.LogError("[Twilight] login failed: " + error);
            TwilightLog.Print("[Twilight] Login failed: " + error);
            ScheduleReconnect();
            yield break;
        }

        _token = token;
        TwilightLog.Print("[Twilight] Login OK, opening WebSocket …");
        OpenSocket();
    }

    private void OpenSocket()
    {
        if (IsConnected) return;
        string seat = TwilightConfig.Seat.Value;
        seat = seat == null ? "" : seat.Trim();
        string path = "/ws/" + _token;
        // Query params: explicit seat override (optional) + the preload capability
        // flag (需求-合集提前下发与预载门控.md R3.3): servers that implement the
        // preload start gate only wait for reports from seats declaring `preload1`;
        // older servers ignore unknown query params entirely.
        path += "?cap=preload1";
        if (!string.IsNullOrEmpty(seat))
            path += "&seat=" + seat;
        TwilightLog.Print($"[Twilight] Connecting {WsScheme}://{Host}:{Port}{path}");
        _ws.Connect(Host, Port, UseTls, path);
    }

    // ── WebSocket events (background thread) ────────────────────────

    private void Ws_OnOpen()
    {
        MainThreadDispatcher.Enqueue(() =>
        {
            OnSocketOpen?.Invoke();
            StartCoroutine(HeartbeatLoop());
        });
    }

    private void Ws_OnText(string json)
    {
        var msg = JsonCodec.Parse(json);
        if (msg == null) return;
        if (TwilightConfig.VerboseNetLog.Value)
            Plugin.Logger.LogInfo("[WS←] " + msg.GetString("type") + ": " + (json.Length > 400 ? json.Substring(0, 400) + "…" : json));
        // auth_ok is special — flips IsAuthenticated.
        if (msg.GetString("type") == Msg.AuthOk)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                IsAuthenticated = true;
                // A fully authenticated connection means the reconnect SUCCEEDED:
                // reset the backoff so the next independent outage starts from the
                // minimum again instead of escalating across separate drops.
                _backoffSecs = TwilightConfig.ReconnectMinBackoffSecs.Value;
                string seat = msg.GetString("seat");
                // Server contract (SrvAuthOk) sends match_id / match_name, not session_*.
                string match = msg.GetString("match_name");
                if (string.IsNullOrEmpty(match)) match = msg.GetString("match_id");
                TwilightLog.Print($"[Twilight] Connected: {msg.GetString("display_name")} ({seat}) — {match}");
                Plugin.Logger.LogInfo($"[Twilight] authenticated as {seat} match={msg.GetString("match_id")}");
                // Replay reports buffered during the outage BEFORE MatchController
                // asks for reconnect_resync: the snapshot then already contains
                // the backfilled progress.
                FlushOutbox();
                OnAuthenticated?.Invoke(msg);
            });
            return;
        }
        if (msg.GetString("type") == Msg.AuthError)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                Plugin.Logger.LogError("[Twilight] auth_error: " + msg.GetString("msg"));
                TwilightLog.Print("[Twilight] Auth failed: " + msg.GetString("msg"));
                // auth failed (bad token / not assigned) — stop reconnecting to avoid a loop.
                _intentionalStop = true;
                _outbox.Clear();
                OnDisconnected?.Invoke("auth_error: " + msg.GetString("msg"), false);
            });
            return;
        }
        MainThreadDispatcher.Enqueue(() => OnMessage?.Invoke(msg));
    }

    private void Ws_OnClosed(string reason)
    {
        MainThreadDispatcher.Enqueue(() =>
        {
            bool wasAuth = IsAuthenticated;
            IsAuthenticated = false;
            bool willReconnect = !_intentionalStop;
            Plugin.Logger.LogWarning("[Twilight] socket closed" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason));
            TwilightLog.Print("[Twilight] Disconnected" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason));
            OnDisconnected?.Invoke(reason, willReconnect);
            if (wasAuth) ScheduleReconnect();
            else if (!_intentionalStop) ScheduleReconnect();
        });
    }

    private void Ws_OnError(System.Exception ex)
    {
        MainThreadDispatcher.Enqueue(() =>
            Plugin.Logger.LogWarning("[Twilight] socket error: " + ex.Message));
    }

    // ── Heartbeat ───────────────────────────────────────────────────

    private IEnumerator HeartbeatLoop()
    {
        int secs = Mathf.Max(2, TwilightConfig.HeartbeatSecs.Value);
        var wait = new WaitForSeconds(secs);
        while (IsConnected)
        {
            Send(new Dictionary<string, object> { { "type", Msg.Heartbeat } });
            yield return wait;
        }
    }

    // ── Reconnect ───────────────────────────────────────────────────

    private void ScheduleReconnect()
    {
        if (_intentionalStop) return;
        int delay = _backoffSecs;
        _backoffSecs = Mathf.Min(_backoffSecs * 2, Mathf.Max(1, TwilightConfig.ReconnectMaxBackoffSecs.Value));
        Plugin.Logger.LogInfo($"[Twilight] reconnecting in {delay}s …");
        TwilightLog.Print($"[Twilight] Reconnecting in {delay}s …");
        StartCoroutine(ReconnectAfter(delay));
    }

    private IEnumerator ReconnectAfter(int delaySecs)
    {
        yield return new WaitForSeconds(delaySecs);
        if (_intentionalStop) yield break;
        // Re-login (token may have expired) then reconnect.
        yield return LoginAndConnect();
    }

    // ── Outbound ────────────────────────────────────────────────────

    /// <summary>Serialize (strict) and send a client→server message. While the
    /// socket is down, replayable match/timer reports are buffered and flushed
    /// after the next auth_ok; anything else is dropped.</summary>
    public void Send(Dictionary<string, object> msg)
    {
        if (msg == null) return;
        string type = msg.GetString("type");
        if (TwilightConfig.VerboseNetLog.Value)
            Plugin.Logger.LogInfo("[WS→] " + type);
        if (!IsConnected)
        {
            if (!_intentionalStop && IsReplayable(type))
            {
                if (_outbox.Count >= MaxOutboxMessages)
                {
                    Plugin.Logger.LogWarning($"[Twilight] outbox full — dropping queued {type}.");
                    return;
                }
                _outbox.Add(msg);
                Plugin.Logger.LogInfo($"[Twilight] queued {type} while disconnected ({_outbox.Count}/{MaxOutboxMessages}).");
            }
            return;
        }
        _ws.Send(JsonCodec.Serialize(msg));
    }

    /// <summary>
    /// Reports that stay meaningful after a reconnect and are safe to replay:
    /// the server upserts by level/attempt index, and terminal messages are
    /// idempotent for the same round.
    /// </summary>
    private static bool IsReplayable(string type)
    {
        return type == Msg.LevelTimeUpload
            || type == Msg.AttemptSkip
            || type == Msg.ProjectComplete
            || type == Msg.ForfeitSignal;
    }

    private void FlushOutbox()
    {
        if (_outbox.Count == 0) return;
        if (_ws == null || !IsConnected)
        {
            Plugin.Logger.LogWarning($"[Twilight] cannot flush {_outbox.Count} queued message(s) — socket not connected.");
            return;
        }
        Plugin.Logger.LogInfo($"[Twilight] flushing {_outbox.Count} queued message(s).");
        foreach (var msg in _outbox)
            _ws.Send(JsonCodec.Serialize(msg));
        _outbox.Clear();
    }
}
