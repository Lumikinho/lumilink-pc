using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LumiLink.Desktop.Messaging;
using LumiLink.Desktop.Pairing;
using LumiLink.Desktop.Profile;
using LumiLink.Desktop.Storage;
using LumiLink.Desktop.Transport;
using Message = LumiLink.Desktop.Messaging.Message;

namespace LumiLink.Desktop.Core;

// Orquestra: hello/auth/ping-pong/profile. Camada fina sobre Transport + Router.
public sealed class ConnectionManager
{
    private readonly PairingManager _pairing;
    private readonly PairStore _pairs;
    private readonly ProfileStore _profiles;
    private readonly MessageRouter _router;
    private readonly Action<string> _log;
    private readonly Dictionary<string, string> _pendingNonce = new(); // deviceId -> nonce
    private readonly Dictionary<string, int> _missedPongs = new();
    private MessageContext? _active;
    private string? _activeDeviceId;
    private CancellationTokenSource? _pingCts;

    public event Action<string>? StateChanged; // "Desconectado" | "Conectado a X"
    public string State { get; private set; } = "Desconectado";

    public ConnectionManager(PairingManager pairing, PairStore pairs, ProfileStore profiles, MessageRouter router, Action<string> log)
    {
        _pairing = pairing; _pairs = pairs; _profiles = profiles; _router = router; _log = log;
        RegisterHandlers();
    }

    public void AttachServer(SecureWebSocketServer server)
    {
        server.OnConnected += ctx => { _active = ctx; return Task.CompletedTask; };
        server.OnMessage += (ctx, msg) => _router.RouteAsync(ctx, msg);
        server.OnDisconnected += _ =>
        {
            SetState("Desconectado");
            _active = null; _activeDeviceId = null;
            _pingCts?.Cancel();
            _log("[conn] caiu. Aguardando celular reconectar (backoff no cliente).");
            return Task.CompletedTask;
        };
    }

    private void SetState(string s) { State = s; StateChanged?.Invoke(s); }

