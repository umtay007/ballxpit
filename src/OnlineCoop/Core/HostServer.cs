using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BALLxPITOnlineCoop.Core;

public sealed class ServerOptions
{
    public int Port = 7777;
    /// <summary>Guests allowed at once: the one playing P2 plus spectators.</summary>
    public int MaxGuests = 4;
    /// <summary>Most video frames a guest may have on the way before we wait for acknowledgements.</summary>
    public int MaxFramesInFlight = 6;
    public string HostName = "Host";
}

/// <summary>What the host's game reports back to the guests once a second.</summary>
public struct HostStatus
{
    public bool PlayerTwoActive;
    public bool AutoShoot;
    public bool AutoShootByCharacter;
    /// <summary>P2's own health, or -1 when P2 shares the host's.</summary>
    public float Health;
    public float MaxHealth;
    /// <summary>Seconds until a knocked-out P2 gets back up; 0 when P2 is up.</summary>
    public float DownedSeconds;
    public string Note;
}

public sealed class GuestInfo
{
    public string Name = "";
    public bool IsPlayer;
    public int PingMs;
    public double KbitPerSecond;
    /// <summary>Frames allowed on the way to this guest right now.</summary>
    public int Window;
    public bool Congested;
    /// <summary>Flow-control internals, for the log: lowest ack delay, estimated throughput, frames acked per second.</summary>
    public string Diagnostics = "";
}

