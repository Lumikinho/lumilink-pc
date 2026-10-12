using System.Collections.Concurrent;
using System.IO;
using LumiLink.Desktop.Messaging;
using LumiLink.Desktop.Peers;
using LumiLink.Desktop.Storage;
using Message = LumiLink.Desktop.Messaging.Message;

namespace LumiLink.Desktop.Clipboard;

// Sincroniza a área de transferência via MessageRouter (tipos clip_*).
public sealed class ClipSync : IAsyncDisposable
{
    private const int Chunk = 32 * 1024;
    private const long MaxFile = 200L * 1024 * 1024;

    private readonly MessageRouter _router;
    private readonly PeerManager _peers;
    private readonly ClipStore _store;
    private readonly ClipMonitor _monitor;
    private readonly string _selfId;
    private readonly Action<string> _log;
    private readonly Action<string, string> _notify;
    private readonly Func<bool> _enabled;
    private readonly Func<string, string> _peerName;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<HashSet<string>>> _acceptWaits = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _accepts = new();
    private readonly ConcurrentDictionary<string, ClipFileSession> _incoming = new();
    private readonly CancellationTokenSource _cts = new();

    public ClipSync(MessageRouter router, PeerManager peers, ClipStore store, ClipMonitor monitor,
        string selfId, Action<string> log, Action<string, string> notify, Func<bool> enabled,
        Func<string, string> peerName)
    {
        _router = router; _peers = peers; _store = store; _monitor = monitor;
        _selfId = selfId; _log = log; _notify = notify; _enabled = enabled; _peerName = peerName;
        _monitor.Changed += c => _ = OnLocalAsync(c);
        Register();
        _ = SweepLoop();
    }

    private bool On() { try { return _enabled(); } catch { return true; } }