    private void RegisterHandlers()
    {
        // hello: pareamento (com token) OU reconexão (deviceId conhecido).
        _router.Register("hello", async (ctx, msg) =>
        {
            var p = msg.Payload.Deserialize<HelloPayload>();
            if (p is null) return;
            _log($"[hello] de {p.DeviceId} ({p.DeviceName})");

            if (p.Token is not null && _pairing.TryConsumeToken(p.Token))
            {
                // Pareamento OK: gera segredo, registra par + perfil inicial.
                var secret = PairingManager.NewSecret();
                var name = ImageUtils.SanitizeName(p.Profile?.Name);
                if (string.IsNullOrEmpty(name)) name = p.DeviceName;
                _pairs.Save(p.DeviceId, name, secret);
                var peerProfile = new DeviceProfile(p.DeviceId, name, p.Profile?.PhotoHash ?? "empty", true, false);
                _profiles.SavePeer(peerProfile);
                ctx.DeviceId = p.DeviceId; ctx.Authenticated = true;
                _activeDeviceId = p.DeviceId;
                await ctx.SendAsync(Message.Create("auth_ok", new { secret, deviceId = _profiles.Current.DeviceId }));
                _log($"[pair] pareado com {p.DeviceId}. Segredo de 32 bytes entregue.");
                await SendOwnProfileAsync(ctx);
                SetState($"Conectado a {name}");
                StartPingLoop(ctx);
                return;
            }

            if (_pairs.IsPaired(p.DeviceId))
            {
                // Reconexão: envia desafio nonce.
                var nonce = PairingManager.NewNonce();
                _pendingNonce[p.DeviceId] = nonce;
                ctx.DeviceId = p.DeviceId;
                await ctx.SendAsync(Message.Create("auth", new { nonce }));
                _log($"[auth] desafio enviado a {p.DeviceId}.");
                return;
            }

            _log($"[auth] deviceId {p.DeviceId} desconhecido. Peça novo QR.");
            await ctx.SendAsync(Message.Create("bye", new { reason = "not-paired" }));
        });

        // auth (resposta HMAC do celular)
        _router.Register("auth", async (ctx, msg) =>
        {
            var p = msg.Payload.Deserialize<AuthResponsePayload>();
            if (p is null || ctx.DeviceId is null) return;
            if (!_pendingNonce.TryGetValue(p.DeviceId, out var nonce)) return;
            var secret = _pairs.GetSecret(p.DeviceId);
            if (secret is null) return;
            var expect = Convert.ToHexString(HMACSHA256.HashData(
                Convert.FromBase64String(secret), Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(p.Hmac), Encoding.UTF8.GetBytes(expect)))
            {
                _log($"[auth] HMAC inválido de {p.DeviceId}.");
                await ctx.SendAsync(Message.Create("bye", new { reason = "bad-hmac" }));
                return;
            }
            _pendingNonce.Remove(p.DeviceId);
            ctx.Authenticated = true;
            _activeDeviceId = p.DeviceId;
            var peer = _profiles.LoadPeer(p.DeviceId);
            var show = peer?.Name ?? p.DeviceId;
            await ctx.SendAsync(Message.Create("auth_ok", new { }));
            _log($"[auth] {p.DeviceId} autenticado (HMAC ok).");
            await SendOwnProfileAsync(ctx);
            SetState($"Conectado a {show}");
            StartPingLoop(ctx);
        });

        _router.Register("ping", async (ctx, msg) =>
        {
            await ctx.SendAsync(Message.Create("pong", new { echo = msg.Id }));
        });

        _router.Register("pong", (ctx, msg) =>
        {
            if (ctx.DeviceId is not null) _missedPongs[ctx.DeviceId] = 0;
            return Task.CompletedTask;
        });

        _router.Register("bye", (ctx, msg) =>
        {
            _log("[conn] bye recebido.");
            SetState("Desconectado");
            return Task.CompletedTask;
        });

        // Perfil: recebe {name, photoHash}; pede foto se hash difere do cache.
        _router.Register("profile", async (ctx, msg) =>
        {
            if (!ctx.Authenticated || ctx.DeviceId is null) return;
            var prof = msg.Payload.Deserialize<ProfilePayload>();
            if (prof is null) return;
            var name = ImageUtils.SanitizeName(prof.Name);
            if (string.IsNullOrEmpty(name)) { _log("[profile] nome inválido, ignorado."); return; }
            var cached = _profiles.LoadPeer(ctx.DeviceId);
            var photoHash = prof.PhotoHash ?? "empty";
            _profiles.SavePeer(new DeviceProfile(ctx.DeviceId, name, photoHash, true, true));
            _pairs.UpdateName(ctx.DeviceId, name);
            _log($"[profile] {ctx.DeviceId} é '{name}' ({photoHash[..Math.Min(8, photoHash.Length)]}…).");
            SetState($"Conectado a {name}");
            if (cached?.PhotoHash != photoHash && photoHash != "empty")
                await ctx.SendAsync(Message.Create("profile_photo_request", new { }));
        });

        _router.Register("profile_update", async (ctx, msg) =>
        {
            // Mesmo tratamento do profile.
            if (!ctx.Authenticated || ctx.DeviceId is null) return;
            var prof = msg.Payload.Deserialize<ProfilePayload>();
            if (prof is null) return;
            var name = ImageUtils.SanitizeName(prof.Name);
            if (string.IsNullOrEmpty(name)) return;
            _profiles.SavePeer(new DeviceProfile(ctx.DeviceId, name, prof.PhotoHash ?? "empty", true, true));
            _pairs.UpdateName(ctx.DeviceId, name);
            SetState($"Conectado a {name}");
            _log($"[profile] atualizado: {name}");
            var cached = _profiles.LoadPeer(ctx.DeviceId);
            if (prof.PhotoHash != "empty" && File.Exists(_profiles.PeerPhotoPath(ctx.DeviceId)) == false)
                await ctx.SendAsync(Message.Create("profile_photo_request", new { }));
        });

        _router.Register("profile_photo", (ctx, msg) =>
        {
            if (!ctx.Authenticated || ctx.DeviceId is null) return Task.CompletedTask;
            try
            {
                var ph = msg.Payload.Deserialize<ProfilePhotoPayload>();
                if (ph is null || ph.DataBase64.Length > 140_000) { _log("[profile] foto grande demais, ignorada."); return Task.CompletedTask; }
                var bytes = Convert.FromBase64String(ph.DataBase64);
                if (!ImageUtils.ValidateIncomingPhoto(bytes, ph.PhotoHash)) { _log("[profile] foto inválida/hash mismatch, ignorada."); return Task.CompletedTask; }
                File.WriteAllBytes(_profiles.PeerPhotoPath(ctx.DeviceId), bytes);
                _log("[profile] foto do parceiro salva.");
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
            if (bytes.Length > 100 * 1024) { _log("[profile] foto própria >100KB, não enviada."); return; }
            await ctx.SendAsync(Message.Create("profile_photo", new
            {
                photoHash = _profiles.Current.PhotoHash,
                mime = "image/jpeg",
                dataBase64 = Convert.ToBase64String(bytes)
            }));
        });

        EchoExample.Register(_router, _log);
    }

    private async Task SendOwnProfileAsync(MessageContext ctx)
    {
        await ctx.SendAsync(Message.Create("profile",
            new { name = _profiles.Current.Name, photoHash = _profiles.Current.PhotoHash }));
    }

    public async Task BroadcastProfileUpdateAsync()
    {
        if (_active is not null && _active.Authenticated)
            await SendOwnProfileAsync(_active);
    }

    private void StartPingLoop(MessageContext ctx)
    {
        _pingCts?.Cancel();
        _pingCts = new CancellationTokenSource();
        var ct = _pingCts.Token;
        var dev = ctx.DeviceId ?? "?";
        _missedPongs[dev] = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && ctx.Socket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    await ctx.SendAsync(Message.Create("ping", new { }));
                    _missedPongs[dev] = _missedPongs.GetValueOrDefault(dev) + 1;
                    if (_missedPongs[dev] >= 3)
                    {
                        _log("[conn] 3 pongs perdidos — considerando caída.");
                        SetState("Desconectado");
                        break;
                    }
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex) { _log($"[ping] {ex.Message}"); }
        }, ct);
    }
}