/// <summary>
/// Serves the guest page and the WebSocket the guests stream through. One guest at a time controls
/// P2; everyone else who knows the code can watch. Thread-safe: the game thread reads input and
/// pushes frames, the network runs on the thread pool.
/// </summary>
public sealed class HostServer : IDisposable
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly ServerOptions _options;
    private readonly byte[] _page;
    private readonly object _lock = new();
    private readonly List<Session> _sessions = new();
    private readonly Dictionary<string, (int Count, double Since)> _failedJoins = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Session? _player;
    private GuestInput _playerInput;
    private int _autoShootToggles;
    private HostStatus _status;
    private int _nextSessionId;
    private string _loadout = "";
    private long _pick = -1;

    public HostServer(ServerOptions options, byte[] guestPage)
    {
        _options = options;
        _page = guestPage;
        JoinCode = NewCode();
    }

    public static double Now => Clock.Elapsed.TotalSeconds;

    public int Port { get; private set; }
    public string JoinCode { get; private set; }
    public bool IsRunning => _listener != null;

    public void Start()
    {
        if (_listener != null) return;
        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _ = Task.Run(() => AcceptLoop(listener, token));
        _ = Task.Run(() => HousekeepingLoop(token));
        Log.Info($"Online co-op server listening on port {Port}.");
    }

    public void Stop()
    {
        TcpListener? listener = _listener;
        if (listener == null) return;
        _listener = null;
        try { _cts?.Cancel(); } catch { }
        try { listener.Stop(); } catch { }
        Session[] sessions;
        lock (_lock)
        {
            sessions = _sessions.ToArray();
            _sessions.Clear();
            _player = null;
            _playerInput = default;
        }
        foreach (Session session in sessions) session.Close();
        Log.Info("Online co-op server stopped.");
    }

    public void Dispose() => Stop();

    public void RegenerateCode()
    {
        JoinCode = NewCode();
    }

    private static string NewCode()
    {
        Span<char> code = stackalloc char[6];
        for (int i = 0; i < code.Length; i++) code[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(code);
    }

    // ---- Game-thread API ------------------------------------------------------------------

    /// <summary>The newest input from the guest playing P2, if there is one.</summary>
    public bool TryGetPlayerInput(out GuestInput input)
    {
        lock (_lock)
        {
            input = _playerInput;
            return _player != null;
        }
    }

    public int TakeAutoShootToggles() => Interlocked.Exchange(ref _autoShootToggles, 0);

    public void SetHostStatus(HostStatus status)
    {
        lock (_lock) _status = status;
    }

    /// <summary>P2's balls, passives and level-up choices (a "loadout" message); sent to everyone now and to later joiners.</summary>
    public void SetLoadout(string json)
    {
        lock (_lock)
        {
            if (json == _loadout) return;
            _loadout = json;
            foreach (Session s in _sessions)
                if (s.Joined) s.EnqueueText(json);
        }
    }

    /// <summary>The level-up choice the guest playing P2 clicked, if any since the last call.</summary>
    public bool TakePick(out int offerId, out int index)
    {
        long pick = Interlocked.Exchange(ref _pick, -1);
        offerId = pick < 0 ? 0 : (int)(pick >> 8);
        index = pick < 0 ? 0 : (int)(pick & 0xff);
        return pick >= 0;
    }

    public List<GuestInfo> GetGuests()
    {
        lock (_lock)
        {
            return _sessions.Where(s => s.Joined).Select(s => new GuestInfo
            {
                Name = s.Name,
                IsPlayer = s == _player,
                PingMs = s.PingMs,
                KbitPerSecond = s.KbitPerSecond,
                Window = s.Window,
                Congested = s.Congested,
                Diagnostics = s.Diagnostics,
            }).ToList();
        }
    }

    /// <summary>Someone is playing P2.</summary>
    public bool HasPlayer
    {
        get
        {
            lock (_lock) return _player != null;
        }
    }

    public int GuestCount
    {
        get { lock (_lock) return _sessions.Count(s => s.Joined); }
    }

    /// <summary>True when at least one guest is ready for another video frame.</summary>
    public bool WantsVideoFrame()
    {
        lock (_lock)
        {
            foreach (Session s in _sessions)
            {
                if (s.Joined && s.FramesInFlight < s.Window) return true;
            }
        }
        return false;
    }

    public bool HasGuests
    {
        get { lock (_lock) return _sessions.Any(s => s.Joined); }
    }

    /// <summary>Frame rate the stream aims for; sets how many frames may be on the way per guest.</summary>
    public int TargetFps { get; set; } = 30;

    /// <summary>
    /// True while frames pile up on the way to the guest playing P2 (or, with nobody playing, to any
    /// guest): their connection can't carry this picture quality at this frame rate.
    /// </summary>
    public bool IsCongested()
    {
        lock (_lock)
        {
            if (_player != null) return _player.Congested;
            foreach (Session s in _sessions)
            {
                if (s.Joined && s.Congested) return true;
            }
        }
        return false;
    }

    /// <summary>Queues a finished video packet for every guest that is keeping up.</summary>
    public void SendVideo(uint frameId, byte[] packet)
    {
        double now = Now;
        lock (_lock)
        {
            foreach (Session s in _sessions)
            {
                if (!s.Joined || s.FramesInFlight >= s.Window) continue;
                s.SentAt[frameId] = (now, packet.Length);
                s.Enqueue(new Outgoing(WebSocketOpcode.Binary, packet, OutgoingKind.Video));
            }
        }
    }

    public void SendAudio(byte[] packet)
    {
        lock (_lock)
        {
            foreach (Session s in _sessions)
            {
                if (!s.Joined || !s.WantsAudio) continue;
                if (Volatile.Read(ref s.QueuedAudio) > 12) continue; // this guest is behind; drop rather than add lag
                Interlocked.Increment(ref s.QueuedAudio);
                s.Enqueue(new Outgoing(WebSocketOpcode.Binary, packet, OutgoingKind.Audio));
            }
        }
    }

    public void KickPlayer()
    {
        Session? player;
        lock (_lock) player = _player;
        if (player == null) return;
        player.EnqueueText("{\"t\":\"kicked\"}");
        player.CompleteQueue();
        Log.Info($"Kicked {player.Name}.");
    }

    // ---- Network ----------------------------------------------------------------------------

    private async Task AcceptLoop(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Accept failed: {ex.Message}");
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(() => HandleConnection(client, token));
        }
    }

    private async Task HandleConnection(TcpClient client, CancellationToken serverToken)
    {
        client.NoDelay = true;
        client.SendBufferSize = 256 * 1024;
        NetworkStream stream = client.GetStream();
        string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        try
        {
            HttpRequest? request;
            using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
            {
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                request = await HttpRequest.ReadAsync(stream, headerTimeout.Token).ConfigureAwait(false);
            }
            if (request == null)
            {
                client.Dispose();
                return;
            }
            // Behind cloudflared every connection comes from localhost; the real address is in a header.
            string forwarded = request.Header("CF-Connecting-IP");
            if (forwarded.Length > 0 && IPAddress.IsLoopback(((IPEndPoint)client.Client.RemoteEndPoint!).Address)) remote = forwarded;

            string path = request.Path;
            int query = path.IndexOfAny(new[] { '?', '#' });
            if (query >= 0) path = path.Substring(0, query);

            if (request.Method == "GET" && path == "/ws" && WebSocketConnection.IsUpgradeRequest(request))
            {
                WebSocketConnection ws = await WebSocketConnection.AcceptAsync(stream, request, serverToken).ConfigureAwait(false);
                await RunSession(client, ws, remote, serverToken).ConfigureAwait(false);
                return;
            }

            if ((request.Method == "GET" || request.Method == "HEAD") && (path == "/" || path == "/index.html"))
                await WriteHttp(stream, "200 OK", "text/html; charset=utf-8", _page, request.Method == "HEAD", serverToken).ConfigureAwait(false);
            else if (request.Method == "GET" && path == "/health")
                await WriteHttp(stream, "200 OK", "text/plain", Encoding.ASCII.GetBytes("ok"), false, serverToken).ConfigureAwait(false);
            else
                await WriteHttp(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("not found"), false, serverToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.IO.IOException or SocketException or ObjectDisposedException)
        {
            // Client went away.
        }
        catch (Exception ex)
        {
            Log.Warn($"Connection from {remote} failed: {ex.Message}");
        }
        client.Dispose();
    }

    private static async Task WriteHttp(NetworkStream stream, string status, string contentType, byte[] body, bool headOnly, CancellationToken token)
    {
        string head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token).ConfigureAwait(false);
        if (!headOnly) await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task RunSession(TcpClient client, WebSocketConnection ws, string remote, CancellationToken serverToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var session = new Session(Interlocked.Increment(ref _nextSessionId), client, ws, remote, cts);
        lock (_lock) _sessions.Add(session);
        Task sendLoop = Task.Run(() => session.SendLoop());
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var message = await ws.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (message == null) break;
                session.LastSeenAt = Now;
                if (message.Value.Opcode != WebSocketOpcode.Text) continue;
                if (!HandleMessage(session, message.Value.Payload)) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.IO.IOException or SocketException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Guest session {session.Name} ended: {ex.Message}");
        }
        finally
        {
            bool wasPlayer;
            lock (_lock)
            {
                _sessions.Remove(session);
                wasPlayer = _player == session;
                if (wasPlayer)
                {
                    _player = null;
                    _playerInput = default;
                }
            }
            // Let queued messages (an error, "kicked") go out before the socket closes.
            session.CompleteQueue();
            try { await Task.WhenAny(sendLoop, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false); } catch { }
            session.Close();
            if (session.Joined) Log.Info($"{session.Name} left{(wasPlayer ? " (P2 is free again)" : "")}.");
        }
    }

    /// <returns>False to drop the connection.</returns>
    private bool HandleMessage(Session session, byte[] payload)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return true;
        }
        using (doc)
            return HandleMessage(session, doc);
    }

    private bool HandleMessage(Session session, JsonDocument doc)
    {
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out JsonElement typeElement)) return true;
        string type = typeElement.GetString() ?? "";

        if (!session.Joined)
            return type == "hello" && HandleHello(session, root);

        switch (type)
        {
            case "in":
                lock (_lock)
                {
                    if (_player != session) break;
                    _playerInput = new GuestInput
                    {
                        MoveX = Clamp(GetFloat(root, "mx"), -1, 1),
                        MoveY = Clamp(GetFloat(root, "my"), -1, 1),
                        AimMode = (GuestAimMode)Math.Clamp((int)GetFloat(root, "am"), 0, 2),
                        AimX = Clamp(GetFloat(root, "ax"), -1, 2),
                        AimY = Clamp(GetFloat(root, "ay"), -1, 2),
                        Shoot = GetFloat(root, "sh") > 0.5f,
                        ReceivedAt = Now,
                    };
                }
                break;
            case "ack":
                uint frameId = (uint)Math.Clamp(GetFloat(root, "f"), 0, uint.MaxValue);
                lock (_lock)
                {
                    if (session.SentAt.Remove(frameId, out var sent))
                        session.OnFrameAcknowledged(sent.At, sent.Bytes, Now, TargetFps, _options.MaxFramesInFlight);
                }
                break;
            case "tog":
                lock (_lock)
                {
                    if (_player == session) Interlocked.Increment(ref _autoShootToggles);
                }
                break;
            case "ping":
                session.PingMs = (int)Clamp(GetFloat(root, "rtt"), 0, 99999);
                string echo = root.TryGetProperty("c", out JsonElement c) ? c.GetRawText() : "0";
                session.EnqueueText("{\"t\":\"pong\",\"c\":" + echo + "}");
                break;
            case "claim":
                lock (_lock)
                {
                    if (_player == null)
                    {
                        _player = session;
                        _playerInput = default;
                        session.EnqueueText("{\"t\":\"role\",\"role\":\"player\"}");
                        Log.Info($"{session.Name} took control of P2.");
                    }
                }
                break;
            case "audio":
                session.WantsAudio = GetFloat(root, "on") > 0.5f;
                break;
            case "pick":
                lock (_lock)
                {
                    if (_player != session) break;
                    long offer = (long)Clamp(GetFloat(root, "o"), 0, int.MaxValue);
                    long index = (long)Clamp(GetFloat(root, "i"), 0, 16);
                    Interlocked.Exchange(ref _pick, (offer << 8) | index);
                }
                break;
        }
        return true;
    }

    private bool HandleHello(Session session, JsonElement root)
    {
        string code = root.TryGetProperty("code", out JsonElement c) ? (c.GetString() ?? "") : "";
        double now = Now;
        lock (_lock)
        {
            if (_failedJoins.TryGetValue(session.Remote, out var failures) && failures.Count >= 8 && now - failures.Since < 60)
            {
                session.EnqueueText("{\"t\":\"error\",\"msg\":\"Too many wrong codes. Wait a minute and try again.\"}");
                return false;
            }
        }
        if (!CodesMatch(code, JoinCode))
        {
            lock (_lock)
            {
                _failedJoins.TryGetValue(session.Remote, out var f);
                _failedJoins[session.Remote] = now - f.Since > 60 ? (1, now) : (f.Count + 1, f.Count == 0 ? now : f.Since);
            }
            session.EnqueueText("{\"t\":\"error\",\"msg\":\"That join code is wrong. Ask the host for the current link.\"}");
            Log.Info($"Rejected a join from {session.Remote}: wrong code.");
            return false;
        }

        session.Name = CleanName(root.TryGetProperty("name", out JsonElement n) ? n.GetString() : null);
        string clientId = root.TryGetProperty("id", out JsonElement idElement) ? (idElement.GetString() ?? "") : "";
        bool wantsPlayer = !root.TryGetProperty("want", out JsonElement want) || want.GetString() != "watch";
        session.ClientId = clientId.Length > 64 ? clientId.Substring(0, 64) : clientId;

        Session? replaced = null;
        string role;
        lock (_lock)
        {
            // A reload of the same browser tab replaces its old connection instead of waiting for it to time out.
            if (session.ClientId.Length > 0)
                replaced = _sessions.FirstOrDefault(s => s != session && s.Joined && s.ClientId == session.ClientId);
            int others = _sessions.Count(s => s.Joined && s != replaced);
            if (others >= _options.MaxGuests)
            {
                session.EnqueueText("{\"t\":\"error\",\"msg\":\"The session is full.\"}");
                return false;
            }
            if (replaced != null && _player == replaced)
            {
                _player = null;
                _playerInput = default;
            }
            if (wantsPlayer && _player == null)
                _player = session;
            role = _player == session ? "player" : "spectator";
            session.Joined = true;
        }
        replaced?.Close();

        session.EnqueueText("{\"t\":\"welcome\",\"role\":\"" + role + "\",\"host\":" + JsonString(_options.HostName) + "}");
        string loadout;
        lock (_lock) loadout = _loadout;
        if (loadout.Length > 0) session.EnqueueText(loadout);
        Log.Info($"{session.Name} joined from {session.Remote} as {(role == "player" ? "P2" : "a spectator")}.");
        return true;
    }

    private async Task HousekeepingLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            double now = Now;
            List<Session> stale = new();
            string status;
            lock (_lock)
            {
                string playerName = _player?.Name ?? "";
                status = "{\"t\":\"status\",\"p2\":" + Bool(_status.PlayerTwoActive) + ",\"auto\":" + Bool(_status.AutoShoot)
                    + ",\"autoChar\":" + Bool(_status.AutoShootByCharacter)
                    + ",\"hp\":" + Num(_status.Health) + ",\"hpMax\":" + Num(_status.MaxHealth) + ",\"down\":" + Num(_status.DownedSeconds)
                    + ",\"player\":" + JsonString(playerName) + ",\"guests\":" + _sessions.Count(s => s.Joined).ToString(CultureInfo.InvariantCulture)
                    + ",\"note\":" + JsonString(_status.Note ?? "") + "}";
                foreach (Session s in _sessions)
                {
                    s.UpdateRate(now);
                    if (now - s.LastSeenAt > 20) stale.Add(s);
                    // A backgrounded browser tab stops acknowledging; don't let it stall forever.
                    s.ForgetFramesOlderThan(now - 3);
                    if (s.Joined) s.EnqueueText(status);
                }
                foreach (var key in _failedJoins.Where(kv => now - kv.Value.Since > 120).Select(kv => kv.Key).ToList())
                    _failedJoins.Remove(key);
            }
            foreach (Session s in stale) s.Close();
        }
    }

    private static bool CodesMatch(string given, string expected)
    {
        byte[] a = Encoding.UTF8.GetBytes(given.Trim().ToUpperInvariant());
        byte[] b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string CleanName(string? name)
    {
        var sb = new StringBuilder();
        foreach (char ch in (name ?? "").Trim())
        {
            if (sb.Length >= 20) break;
            if (!char.IsControl(ch)) sb.Append(ch);
        }
        return sb.Length > 0 ? sb.ToString() : "Guest";
    }

    private static float GetFloat(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement e)) return 0;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double d) && double.IsFinite(d)) return (float)d;
        if (e.ValueKind == JsonValueKind.True) return 1;
        return 0;
    }

    private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

    private static string Bool(bool b) => b ? "true" : "false";

    private static string Num(float v) => float.IsFinite(v) ? Math.Round(v, 1).ToString(CultureInfo.InvariantCulture) : "0";

    internal static string JsonString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (ch < 0x20 || ch == '<' || ch == '>' || ch == '&') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private enum OutgoingKind
    {
        Control,
        Video,
        Audio,
    }

    private readonly struct Outgoing
    {
        public readonly WebSocketOpcode Opcode;
        public readonly byte[] Data;
        public readonly OutgoingKind Kind;

        public Outgoing(WebSocketOpcode opcode, byte[] data, OutgoingKind kind)
        {
            Opcode = opcode;
            Data = data;
            Kind = kind;
        }
    }

    private sealed class Session
    {
        public readonly int Id;
        public readonly string Remote;
        public readonly TcpClient Client;
        public readonly WebSocketConnection Ws;
        public readonly CancellationTokenSource Cts;
        private readonly Channel<Outgoing> _queue = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
        public string Name = "Guest";
        public string ClientId = "";
        public bool Joined;
        public bool WantsAudio = true;
        /// <summary>When each unacknowledged video frame was queued and its size, by frame id.</summary>
        public readonly Dictionary<uint, (double At, int Bytes)> SentAt = new();
        public int FramesInFlight => SentAt.Count;
        public int Window = 2;
        public bool Congested => Now < _congestedUntil;
        public string Diagnostics = "";
        private double _minDelay = double.MaxValue, _previousMinDelay = double.MaxValue, _minDelaySince = Now;
        private double _averageDelay;
        private double _lastIncrease, _lastDecrease, _congestedUntil, _noIncreaseUntil;
        private double _fpsBeforeIncrease = -1;
        private readonly Queue<(double At, long Bytes)> _ackHistory = new();
        private long _bytesAcked;

        /// <summary>
        /// Flow control from acknowledgement delays: a frame's delay runs from being queued here to its
        /// ack, so it covers sending, the network round trip and the browser's decode. The lowest
        /// delay seen lately is the cost with nothing queued; anything above it is frames waiting
        /// behind each other because the connection is full.
        ///   * no queueing and below the target frame rate: allow one more frame on the way (at most
        ///     once a second), because the round trip rather than the connection is the limit;
        ///   * queueing: allow one fewer and report congestion, so the picture gets smaller.
        /// This keeps the frame rate up on high-latency links without piling frames (and lag) up on
        /// slow ones.
        /// </summary>
        public void OnFrameAcknowledged(double sentAt, int bytes, double now, int targetFps, int maxWindow)
        {
            double delay = now - sentAt;
            if (now - _minDelaySince > 5)
            {
                _previousMinDelay = _minDelay;
                _minDelay = double.MaxValue;
                _minDelaySince = now;
            }
            if (delay < _minDelay) _minDelay = delay;
            double minDelay = Math.Min(_minDelay, _previousMinDelay);
            _averageDelay = _averageDelay <= 0 ? delay : _averageDelay * 0.75 + delay * 0.25;

            _bytesAcked += bytes;
            _ackHistory.Enqueue((now, _bytesAcked));
            while (_ackHistory.Count > 2 && now - _ackHistory.Peek().At > 1) _ackHistory.Dequeue();
            (double firstAt, long firstBytes) = _ackHistory.Peek();
            double span = now - firstAt;
            double fps = span > 0.2 ? (_ackHistory.Count - 1) / span : targetFps;
            double mbit = span > 0.2 ? (_bytesAcked - firstBytes) * 8 / 1e6 / span : 0;

            double queueing = _averageDelay - minDelay;
            if (queueing > Math.Max(0.02, minDelay * 0.3))
            {
                if (now - _lastDecrease > 0.3)
                {
                    Window = Math.Max(2, Window - 1);
                    _lastDecrease = now;
                }
                _congestedUntil = now + 1.5;
                _fpsBeforeIncrease = -1;
            }
            else if (_fpsBeforeIncrease >= 0 && now - _lastIncrease > 2)
            {
                // Judge the last extra frame: keep it only if it bought frame rate. If not, the
                // connection itself is the limit; hold off probing for a while.
                if (fps < _fpsBeforeIncrease * 1.1)
                {
                    Window = Math.Max(2, Window - 1);
                    _noIncreaseUntil = now + 10;
                }
                _fpsBeforeIncrease = -1;
            }
            else if (fps < targetFps * 0.9 && now > _noIncreaseUntil && now - _lastIncrease > 1 && now - _lastDecrease > 2 && Window < maxWindow)
            {
                _fpsBeforeIncrease = fps;
                Window++;
                _lastIncrease = now;
            }
            // Below the target with no room to grow: the connection is what's holding the frame rate
            // back, so smaller frames are the only way to more of them.
            if (fps < targetFps * 0.75 && now < _noIncreaseUntil) _congestedUntil = Math.Max(_congestedUntil, now + 1.5);
            AchievedFps = fps;
            Diagnostics = FormattableString.Invariant(
                $"minDelay={minDelay * 1000:0}ms avgDelay={_averageDelay * 1000:0}ms fps={fps:0.0} {mbit:0.0}Mbit");
        }

        public double AchievedFps;

        public double LastSeenAt = Now;
        public int QueuedAudio;
        public int PingMs;
        public double KbitPerSecond;
        private long _bytesSent;
        private long _bytesAtLastRate;
        private double _lastRateAt = Now;
        private int _closed;

        public Session(int id, TcpClient client, WebSocketConnection ws, string remote, CancellationTokenSource cts)
        {
            Id = id;
            Client = client;
            Ws = ws;
            Remote = remote;
            Cts = cts;
        }

        public void Enqueue(Outgoing message) => _queue.Writer.TryWrite(message);

        public void EnqueueText(string json) => Enqueue(new Outgoing(WebSocketOpcode.Text, Encoding.UTF8.GetBytes(json), OutgoingKind.Control));

        /// <summary>Stops taking messages; the send loop closes the connection once the queue is sent.</summary>
        public void CompleteQueue() => _queue.Writer.TryComplete();

        public async Task SendLoop()
        {
            try
            {
                await foreach (Outgoing message in _queue.Reader.ReadAllAsync(Cts.Token).ConfigureAwait(false))
                {
                    if (message.Kind == OutgoingKind.Audio) Interlocked.Decrement(ref QueuedAudio);
                    await Ws.SendAsync(message.Opcode, message.Data, Cts.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _bytesSent, message.Data.Length);
                }
            }
            catch (Exception)
            {
            }
            Close();
        }

        public void ForgetFramesOlderThan(double cutoff)
        {
            List<uint>? stale = null;
            foreach (var entry in SentAt)
            {
                if (entry.Value.At < cutoff) (stale ??= new List<uint>()).Add(entry.Key);
            }
            if (stale == null) return;
            foreach (uint id in stale) SentAt.Remove(id);
        }

        public void UpdateRate(double now)
        {
            long sent = Interlocked.Read(ref _bytesSent);
            double elapsed = now - _lastRateAt;
            if (elapsed <= 0) return;
            KbitPerSecond = (sent - _bytesAtLastRate) * 8 / 1000.0 / elapsed;
            _bytesAtLastRate = sent;
            _lastRateAt = now;
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _queue.Writer.TryComplete();
            try { Cts.Cancel(); } catch { }
            try { Ws.Dispose(); } catch { }
            try { Client.Dispose(); } catch { }
        }
    }
}