    // ---- origem local ----
    private async Task OnLocalAsync(ClipContent c)
    {
        try
        {
            if (!On()) return;
            if (c.Text is not null)
            {
                _monitor.Suppress(c.Hash);
                _store.AddText(c.Text, _selfId, "*");
                await _peers.BroadcastAsync(Message.Create("clip_text",
                    new { text = c.Text, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), source = _selfId }));
                _log($"[clip] texto difundido ({c.Text.Length} ch).");
            }
            else if (c.ImageBytes is not null)
            {
                _monitor.Suppress(c.Hash);
                var ah0 = Profile.ImageUtils.AverageHash(c.ImageBytes);
                if (ah0.HasValue) ClipMonitor.SuppressImage(ah0.Value);
                var ext = c.ImageMime == "image/jpeg" ? "jpg" : "png";
                var path = Path.Combine(_store.ClipDir, $"clip-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{ext}");
                await File.WriteAllBytesAsync(path, c.ImageBytes);
                _store.AddImage(c.ImageMime, path, c.ImageBytes.Length, c.Hash, _selfId, "*");
                await _peers.BroadcastAsync(Message.Create("clip_image", new
                {
                    mime = c.ImageMime, dataBase64 = Convert.ToBase64String(c.ImageBytes),
                    hash = c.Hash, w = 0, h = 0
                }));
                _log($"[clip] imagem difundida ({c.ImageBytes.Length / 1024} KB).");
            }
            else if (c.Files.Length > 0)
            {
                foreach (var f in c.Files) _ = SendFileAsync(f);
            }
        }
        catch (Exception ex) { _log($"[clip] envio falhou: {ex.Message}"); }
    }

    public async Task SendFileAsync(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxFile) { _log("[clip] arquivo grande demais/inexistente."); return; }
        var fileId = Guid.NewGuid().ToString("N")[..16];
        string hex;
        using (var fs = File.OpenRead(path))
        using (var sha = System.Security.Cryptography.SHA256.Create())
            hex = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        var waiters = new TaskCompletionSource<HashSet<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _acceptWaits[fileId] = waiters;
        _accepts[fileId] = new HashSet<string>();
        try
        {
            await _peers.BroadcastAsync(Message.Create("clip_file_offer",
                new { fileId, name = info.Name, size = info.Length, sha256 = hex }));
            _log($"[clip] oferta {info.Name} ({info.Length / 1024} KB)…");
            var ok = await waiters.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(2000); // graça p/ outros aceites
            List<string> who;
            lock (ok) who = ok.ToList();
            if (who.Count == 0) { _log("[clip] ninguém aceitou."); return; }
            using var fs = File.OpenRead(path);
            var buf = new byte[Chunk];
            int seq = 0, n;
            while ((n = await fs.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                var b64 = Convert.ToBase64String(buf, 0, n);
                foreach (var id in who)
                    await _peers.SendToAsync(id, Message.Create("clip_file_chunk",
                        new { fileId, seq, data = b64 }));
                seq++;
            }
            foreach (var id in who)
                await _peers.SendToAsync(id, Message.Create("clip_file_done",
                    new { fileId, sha256 = hex }));
            _log($"[clip] {info.Name} enviado ({seq} chunks).");
        }
        catch (Exception ex) { _log($"[clip] envio falhou: {ex.Message}"); }
        finally { _acceptWaits.TryRemove(fileId, out _); _accepts.TryRemove(fileId, out _); }
    }

    // ---- recepção ----
    private void Register()
    {
        _router.Register("clip_text", (ctx, msg) =>
        {
            if (!On() || !ctx.Authenticated || ctx.DeviceId is null) return Task.CompletedTask;
            try
            {
                var text = msg.Payload.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(text) || text.Length > 256 * 1024) return Task.CompletedTask;
                _monitor.Suppress(ClipFileSession.ShaHex(text));
                _store.AddText(text, ctx.DeviceId, ctx.DeviceId);
                _monitor.SetText(text);
                _notify("Área de transferência", $"Texto de {_peerName(ctx.DeviceId)}.");
                _log("[clip] texto recebido e aplicado.");
            }
            catch (Exception ex) { _log($"[clip] texto ignorado: {ex.Message}"); }
            return Task.CompletedTask;
        });

        _router.Register("clip_image", (ctx, msg) =>
        {
            if (!On() || !ctx.Authenticated || ctx.DeviceId is null) return Task.CompletedTask;
            try
            {
                var b64 = msg.Payload.TryGetProperty("dataBase64", out var d) ? d.GetString() ?? "" : "";
                if (b64.Length > 4_500_000) { _log("[clip] imagem grande, ignorada."); return Task.CompletedTask; }
                var bytes = Convert.FromBase64String(b64);
                var hash = msg.Payload.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(hash) && !hash.Equals(ClipFileSession.ShaHex(bytes), StringComparison.OrdinalIgnoreCase))
                { _log("[clip] hash da imagem não confere."); return Task.CompletedTask; }
                if (ClipMonitor.DecodeImage(bytes) is null) { _log("[clip] imagem inválida."); return Task.CompletedTask; }
                var mime = msg.Payload.TryGetProperty("mime", out var m) ? m.GetString() ?? "image/png" : "image/png";
                var path = Path.Combine(_store.ClipDir, $"clip-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{(mime.Contains("jpeg") ? ".jpg" : ".png")}");
                File.WriteAllBytes(path, bytes);
                _monitor.Suppress(ClipFileSession.ShaHex(bytes));
                var ah1 = Profile.ImageUtils.AverageHash(bytes);
                if (ah1.HasValue) ClipMonitor.SuppressImage(ah1.Value);
                _store.AddImage(mime, path, bytes.Length, hash, ctx.DeviceId, ctx.DeviceId);
                _monitor.SetImage(bytes);
                _notify("Área de transferência", $"Imagem de {_peerName(ctx.DeviceId)}.");
                _log("[clip] imagem recebida e aplicada.");
            }
            catch (Exception ex) { _log($"[clip] imagem ignorada: {ex.Message}"); }
            return Task.CompletedTask;
        });

        _router.Register("clip_file_offer", async (ctx, msg) =>
        {
            if (!On() || !ctx.Authenticated || ctx.DeviceId is null) return;
            try
            {
                var p = msg.Payload;
                var fileId = p.TryGetProperty("fileId", out var f) ? f.GetString() ?? "" : "";
                var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? fileId : fileId;
                var size = p.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                var sha = p.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(fileId) || size <= 0 || size > MaxFile) return;
                _incoming[fileId] = new ClipFileSession(_store.ClipDir, fileId, name, size, sha);
                await ctx.SendAsync(Message.Create("clip_file_accept", new { fileId, accept = true }));
                _log($"[clip] oferta aceita: {name}.");
            }
            catch (Exception ex) { _log($"[clip] oferta ignorada: {ex.Message}"); }
        });

        _router.Register("clip_file_accept", (ctx, msg) =>
        {
            try
            {
                var fileId = msg.Payload.TryGetProperty("fileId", out var f) ? f.GetString() ?? "" : "";
                var accept = msg.Payload.TryGetProperty("accept", out var a) && a.GetBoolean();
                if (accept && ctx.DeviceId is not null && _accepts.TryGetValue(fileId, out var set))
                {
                    lock (set) set.Add(ctx.DeviceId);
                    if (_acceptWaits.TryGetValue(fileId, out var w)) w.TrySetResult(set);
                }
            }
            catch { }
            return Task.CompletedTask;
        });

        _router.Register("clip_file_chunk", (ctx, msg) =>
        {
            if (!On() || !ctx.Authenticated) return Task.CompletedTask;
            try
            {
                var p = msg.Payload;
                var fileId = p.TryGetProperty("fileId", out var f) ? f.GetString() ?? "" : "";
                var seq = p.TryGetProperty("seq", out var s) ? s.GetInt32() : -1;
                var data = p.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
                if (!_incoming.TryGetValue(fileId, out var sess)) return Task.CompletedTask;
                if (!sess.Feed(seq, Convert.FromBase64String(data)))
                    _log($"[clip] chunk fora de ordem ({fileId}:{seq}).");
            }
            catch (Exception ex) { _log($"[clip] chunk ignorado: {ex.Message}"); }
            return Task.CompletedTask;
        });

        _router.Register("clip_file_done", (ctx, msg) =>
        {
            if (!ctx.Authenticated || ctx.DeviceId is null) return Task.CompletedTask;
            try
            {
                var fileId = msg.Payload.TryGetProperty("fileId", out var f) ? f.GetString() ?? "" : "";
                if (!_incoming.TryRemove(fileId, out var sess)) return Task.CompletedTask;
                try
                {
                    if (!sess.VerifyAndClose(out var actual))
                    {
                        _log($"[clip] arquivo corrompido (hash).");
                        sess.Dispose();
                        return Task.CompletedTask;
                    }
                    var dst = UniquePath(Path.Combine(_store.ClipDir, sess.Name));
                    File.Move(sess.PartPath, dst);
                    _store.AddFile(sess.Name, dst, sess.Size, sess.Sha256, ctx.DeviceId, ctx.DeviceId);
                    _monitor.SetFiles(new[] { dst });
                    _notify("Arquivo recebido", $"{sess.Name} de {_peerName(ctx.DeviceId)}.");
                    _log($"[clip] arquivo salvo: {dst}.");
                }
                finally { try { sess.Dispose(); } catch { } }
            }
            catch (Exception ex) { _log($"[clip] done ignorado: {ex.Message}"); }
            return Task.CompletedTask;
        });
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var c = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(c)) return c;
        }
        return path;
    }

    private async Task SweepLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                foreach (var kv in _incoming.ToArray())
                    if (kv.Value.IdleFor(TimeSpan.FromSeconds(60)))
                    {
                        if (_incoming.TryRemove(kv.Key, out var s)) s.Dispose();
                        _log($"[clip] sessão expirada ({kv.Key}).");
                    }
            }
        }
        catch (TaskCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        await Task.CompletedTask;
    }
}
