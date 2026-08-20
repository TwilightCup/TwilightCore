using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TwilightCore.Net;

/// <summary>
/// Minimal RFC 6455 WebSocket client (text frames only) for the Unity 2017 Mono
/// runtime, which has no functional <c>ClientWebSocket</c> / <c>System.Net.Http</c>.
///
/// - <see cref="Connect"/> starts a background thread: TCP connect → optional TLS →
///   HTTP upgrade handshake → frame receive loop.
/// - <see cref="Send"/> is thread-safe (serialised by a lock) and masks payloads
///   (clients MUST mask; the server enforces it).
/// - All events fire on the BACKGROUND THREAD. Callers must marshal to the main
///   thread via <see cref="MainThreadDispatcher"/>.
/// - <see cref="OnClosed"/> fires only on UNEXPECTED disconnect (not on intentional
///   <see cref="Close"/>), so the caller can use it to drive reconnect logic.
/// </summary>
internal sealed class WebSocketClient : IDisposable
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static readonly byte[] EmptyBytes = new byte[0];

    public enum ConnState { Disconnected, Connecting, Connected, Closed }

    public ConnState State { get; private set; } = ConnState.Disconnected;

    public event Action OnOpen;             // bg thread
    public event Action<string> OnText;      // bg thread
    public event Action<string> OnClosed;    // bg thread (reason; null when unknown) — unexpected only
    public event Action<Exception> OnError;  // bg thread

    private TcpClient _tcp;
    private Stream _stream;
    private Thread _thread;
    private readonly object _sendLock = new object();
    private readonly Random _rng = new Random();
    private volatile bool _closing;

    /// <summary>Begin connecting asynchronously (returns immediately).</summary>
    public void Connect(string host, int port, bool useTls, string pathAndQuery)
    {
        if (State == ConnState.Connecting || State == ConnState.Connected) return;
        State = ConnState.Connecting;
        _closing = false;
        _thread = new Thread(() => Run(host, port, useTls, pathAndQuery))
        {
            IsBackground = true,
            Name = "TwilightCore-WS"
        };
        _thread.Start();
    }

    /// <summary>Intentional shutdown. Does NOT raise <see cref="OnClosed"/>.</summary>
    public void Close()
    {
        _closing = true;
        if (State == ConnState.Connected)
        {
            try { lock (_sendLock) SendFrame(0x8, EmptyBytes, 0, 0); } catch { /* ignore */ }
        }
        CleanupStreams();
        State = ConnState.Closed;
    }

    /// <summary>
    /// Debug/testing hook (<c>twi disconnect simulate</c>): tear the transport
    /// down WITHOUT setting <c>_closing</c>, so the receive thread's finally
    /// reports it as an unexpected disconnect and <see cref="OnClosed"/> fires.
    /// </summary>
    public void SimulateUnexpectedDrop()
    {
        if (State == ConnState.Disconnected || State == ConnState.Closed) return;
        // Flip state first so TwilightClient.Send() immediately treats the
        // socket as down (queues replayable reports); the receive thread's
        // finally still sees _closing == false and raises OnClosed.
        State = ConnState.Closed;
        Plugin.Logger.LogInfo("[WS] simulated unexpected transport drop.");
        lock (_sendLock) CleanupStreams();
    }

    public void Dispose()
    {
        _closing = true;
        Close();
    }

    // ── Background connect + loop ───────────────────────────────────

    private void Run(string host, int port, bool useTls, string pathAndQuery)
    {
        Exception error = null;
        try
        {
            _tcp = new TcpClient();
            _tcp.Connect(host, port);
            Stream raw = _tcp.GetStream();

            if (useTls)
            {
                // Managed TLS1.2 via BouncyCastle. Unity 2017.4's bundled Mono has a
                // native TLS stack whose cipher suite is too old to handshake with
                // the public server (SslStream fails with "The authentication or
                // decryption has failed"), so the handshake is done in pure managed
                // code with BC's own modern ciphers. Accept-any-cert (organiser-
                // controlled hosts). See Net/BouncyTls.cs.
                Plugin.Logger.LogInfo("[WS] TLS handshake: BouncyCastle managed TLS1.2 (bypassing legacy Unity Mono SslStream).");
                _stream = BouncyTls.Connect(host, raw);
                Plugin.Logger.LogInfo("[WS] TLS established via BouncyCastle.");
            }
            else
            {
                _stream = raw;
            }

            DoHandshake(host, port, pathAndQuery);

            State = ConnState.Connected;
            Plugin.Logger.LogInfo($"[WS] connected to {(useTls ? "wss" : "ws")}://{host}:{port}{pathAndQuery}");
            SafeInvoke(OnOpen);

            ReceiveLoop();
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            bool unexpected = !_closing;
            State = ConnState.Closed;
            CleanupStreams();
            if (error != null && unexpected) SafeInvoke(OnError, error);
            if (unexpected) SafeInvoke(OnClosed, error?.Message);
        }
    }

    // ── Handshake ───────────────────────────────────────────────────

    private void DoHandshake(string host, int port, string pathAndQuery)
    {
        var keyBytes = new byte[16];
        lock (_rng) _rng.NextBytes(keyBytes);
        string key = Convert.ToBase64String(keyBytes);

        var sb = new StringBuilder();
        sb.Append("GET ").Append(pathAndQuery).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append("Sec-WebSocket-Key: ").Append(key).Append("\r\n");
        sb.Append("Sec-WebSocket-Version: 13\r\n");
        sb.Append("\r\n");

        byte[] req = Encoding.ASCII.GetBytes(sb.ToString());
        _stream.Write(req, 0, req.Length);

        string response = ReadHttpResponseHeader();
        string statusLine = response.Length > 0 ? response.Split('\r')[0] : "";
        if (!statusLine.Contains(" 101 "))
            throw new IOException("WebSocket handshake failed: " + statusLine);

        string expectedAccept = ComputeAccept(key);
        // Header is case-insensitive; tolerate either casing.
        if (!response.Contains(expectedAccept))
            throw new IOException("WebSocket handshake: bad Sec-WebSocket-Accept.");
    }

    private static string ComputeAccept(string key)
    {
        using (var sha1 = SHA1.Create())
        {
            byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WsGuid));
            return Convert.ToBase64String(hash);
        }
    }

    private string ReadHttpResponseHeader()
    {
        var sb = new StringBuilder();
        byte[] one = new byte[1];
        byte[] tail = new byte[4];
        int count = 0;
        while (true)
        {
            int n = _stream.Read(one, 0, 1);
            if (n <= 0) throw new IOException("Connection closed during handshake.");
            sb.Append((char)one[0]);
            tail[0] = tail[1]; tail[1] = tail[2]; tail[2] = tail[3]; tail[3] = one[0];
            count++;
            if (count >= 4 && tail[0] == '\r' && tail[1] == '\n' && tail[2] == '\r' && tail[3] == '\n')
                return sb.ToString();
            if (count > 16384) throw new IOException("HTTP response header too large.");
        }
    }

    // ── Receive loop ────────────────────────────────────────────────

    private void ReceiveLoop()
    {
        var frag = new StringBuilder();
        while (!_closing && State == ConnState.Connected)
        {
            int b0 = _stream.ReadByte();
            int b1 = _stream.ReadByte();
            if (b0 < 0 || b1 < 0) return; // remote closed

            bool fin = (b0 & 0x80) != 0;
            int opcode = b0 & 0x0F;
            bool masked = (b1 & 0x80) != 0;
            long len = b1 & 0x7F;
            if (len == 126)
            {
                byte[] e = ReadExact(2);
                len = (e[0] << 8) | e[1];
            }
            else if (len == 127)
            {
                byte[] e = ReadExact(8);
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | e[i];
            }

            byte[] mask = masked ? ReadExact(4) : null;
            byte[] payload = len > 0 ? ReadExact((int)len) : EmptyBytes;
            if (masked)
                for (int i = 0; i < payload.Length; i++)
                    payload[i] ^= mask[i & 3];

            switch (opcode)
            {
                case 0x0: // continuation
                    frag.Append(Encoding.UTF8.GetString(payload));
                    if (fin)
                    {
                        SafeInvoke(OnText, frag.ToString());
                        frag.Clear();
                    }
                    break;
                case 0x1: // text
                    if (fin)
                    {
                        SafeInvoke(OnText, Encoding.UTF8.GetString(payload));
                    }
                    else
                    {
                        frag.Clear();
                        frag.Append(Encoding.UTF8.GetString(payload));
                    }
                    break;
                case 0x2: // binary — ignored (we only handle text)
                    break;
                case 0x9: // ping → pong
                    try { lock (_sendLock) SendFrame(0xA, payload, 0, payload.Length); } catch { }
                    break;
                case 0xA: // pong
                    break;
                case 0x8: // close
                    _closing = true;
                    return;
                default:
                    break;
            }
        }
    }

    private byte[] ReadExact(int count)
    {
        byte[] buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = _stream.Read(buf, read, count - read);
            if (n <= 0) throw new IOException("WebSocket stream closed mid-frame.");
            read += n;
        }
        return buf;
    }

    // ── Send ────────────────────────────────────────────────────────

    public void Send(string text)
    {
        if (State != ConnState.Connected || _stream == null) return;
        byte[] payload = Encoding.UTF8.GetBytes(text);
        lock (_sendLock)
        {
            try { SendFrame(0x1, payload, 0, payload.Length); }
            catch (Exception ex) { SafeInvoke(OnError, ex); }
        }
    }

    /// <summary>Write a single (masked) client→server frame. Caller holds _sendLock.</summary>
    private void SendFrame(int opcode, byte[] payload, int offset, int length)
    {
        byte[] mask = new byte[4];
        lock (_rng) _rng.NextBytes(mask);
        byte[] masked = new byte[length];
        for (int i = 0; i < length; i++)
            masked[i] = (byte)(payload[offset + i] ^ mask[i & 3]);

        using (var ms = new MemoryStream())
        {
            ms.WriteByte((byte)(0x80 | opcode)); // FIN + opcode
            if (length < 126)
            {
                ms.WriteByte((byte)(0x80 | length)); // mask bit + len
            }
            else if (length <= 0xFFFF)
            {
                ms.WriteByte((byte)(0x80 | 126));
                ms.WriteByte((byte)(length >> 8));
                ms.WriteByte((byte)(length));
            }
            else
            {
                ms.WriteByte((byte)(0x80 | 127));
                for (int i = 7; i >= 0; i--)
                    ms.WriteByte((byte)((length >> (8 * i)) & 0xFF));
            }
            ms.Write(mask, 0, 4);
            if (length > 0)
                ms.Write(masked, 0, length);
            byte[] frame = ms.ToArray();
            _stream.Write(frame, 0, frame.Length);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void CleanupStreams()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Close(); } catch { }
        _stream = null;
        _tcp = null;
    }

    private static void SafeInvoke(Action a)
    {
        if (a != null) try { a(); } catch (Exception e) { Plugin.Logger.LogError("[WS] handler threw: " + e); }
    }
    private static void SafeInvoke<T>(Action<T> a, T v)
    {
        if (a != null) try { a(v); } catch (Exception e) { Plugin.Logger.LogError("[WS] handler threw: " + e); }
    }
}
