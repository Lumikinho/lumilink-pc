using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Diagnostics;
using LumiLink.Desktop.Discovery;
using LumiLink.Desktop.Messaging;
using LumiLink.Desktop.Pairing;
using LumiLink.Desktop.Profile;
using LumiLink.Desktop.Storage;
using LumiLink.Desktop.Transport;
using Message = LumiLink.Desktop.Messaging.Message;

namespace LumiLink.Desktop.Peers;

public enum PeerConnState { Desconectado, Conectando, Conectado }
public sealed record PeerView(string DeviceId, string Name, string State, DateTime LastSeen, string LastIp, string PhotoHash, string Media = "");

// Uma conexão por par. Só o menor deviceId disca; o maior espera 10 s (PROTOCOL.md §3).
public sealed class PeerManager : IAsyncDisposable
{
    private sealed class Inbox
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<Message>> _w = new();
        public async Task<Message> WaitAsync(string type, TimeSpan t, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
            _w[type] = tcs;
            return await tcs.Task.WaitAsync(t, ct);
        }
        public void Deliver(Message m)
        { if (_w.TryRemove(m.Type, out var tcs)) tcs.TrySetResult(m); }
        public void Fail(Exception ex)
        { foreach (var kv in _w) kv.Value.TrySetException(ex); _w.Clear(); }
    }

    private sealed class Link
    {
        public required bool Outgoing;
        public required Func<Message, Task> Send;
        public required Func<Task> Close;
        public required Func<bool> IsOpen;
        public required MessageContext Ctx;
        public required Inbox Inbox;
        public required CancellationTokenSource Cts;
        public object? Tag; // WsClient (saída) — p/ detach só do próprio link
    }

    private sealed class Session
    {
        public required string Id;
        public PeerConnState State = PeerConnState.Desconectado;
        public Link? Link;
        public bool Busy; // handshake (pareamento) em curso: loop não disca
        public CancellationTokenSource? DialCts; // discagem em curso (p/ abortar dando vez à entrada)
        public TimeSpan Backoff = TimeSpan.FromSeconds(1);
        public DateTime LinkLostAt = DateTime.MinValue;
        public DateTime LastSeen = DateTime.MinValue;
        public string DiscIp = "";
        public int DiscPort;
        public DateTime DiscAt = DateTime.MinValue;
        public int Missed;
    }

    private readonly AppSettings _settings;
    private readonly CertManager _certs;
    private readonly PairStore _pairs;
    private readonly ProfileStore _profiles;
    private readonly PairingManager _pairing;
    private readonly MessageRouter _router;
    private readonly Action<string> _log;
    private readonly RateLimiter _limiter = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (TaskCompletionSource<TimeSpan> tcs, Stopwatch sw)> _pingWaiters = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly Dictionary<string, string> _media = new(); // deviceId -> "▶ título — artista"
    private readonly System.Collections.Concurrent.ConcurrentDictionary<MessageContext, Inbox> _inboxes = new();
    private readonly object _lock = new();
    private SecureWebSocketServer? _server;
    private MdnsDiscovery? _mdns;
    private CancellationTokenSource? _runCts;

    public event Action? Changed;
    public event Action<string, string>? Notify; // (título, texto) p/ balão da bandeja
    public event Func<string, long, Task>? MediaCmd; // cmd do par (play/pause/toggle/next/prev/seek)
    public string DeviceId => _settings.DeviceId;
    public int Port => _settings.Port;
    public string OwnFingerprint => _certs.Fingerprint;
    public string OwnName => _profiles.Current.Name;

    public PeerManager(AppSettings settings, CertManager certs, PairStore pairs,
        ProfileStore profiles, PairingManager pairing, MessageRouter router, Action<string> log)
    {
        _settings = settings; _certs = certs; _pairs = pairs; _profiles = profiles;
        _pairing = pairing; _router = router; _log = log;
        RegisterHandlers();
    }

    public static string ComputeHmac(string secretB64, string nonce)
        => Convert.ToHexString(HMACSHA256.HashData(
            Convert.FromBase64String(secretB64), Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();

    // Kestrel entrega IPv4 como ::ffff:1.2.3.4 — normaliza p/ discar de volta.
    public static string NormIp(string ip)
        => ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) ? ip["::ffff:".Length..] : ip;

    public static string JoinIps(List<string>? ips)
        => string.Join(",", (ips ?? new List<string>()).Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0 && x.Length < 64).Take(8));

    // ---- API pública ----
    public async Task StartAsync(CancellationToken ct)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _server = new SecureWebSocketServer(_certs.Certificate, _settings.Port, _log);
        _server.OnMessage += (ctx, msg) => _router.RouteAsync(ctx, msg);
        _server.OnDisconnected += id => { OnServerDisc(id); return Task.CompletedTask; };
        _mdns = new MdnsDiscovery(_log);
        _mdns.PeerSeen += OnPeerSeen;
        _mdns.Advertise(_settings.DeviceId, OwnName, _settings.Port);
        _mdns.StartBrowse();
        _server.EnsureFirewallRule(_settings.Port);
        lock (_lock) foreach (var p in _pairs.All) EnsureSessionLocked(p.DeviceId);
        _log($"[net] wss ouvindo em 0.0.0.0:{_settings.Port}/ws");
        await _server.StartAsync(_runCts.Token);
    }

    public List<PeerView> GetPeers()
    {
        lock (_lock)
        {
            return _pairs.All.Select(r =>
            {
                _sessions.TryGetValue(r.DeviceId, out var s);
                _media.TryGetValue(r.DeviceId, out var med);
                return new PeerView(r.DeviceId, r.Name, (s?.State ?? PeerConnState.Desconectado).ToString(),
                    r.LastSeen, string.IsNullOrEmpty(r.LastIp) ? (s?.DiscIp ?? "") : r.LastIp, r.PhotoHash,
                    med ?? "");
            }).ToList();
        }
    }

    public (string token, string code) CreatePairing() => _pairing.NewPairing();

    // Pareamento ativo (QR ou manual). credential = token ou código. fp null = TOFU.
    public async Task PairToAsync(string ip, int port, string credential, string? expectedFp)
    {
        var client = new WsClient(_log);
        var ctx = new MessageContext(null, m => client.SendAsync(m));
        var inbox = new Inbox();
        _inboxes[ctx] = inbox;
        Session? session = null;
        string peerId = "";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            client.OnText += async raw =>
            {
                var m = Message.FromJson(raw);
                if (m is not null) await _router.RouteAsync(ctx, m);
            };
            client.OnClosed += () => { inbox.Fail(new IOException("fechado durante pareamento")); return Task.CompletedTask; };
            await client.ConnectAsync(ip, port, expectedFp, cts.Token);
            var me = _profiles.Current;
            await client.SendAsync(Message.Create("hello", new
            {
                token = credential, code = credential, deviceId = _settings.DeviceId,
                deviceName = me.Name, profile = new { name = me.Name, photoHash = me.PhotoHash },
                fingerprint = OwnFingerprint, port = _settings.Port, ips = PairingManager.OrderedIps()
            }));
            var ok = await inbox.WaitAsync("auth_ok", TimeSpan.FromSeconds(20));
            var p = ok.Payload.Deserialize<AuthOkPayload>(Message.PayloadOptions);
            if (string.IsNullOrEmpty(p?.Secret)) throw new InvalidOperationException("pareamento recusado pelo par.");
            peerId = p.DeviceId ?? "";
            if (string.IsNullOrEmpty(peerId)) throw new InvalidOperationException("resposta sem deviceId.");
            var prof = p.Profile;
            var pname = Profile.ImageUtils.SanitizeName(prof?.Name);
            _pairs.SavePeer(peerId, string.IsNullOrEmpty(pname) ? peerId : pname, p.Secret,
                expectedFp ?? client.CapturedFingerprint ?? "", ip, port, prof?.PhotoHash ?? "empty");
            if (prof is not null) _profiles.SavePeer(new DeviceProfile(peerId, pname, prof.PhotoHash, true, true));
            session = EnsureSessionBusy(peerId);
            try
            {
                await AttachOutgoing(session, ctx, inbox, client, ip, port);
                lock (_lock)
                {
                    if (session.Link is null) throw new IOException("link perdido na resolução.");
                }
            }
            finally { lock (_lock) session.Busy = false; }
            _log($"[pair] PAREADO com {peerId}.");
            Changed?.Invoke();
        }
        catch
        {
            _inboxes.TryRemove(ctx, out _);
            try { await client.CloseAsync(); } catch { }
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task ReconnectNowAsync(string deviceId)
    {
        Link? link = null;
        lock (_lock)
        {
            if (_sessions.TryGetValue(deviceId, out var s))
            {
                s.Backoff = TimeSpan.FromMilliseconds(200);
                link = s.Link;
            }
        }
        if (link is not null)
        {
            try { await link.Send(Message.Create("bye", new { reason = "going-away" })); } catch { }
            try { await link.Close(); } catch { }
        }
        _log($"[conn] reconexão forçada de {deviceId}.");
    }

    public async Task ForgetAsync(string deviceId)
    {
        Link? link;
        lock (_lock)
        {
            _sessions.TryGetValue(deviceId, out var s);
            link = s?.Link;
            if (s is not null) { s.Link = null; _sessions.Remove(deviceId); }
        }
        if (link is not null)
        {
            try { await link.Send(Message.Create("bye", new { reason = "going-away" })); } catch { }
            try { await link.Close(); } catch { }
        }
        _pairs.Forget(deviceId);
        _profiles.ForgetPeer(deviceId);
        lock (_lock) _media.Remove(deviceId);
        _log($"[pair] esquecido {deviceId}.");
        Changed?.Invoke();
    }

    public async Task BroadcastProfileUpdateAsync()
    {
        List<Link> links;
        lock (_lock) links = _sessions.Values.Select(s => s.Link).Where(l => l is not null).ToList()!;
        foreach (var l in links) await SendOwnProfileAsync(l.Ctx);
        try
        {
            if (_mdns is not null)
            {
                _mdns.Advertise(_settings.DeviceId, OwnName, _settings.Port); // re-anuncia nome novo
            }
        }
        catch { }
    }

    // Difunde para todos os pares conectados (clipboard etc.). Best-effort por link.
    public async Task BroadcastAsync(Message msg)
    {
        List<Link> links;
        lock (_lock) links = _sessions.Values
            .Select(s => s.Link).Where(l => l is not null && l.IsOpen()).ToList()!;
        foreach (var l in links)
        {
            try { await l.Send(msg); } catch { }
        }
    }

    // Envia a um par específico. false = sem conexão.
    public async Task<bool> SendToAsync(string deviceId, Message msg)
    {
        Link? link;
        lock (_lock) link = _sessions.TryGetValue(deviceId, out var s) ? s.Link : null;
        if (link is null || !link.IsOpen()) return false;
        try { await link.Send(msg); return true; }
        catch { return false; }
    }

    // Ping manual com medida: retorna o RTT ou lança (timeout/sem conexão).
    public async Task<TimeSpan> PingAsync(string deviceId, TimeSpan timeout)
    {
        Link? link;
        lock (_lock) link = _sessions.TryGetValue(deviceId, out var s) ? s.Link : null;
        if (link is null || !link.IsOpen()) throw new InvalidOperationException("sem conexão ativa.");
        var ping = Message.Create("ping", new { manual = true }); // manual avisa o outro lado
        var tcs = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pingWaiters[ping.Id] = (tcs, Stopwatch.StartNew());
        try
        {
            await link.Send(ping);
            return await tcs.Task.WaitAsync(timeout);
        }
        finally { _pingWaiters.TryRemove(ping.Id, out _); }
    }

    public async Task DropLinkAsync(string deviceId) // gancho de teste: derruba à força, nunca lança
    {
        Link? link;
        lock (_lock) link = _sessions.TryGetValue(deviceId, out var s) ? s.Link : null;
        if (link is null) return;
        try { await link.Close(); } catch { }
        DetachLink(link);
    }

    // ---- descoberta ----
    private void OnPeerSeen(string id, string ip, int port)
    {
        if (id == _settings.DeviceId)
        {
            // Vejo meu próprio anúncio (loopback mDNS) — ignora; colisão real
            // seria meu id vindo de outro endpoint.
            if (port == _settings.Port && Pairing.PairingManager.LocalIps().Contains(ip)) return;
            _log("[mdns] AVISO: outro nó anuncia MEU deviceId — colisão de UUID!");
            return;
        }
        if (!_pairs.IsPaired(id)) return;
        lock (_lock)
        {
            var s = EnsureSessionLocked(id);
            s.DiscIp = ip; s.DiscPort = port; s.DiscAt = DateTime.UtcNow;
        }
        Changed?.Invoke();
    }

    private Session EnsureSession(string id)
    {
        lock (_lock) return EnsureSessionLocked(id);
    }

    private Session EnsureSessionLocked(string id)
    {
        if (!_sessions.TryGetValue(id, out var s))
        {
            s = new Session { Id = id };
            _sessions[id] = s;
            if (_runCts is not null)
            {
                var s2 = s;
                _ = Task.Run(() => SessionLoopAsync(s2, _runCts.Token));
            }
        }
        return s;
    }

    // Sessão já nascendo ocupada: o loop não disca até o handshake anexar.
    private Session EnsureSessionBusy(string id)
    {
        lock (_lock)
        {
            var s = EnsureSessionLocked(id);
            s.Busy = true;
            return s;
        }
    }

    // ---- loop por par ----
    private async Task SessionLoopAsync(Session s, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            lock (_lock) { if (!_sessions.ContainsKey(s.Id)) return; }
            if (!_pairs.IsPaired(s.Id)) { try { await Task.Delay(5000, ct); } catch { return; } continue; }
            bool hasLink, busy;
            lock (_lock) { hasLink = s.Link is not null; busy = s.Busy; }
            if (hasLink) { try { await Task.Delay(2000, ct); } catch { return; } continue; }
            if (busy) { _log($"[loop] {s.Id} aguardando handshake… (canceled={ct.IsCancellationRequested})"); try { await Task.Delay(2000, ct); } catch (Exception ex) { _log($"[loop] {s.Id} SAINDO busy: {ex.GetType().Name}"); return; } continue; }

            s.State = PeerConnState.Conectando; Changed?.Invoke();
            var targets = new List<(string ip, int port)>();
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(s.DiscIp) && DateTime.UtcNow - s.DiscAt < TimeSpan.FromSeconds(60))
                    targets.Add((s.DiscIp, s.DiscPort));
                var rec = _pairs.Get(s.Id);
                if (rec is not null && !string.IsNullOrEmpty(rec.LastIp) && rec.LastPort != 0
                    && !targets.Any(t => t.ip == rec.LastIp && t.port == rec.LastPort))
                    targets.Add((rec.LastIp, rec.LastPort));
                if (rec is not null && rec.LastPort != 0) // IPs anunciados pelo par (Tailscale, PAN…)
                    foreach (var k in (rec.KnownIps ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(6))
                        if (!targets.Any(t => t.ip == k)) targets.Add((k, rec.LastPort));
            }
            bool iAmSmaller = string.CompareOrdinal(_settings.DeviceId, s.Id) < 0;
            if (!iAmSmaller)
            {
                var quiet = DateTime.UtcNow - (s.LinkLostAt > s.DiscAt ? s.LinkLostAt : s.DiscAt);
                if (quiet < TimeSpan.FromSeconds(10)) { try { await Task.Delay(1000, ct); } catch { return; } continue; }
            }
            if (targets.Count == 0) { try { await Task.Delay(5000, ct); } catch { return; } continue; }
            bool ok = false;
            for (var ti = 0; ti < targets.Count && !ok; ti++)
            {
                var (ip, port) = targets[ti];
                _log($"[loop] {s.Id} discando {ip}:{port} (smaller={iAmSmaller})…");
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(ti == 0 && targets.Count > 1 ? 12 : 35));
                    await DialAndHandshakeAsync(s, ip, port, cts.Token);
                    lock (_lock) { s.Backoff = TimeSpan.FromSeconds(1); }
                    ok = true;
                }
                catch (Exception ex)
                {
                    _log($"[conn] {s.Id} falhou em {ip}:{port}: {ex.Message}");
                }
            }
            if (!ok)
            {
                lock (_lock) s.State = PeerConnState.Desconectado;
                Changed?.Invoke();
            }
            TimeSpan wait;
            lock (_lock) wait = s.Backoff;
            try { await Task.Delay(wait, ct); } catch { return; }
            lock (_lock) s.Backoff = TimeSpan.FromSeconds(Math.Min(30, s.Backoff.TotalSeconds * 2));
        }
    }

    private async Task DialAndHandshakeAsync(Session s, string ip, int port, CancellationToken ct)
    {
        var rec = _pairs.Get(s.Id) ?? throw new InvalidOperationException("par desconhecido");
        var secret = _pairs.GetSecret(s.Id) ?? throw new InvalidOperationException("sem segredo");
        var myCts = new CancellationTokenSource();
        lock (_lock) { try { s.DialCts?.Cancel(); } catch { } s.DialCts = myCts; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, myCts.Token);
        var lct = linked.Token;
        var client = new WsClient(_log);
        var ctx = new MessageContext(null, m => client.SendAsync(m));
        var inbox = new Inbox();
        _inboxes[ctx] = inbox;
        try
        {
            client.OnText += async raw =>
            {
                var m = Message.FromJson(raw);
                if (m is not null) await _router.RouteAsync(ctx, m);
            };
            client.OnClosed += () =>
            {
                inbox.Fail(new IOException("fechado"));
                DetachIfTag(s, client); // só se o link atual ainda for este
                return Task.CompletedTask;
            };
            await client.ConnectAsync(ip, port,
                string.IsNullOrEmpty(rec.CertFingerprint) ? null : rec.CertFingerprint, lct);
            var me = _profiles.Current;
            await client.SendAsync(Message.Create("hello", new
            {
                deviceId = _settings.DeviceId, deviceName = me.Name,
                profile = new { name = me.Name, photoHash = me.PhotoHash },
                fingerprint = OwnFingerprint, port = _settings.Port, ips = PairingManager.OrderedIps()
            }));
            var ch = await inbox.WaitAsync("auth", TimeSpan.FromSeconds(20), lct);
            var nonce = ch.Payload.Deserialize<AuthChallengePayload>(Message.PayloadOptions)?.Nonce
                ?? throw new InvalidOperationException("desafio inválido");
            var nd = PairingManager.NewNonce();
            await client.SendAsync(Message.Create("auth",
                new { deviceId = _settings.DeviceId, hmac = ComputeHmac(secret, nonce), nonce = nd }));
            var ok = await inbox.WaitAsync("auth_ok", TimeSpan.FromSeconds(20), lct);
            var hmacBack = ok.Payload.Deserialize<AuthOkPayload>(Message.PayloadOptions)?.Hmac;
            if (!string.Equals(hmacBack, ComputeHmac(secret, nd), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("HMAC do par inválido (mútuo).");
            await AttachOutgoing(s, ctx, inbox, client, ip, port);
            _log($"[auth] {s.Id} autenticado (mútuo ok).");
        }
        catch (OperationCanceledException) when (myCts.IsCancellationRequested)
        {
            _log($"[conn] {s.Id}: cedendo vez à entrada (sou maior).");
            _inboxes.TryRemove(ctx, out _);
            try { await client.CloseAsync(); } catch { }
            await client.DisposeAsync();
        }
        catch
        {
            _inboxes.TryRemove(ctx, out _);
            try { await client.CloseAsync(); } catch { }
            await client.DisposeAsync();
            throw;
        }
        finally { lock (_lock) { if (s.DialCts == myCts) s.DialCts = null; } myCts.Dispose(); linked.Dispose(); }
    }

    // Mantém exatamente UM link: o discado pelo menor deviceId. Determinístico
    // nos dois lados — dispensa recusar handshake (fim do deadlock de duplicada).
    private async Task ResolveDuplicatesAsync(Session s, Link? old, Link @new)
    {
        if (old is null) return;
        if (!old.IsOpen())
        {
            DetachLink(old);
            return; // velho morto: o novo vale sem disputa
        }
        bool iAmSmaller = string.CompareOrdinal(_settings.DeviceId, s.Id) < 0;
        bool keepNew = (@new.Outgoing == iAmSmaller);
        if (!keepNew)
        {
            lock (_lock) { if (ReferenceEquals(s.Link, @new)) s.Link = old; }
        }
        _log($"[conn] {s.Id}: duplicada resolvida, mantida a discada pelo menor.");
        var drop = keepNew ? old : @new;
        try { await drop.Close(); } catch { }
        DetachLink(drop);
    }
    private async Task AttachOutgoing(Session s, MessageContext ctx, Inbox inbox, WsClient client, string ip, int port)
    {
        var link = new Link
        {
            Outgoing = true,
            Send = m => client.SendAsync(m),
            Close = () => client.CloseAsync(),
            IsOpen = () => client.IsOpen,
            Ctx = ctx, Inbox = inbox, Cts = new CancellationTokenSource(), Tag = client
        };
        Link? old;
        lock (_lock)
        {
            old = s.Link; s.Link = link;
            s.State = PeerConnState.Conectado;
            s.LastSeen = DateTime.UtcNow; s.Missed = 0;
            ctx.DeviceId = s.Id; ctx.Authenticated = true;
        }
        await ResolveDuplicatesAsync(s, old, link);
        bool mine;
        lock (_lock) mine = ReferenceEquals(s.Link, link);
        if (!mine) { _inboxes.TryRemove(ctx, out _); throw new IOException("link perdido na resolução."); }
        _pairs.Touch(s.Id, ip, port);
        _log($"[conn] {s.Id} via {ip}:{port}.");
        _ = SendOwnProfileAsync(ctx);
        StartPingLoop(s, link);
        Changed?.Invoke();
    }

    // ---- lado ouvinte (hello) ----
    private async Task HandleHelloAsync(MessageContext ctx, Message msg)
    {
        var inbox = new Inbox();
        _inboxes[ctx] = inbox;
        try
        {
            var p = msg.Payload.Deserialize<HelloPayload>(Message.PayloadOptions);
            if (p is null || string.IsNullOrWhiteSpace(p.DeviceId))
            {
                await ctx.SendAsync(Message.Create("bye", new { reason = "bad-hello" }));
                await CloseServerCtx(ctx);
                return;
            }
            var id = p.DeviceId;
            var ip = NormIp(ctx.RemoteIp ?? "");
            if (_limiter.IsBlocked(ip))
            {
                await ctx.SendAsync(Message.Create("bye", new { reason = "rate-limited" }));
                await CloseServerCtx(ctx);
                return;
            }
            var cred = string.IsNullOrEmpty(p.Token) ? p.Code : p.Token;
            if (!string.IsNullOrEmpty(cred) && _pairing.TryConsume(cred))
            {
                var secret = PairingManager.NewSecret();
                var name = Profile.ImageUtils.SanitizeName(p.Profile?.Name);
                if (string.IsNullOrEmpty(name)) name = Profile.ImageUtils.SanitizeName(p.DeviceName);
                if (string.IsNullOrEmpty(name)) name = id;
                _pairs.SavePeer(id, name, secret, p.Fingerprint ?? "", ip, p.Port, p.Profile?.PhotoHash ?? "empty", JoinIps(p.Ips));
                if (p.Profile is not null)
                    _profiles.SavePeer(new DeviceProfile(id, name, p.Profile.PhotoHash, true, true));
                var s = EnsureSessionBusy(id);
                try
                {
                    var me = _profiles.Current;
                    await ctx.SendAsync(Message.Create("auth_ok", new
                    {
                        secret,
                        deviceId = _settings.DeviceId,
                        profile = new { name = me.Name, photoHash = me.PhotoHash }
                    }));
                    _limiter.Success(ip);
                    await AttachIncoming(s, ctx, inbox);
                }
                finally { lock (_lock) s.Busy = false; }
                _log($"[pair] pareado com {id}.");
                Changed?.Invoke();
                return;
            }
            var rec = _pairs.Get(id);
            if (rec is null)
            {
                _limiter.Fail(ip);
                await ctx.SendAsync(Message.Create("bye", new { reason = "not-paired" }));
                await CloseServerCtx(ctx);
                return;
            }
            _log($"[auth] {id}: par conhecido, conferindo fingerprint…");
            if (!string.IsNullOrEmpty(rec.CertFingerprint) && !string.IsNullOrEmpty(p.Fingerprint)
                && !rec.CertFingerprint.Equals(p.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                _limiter.Fail(ip);
                _log($"[auth] fingerprint de {id} não confere.");
                await ctx.SendAsync(Message.Create("bye", new { reason = "bad-hmac" }));
                await CloseServerCtx(ctx);
                return;
            }
            if (string.IsNullOrEmpty(rec.CertFingerprint) && !string.IsNullOrEmpty(p.Fingerprint))
            {
                var sec0 = _pairs.GetSecret(id);
                if (sec0 is not null)
                {
                    _pairs.SavePeer(id, rec.Name, sec0, p.Fingerprint, rec.LastIp, rec.LastPort, rec.PhotoHash);
                    _log($"[auth] fingerprint de {id} aprendido (upgrade de par antigo).");
                }
            }
            var session = EnsureSession(id);
            lock (_lock)
            {
                // Sou maior e estou discando? Aborto minha discagem: a do menor vence.
                // (A resolução final acontece no attach: nunca se recusa handshake.)
                if (string.CompareOrdinal(_settings.DeviceId, id) > 0 && session.DialCts is not null)
                {
                    try { session.DialCts.Cancel(); } catch { }
                    _log($"[conn] {id}: abortei minha discagem (entrada do menor vence).");
                }
            }
            var secret2 = _pairs.GetSecret(id);
            if (secret2 is null) { _log($"[auth] {id}: SEM segredo?!"); await CloseServerCtx(ctx); return; }
            var nonce = PairingManager.NewNonce();
            _log($"[auth] {id}: enviando desafio…");
            await ctx.SendAsync(Message.Create("auth", new { nonce }));
            _log($"[auth] {id}: desafio enviado, aguardando resposta…");
            AuthResponsePayload? resp;
            try { resp = (await inbox.WaitAsync("auth", TimeSpan.FromSeconds(30))).Payload.Deserialize<AuthResponsePayload>(Message.PayloadOptions); }
            catch { await CloseServerCtx(ctx); return; }
            if (resp is null || !string.Equals(resp.Hmac, ComputeHmac(secret2, nonce), StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(resp.Nonce))
            {
                _limiter.Fail(ip);
                await ctx.SendAsync(Message.Create("bye", new { reason = "bad-hmac" }));
                await CloseServerCtx(ctx);
                return;
            }
            await ctx.SendAsync(Message.Create("auth_ok", new { hmac = ComputeHmac(secret2, resp.Nonce) }));
            _limiter.Success(ip);
            await AttachIncoming(session, ctx, inbox);
            _pairs.Touch(id, ip, p.Port, JoinIps(p.Ips));
            _log($"[auth] {id} autenticado (mútuo ok).");
            Changed?.Invoke();
        }
        finally { _inboxes.TryRemove(ctx, out _); }
    }

    private async Task AttachIncoming(Session s, MessageContext ctx, Inbox inbox)
    {
        var link = new Link
        {
            Outgoing = false,
            Send = m => ctx.SendAsync(m),
            Close = () => CloseServerCtx(ctx),
            IsOpen = () => ctx.Socket?.State == WebSocketState.Open,
            Ctx = ctx, Inbox = inbox, Cts = new CancellationTokenSource()
        };
        Link? old;
        lock (_lock)
        {
            old = s.Link; s.Link = link;
            s.State = PeerConnState.Conectado;
            s.LastSeen = DateTime.UtcNow; s.Missed = 0;
            ctx.DeviceId = s.Id; ctx.Authenticated = true;
        }
        await ResolveDuplicatesAsync(s, old, link);
        bool mine;
        lock (_lock) mine = ReferenceEquals(s.Link, link);
        if (!mine) { _inboxes.TryRemove(ctx, out _); return; }
        _ = SendOwnProfileAsync(ctx);
        StartPingLoop(s, link);
        Changed?.Invoke();
    }

    private static async Task CloseServerCtx(MessageContext ctx)
    {
        try
        {
            if (ctx.Socket?.State == WebSocketState.Open)
                await ctx.Socket!.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch { }
    }

    // ---- ping / detach ----
    private void StartPingLoop(Session s, Link link)
    {
        _ = Task.Run(async () =>
        {
            var ct = link.Cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!link.IsOpen()) { DetachLink(link); break; } // socket mudou: derruba na hora
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    try { await link.Send(Message.Create("ping", new { })); }
                    catch { DetachLink(link); break; } // envio falhou = caída
                    bool dead;
                    lock (_lock) { s.Missed++; dead = s.Missed >= 3; }
                    if (dead)
                    {
                        _log($"[conn] {s.Id}: 3 pongs perdidos.");
                        try { await link.Close(); } catch { }
                        DetachLink(link);
                        break;
                    }
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex) { _log($"[ping] {s.Id}: {ex.Message}"); }
        });
    }

    private void DetachLink(Link link)
    {
        bool cleared = false;
        lock (_lock)
        {
            foreach (var s in _sessions.Values)
                if (ReferenceEquals(s.Link, link))
                {
                    s.Link = null; s.LinkLostAt = DateTime.UtcNow;
                    s.State = PeerConnState.Desconectado; cleared = true;
                }
        }
        try { link.Cts.Cancel(); } catch { }
        _inboxes.TryRemove(link.Ctx, out _);
        if (cleared) { _log("[conn] caída. Backoff por par."); Changed?.Invoke(); }
    }

    private void DetachIfTag(Session s, object? tag)
    {
        Link? link;
        lock (_lock) link = (s.Link is not null && ReferenceEquals(s.Link.Tag, tag)) ? s.Link : null;
        if (link is not null) DetachLink(link);
    }

    private void OnServerDisc(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        Session? s;
        lock (_lock) _sessions.TryGetValue(id, out s);
        if (s is null) return;
        Link? link;
        lock (_lock) link = s.Link;
        if (link is not null && !link.Outgoing && !link.IsOpen()) DetachLink(link);
    }

    private Task SendOwnProfileAsync(MessageContext ctx)
        => ctx.SendAsync(Message.Create("profile",
            new { name = _profiles.Current.Name, photoHash = _profiles.Current.PhotoHash }));

    // ---- roteador ----
    private void RegisterHandlers()
    {
        // hello roda fora do pump de leitura: o handshake do ouvinte aguarda
        // mensagens (auth) que só chegam se o pump continuar lendo o socket.
        _router.Register("hello", (ctx, msg) =>
        {
            _ = Task.Run(async () =>
            {
                try { await HandleHelloAsync(ctx, msg); }
                catch (Exception ex) { _log($"[hello] falhou: {ex.Message}"); }
            });
            return Task.CompletedTask;
        });
        _router.Register("auth", (ctx, msg) =>
        {
            if (_inboxes.TryGetValue(ctx, out var ib)) ib.Deliver(msg);
            return Task.CompletedTask;
        });
        _router.Register("auth_ok", (ctx, msg) =>
        {
            if (_inboxes.TryGetValue(ctx, out var ib)) ib.Deliver(msg);
            return Task.CompletedTask;
        });
        _router.Register("ping", async (ctx, msg) =>
        {
            await ctx.SendAsync(Message.Create("pong", new { echo = msg.Id }));
            try
            {
                if (msg.Payload.TryGetProperty("manual", out var man)
                    && man.ValueKind == JsonValueKind.True && ctx.DeviceId is not null)
                {
                    var peerName = _pairs.Get(ctx.DeviceId)?.Name ?? ctx.DeviceId;
                    Notify?.Invoke("Ping recebido", $"De {peerName}.");
                }
            }
            catch { }
        });
        _router.Register("pong", (ctx, msg) =>
        {
            lock (_lock)
                foreach (var s in _sessions.Values)
                    if (s.Link is not null && ReferenceEquals(s.Link.Ctx, ctx)) s.Missed = 0;
            try
            {
                if (msg.Payload.TryGetProperty("echo", out var e)
                    && _pingWaiters.TryRemove(e.GetString() ?? "", out var w))
                    w.tcs.TrySetResult(w.sw.Elapsed);
            }
            catch { }
            return Task.CompletedTask;
        });
        _router.Register("profile", async (ctx, msg) => await OnProfileAsync(ctx, msg, false));
        _router.Register("profile_update", async (ctx, msg) => await OnProfileAsync(ctx, msg, true));
        _router.Register("profile_photo", (ctx, msg) =>
        {
            if (!ctx.Authenticated || ctx.DeviceId is null) return Task.CompletedTask;
            try
            {
                var ph = msg.Payload.Deserialize<ProfilePhotoPayload>(Message.PayloadOptions);
                if (ph is null || ph.DataBase64.Length > 140_000) { _log("[profile] foto grande, ignorada."); return Task.CompletedTask; }
                var bytes = Convert.FromBase64String(ph.DataBase64);
                if (!Profile.ImageUtils.ValidateIncomingPhoto(bytes, ph.PhotoHash)) { _log("[profile] foto inválida, ignorada."); return Task.CompletedTask; }
                File.WriteAllBytes(_profiles.PeerPhotoPath(ctx.DeviceId), bytes);
                _log("[profile] foto do par salva.");
                Changed?.Invoke();
            }
            catch (Exception ex) { _log($"[profile] foto ignorada: {ex.Message}"); }
            return Task.CompletedTask;
        });
        _router.Register("profile_photo_request", async (ctx, msg) =>
        {
            if (!ctx.Authenticated) return;
            var path = _profiles.OwnPhotoPath();
            if (!File.Exists(path)) return;
            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length > 100 * 1024) { _log("[profile] foto >100KB, não enviada."); return; }
            await ctx.SendAsync(Message.Create("profile_photo", new
            {
                photoHash = _profiles.Current.PhotoHash, mime = "image/jpeg",
                dataBase64 = Convert.ToBase64String(bytes)
            }));
        });
        _router.Register("bye", (ctx, msg) =>
        {
            var reason = "";
            try { reason = msg.Payload.Deserialize<ByePayload>(Message.PayloadOptions)?.Reason ?? ""; } catch { }
            _log($"[conn] bye de {ctx.DeviceId ?? "?"} ({reason}).");
            Link? dead = null; // bye válido derruba o link na hora (fecha limpo, sem esperar ping)
            lock (_lock)
                foreach (var s in _sessions.Values)
                    if (s.Link is not null && ReferenceEquals(s.Link.Ctx, ctx)) dead = s.Link;
            if (dead is not null) DetachLink(dead);
            return Task.CompletedTask;
        });
        Messaging.EchoExample.Register(_router, _log);
        _router.Register("media_cmd", async (ctx, msg) =>
        {
            if (!ctx.Authenticated) return;
            try
            {
                var cmd = msg.Payload.TryGetProperty("cmd", out var c) ? (c.GetString() ?? "") : "";
                var pos = msg.Payload.TryGetProperty("pos", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetInt64() : -1L;
                if (MediaCmd is not null) await MediaCmd(cmd, pos);
            }
            catch (Exception ex) { _log($"[media] cmd ignorado: {ex.Message}"); }
        });
        _router.Register("media_status", (ctx, msg) =>
        {
            if (!ctx.Authenticated || ctx.DeviceId is null) return Task.CompletedTask;
            try
            {
                var t = msg.Payload.TryGetProperty("title", out var tt) ? (tt.GetString() ?? "").Trim() : "";
                if (t.Length > 64) t = t[..64];
                var a = msg.Payload.TryGetProperty("artist", out var aa) ? (aa.GetString() ?? "").Trim() : "";
                if (a.Length > 64) a = a[..64];
                var playing = msg.Payload.TryGetProperty("playing", out var pp) && pp.ValueKind == JsonValueKind.True;
                var state = msg.Payload.TryGetProperty("state", out var ss) ? (ss.GetString() ?? "") : "";
                var line = t.Length == 0 ? "" : (t + (a.Length == 0 ? "" : " — " + a));
                string? old;
                lock (_lock) _media.TryGetValue(ctx.DeviceId, out old);
                if (old == line) return Task.CompletedTask;
                lock (_lock) _media[ctx.DeviceId] = line;
                if (line.Length > 0)
                {
                    _log($"[media] {ctx.DeviceId}: {line}");
                    Notify?.Invoke("Tocando agora", line);
                }
                Changed?.Invoke();
            }
            catch { }
            return Task.CompletedTask;
        });
    }

    private async Task OnProfileAsync(MessageContext ctx, Message msg, bool update)
    {
        if (!ctx.Authenticated || ctx.DeviceId is null) return;
        var prof = msg.Payload.Deserialize<ProfilePayload>(Message.PayloadOptions);
        if (prof is null) return;
        var name = Profile.ImageUtils.SanitizeName(prof.Name);
        if (string.IsNullOrEmpty(name)) { _log("[profile] nome inválido, ignorado."); return; }
        var id = ctx.DeviceId;
        var cached = _profiles.LoadPeer(id);
        _profiles.SavePeer(new DeviceProfile(id, name, prof.PhotoHash ?? "empty", true, true));
        _pairs.UpdateName(id, name, prof.PhotoHash ?? "empty");
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s)) s.LastSeen = DateTime.UtcNow;
        }
        _log($"[profile] {(update ? "atualizado" : "recebido")}: {name}.");
        Changed?.Invoke();
        if (cached?.PhotoHash != prof.PhotoHash && prof.PhotoHash != "empty")
            await ctx.SendAsync(Message.Create("profile_photo_request", new { }));
    }

    public async ValueTask DisposeAsync()
    {
        try { _runCts?.Cancel(); } catch { }
        if (_mdns is not null) try { await _mdns.DisposeAsync(); } catch { }
        List<Link> links;
        lock (_lock) links = _sessions.Values.Select(s => s.Link).Where(l => l is not null).ToList()!;
        foreach (var l in links) try { await l.Close(); } catch { }
    }
}
