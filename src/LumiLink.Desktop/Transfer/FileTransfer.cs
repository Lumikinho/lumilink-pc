using System.Collections.Concurrent;
using System.IO;
using LumiLink.Desktop.Messaging;
using LumiLink.Desktop.Peers;
using LumiLink.Desktop.Storage;
using Message = LumiLink.Desktop.Messaging.Message;

namespace LumiLink.Desktop.Transfer;

// Envio EXPLÍCITO de arquivos (botão — não via clipboard).
// clip_file_offer/accept/chunk/done, 32 KB por chunk, SHA-256 no done.
public sealed class FileTransfer : IAsyncDisposable
{
    private const int Chunk = 32 * 1024;
    public const long MaxFile = 200L * 1024 * 1024;

    private readonly MessageRouter _router;
    private readonly PeerManager _peers;
    private readonly ClipStore _store;
    private readonly Action<string> _log;
    private readonly Action<string, string> _notify;
    private readonly Func<string, string> _peerName;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<HashSet<string>>> _acceptWaits = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _accepts = new();
    private readonly ConcurrentDictionary<string, FileAssembler> _incoming = new();
    private readonly CancellationTokenSource _cts = new();

    public FileTransfer(MessageRouter router, PeerManager peers, ClipStore store, string selfId,
        Action<string> log, Action<string, string> notify, Func<string, string> peerName)
    {
        _router = router; _peers = peers; _store = store;
        _log = log; _notify = notify; _peerName = peerName;
        Register();
        _ = SweepLoop();
    }

    public async Task SendFileAsync(string deviceId, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("arquivo não existe.", path);
        if (info.Length > MaxFile) throw new InvalidOperationException("arquivo > 200 MB.");
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
            if (!await _peers.SendToAsync(deviceId, Message.Create("clip_file_offer",
                new { fileId, name = info.Name, size = info.Length, sha256 = hex })))
                throw new IOException("sem conexão com o par.");
            _log($"[file] oferta {info.Name} ({info.Length / 1024} KB)…");
            HashSet<string> who;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await waiters.Task.WaitAsync(timeout.Token);
                await Task.Delay(1500);
                who = _accepts.TryGetValue(fileId, out var set) ? new HashSet<string>(set) : new HashSet<string>();
            }
            catch (OperationCanceledException) { throw new TimeoutException("ninguém aceitou."); }
            if (who.Count == 0) throw new TimeoutException("ninguém aceitou.");
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
            _log($"[file] {info.Name} enviado ({seq} chunks).");
        }
        finally { _acceptWaits.TryRemove(fileId, out _); _accepts.TryRemove(fileId, out _); }
    }

    private void Register()
    {
        _router.Register("clip_file_offer", async (ctx, msg) =>
        {
            if (!ctx.Authenticated || ctx.DeviceId is null) return;
            try
            {
                var p = msg.Payload;
                var fileId = p.TryGetProperty("fileId", out var f) ? f.GetString() ?? "" : "";
                var name = Sanitize(p.TryGetProperty("name", out var n) ? n.GetString() ?? fileId : fileId);
                var size = p.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                var sha = p.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(fileId) || size <= 0 || size > MaxFile) return;
                _incoming[fileId] = new FileAssembler(_store.ClipDir, fileId, name, size, sha);
                await ctx.SendAsync(Message.Create("clip_file_accept", new { fileId, accept = true }));
                _log($"[file] oferta aceita: {name}.");
            }
            catch (Exception ex) { _log($"[file] oferta ignorada: {ex.Message}"); }
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
            if (!ctx.Authenticated) return Task.CompletedTask;
            try
            {
                var p = msg.Payload;
                var fileId = p.TryGetProperty("fileId", out var f) ? f.GetString() ?? "" : "";
                var seq = p.TryGetProperty("seq", out var s) ? s.GetInt32() : -1;
                var data = p.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
                if (!_incoming.TryGetValue(fileId, out var sess)) return Task.CompletedTask;
                if (!sess.Feed(seq, Convert.FromBase64String(data)))
                    _log($"[file] chunk fora de ordem ({fileId}:{seq}).");
            }
            catch (Exception ex) { _log($"[file] chunk ignorado: {ex.Message}"); }
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
                    if (!sess.VerifyAndClose(out _))
                    {
                        _log("[file] corrompido (hash).");
                        sess.Dispose();
                        return Task.CompletedTask;
                    }
                    var dst = UniquePath(Path.Combine(_store.ClipDir, sess.Name));
                    File.Move(sess.PartPath, dst);
                    _store.AddFile(sess.Name, dst, sess.Size, sess.Sha256, ctx.DeviceId, ctx.DeviceId);
                    _notify("Arquivo recebido", $"{sess.Name} de {_peerName(ctx.DeviceId)}.");
                    _log($"[file] salvo: {dst}.");
                }
                finally { try { sess.Dispose(); } catch { } }
            }
            catch (Exception ex) { _log($"[file] done ignorado: {ex.Message}"); }
            return Task.CompletedTask;
        });
    }

    private static string Sanitize(string n)
    {
        var b = (n ?? "").Split('/', '\\').LastOrDefault() ?? "arquivo";
        b = new string(b.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(b) ? "arquivo" : b[..Math.Min(120, b.Length)];
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
                        _log($"[file] sessão expirada ({kv.Key}).");
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
