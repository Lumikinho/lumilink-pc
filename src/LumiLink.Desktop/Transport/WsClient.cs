using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LumiLink.Desktop.Messaging;
using Message = LumiLink.Desktop.Messaging.Message;

namespace LumiLink.Desktop.Transport;

// Cliente wss (lado discador: PC→PC, PC→Android). Pinning pelo fingerprint do par;
// expectedFp null = TOFU (primeiro pareamento manual), com aviso em log.
public sealed class WsClient : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private readonly Action<string> _log;
    private CancellationTokenSource? _cts;

    public event Func<string, Task>? OnText;
    public event Func<Task>? OnClosed;
    public string? CapturedFingerprint { get; private set; }

    public WsClient(Action<string> log) => _log = log;
    public bool IsOpen => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string ip, int port, string? expectedFp, CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        _ws.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
        {
            if (cert is null) return false;
            var fp = CertManager.Sha256Hex(cert.GetRawCertData());
            CapturedFingerprint = fp;
            if (string.IsNullOrEmpty(expectedFp))
            {
                _log($"[tls] TOFU: confiando no fingerprint {fp[..12]}… (manual, 1ª vez)");
                return true;
            }
            var ok = fp.Equals(expectedFp, StringComparison.OrdinalIgnoreCase);
            if (!ok) _log("[tls] pinning FALHOU — fingerprint não confere.");
            return ok;
        };
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await _ws.ConnectAsync(new Uri($"wss://{ip}:{port}/ws"), ct);
        _ = ReceiveLoopAsync(_cts.Token);
    }

    public async Task SendAsync(Message msg)
    {
        if (_ws is null) throw new InvalidOperationException("sem conexão");
        var bytes = Encoding.UTF8.GetBytes(msg.ToJson());
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null) return;
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                if (res.EndOfMessage)
                {
                    var text = sb.ToString(); sb.Clear();
                    if (OnText is not null) await OnText(text);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[net] cliente caiu: {ex.Message}"); }
        finally { if (OnClosed is not null) await OnClosed(); }
    }

    public async Task CloseAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try
        {
            // Handshake de close com teto: par morto não pode travar (era minutos).
            if (_ws?.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch { }
        try { _ws?.Dispose(); } catch { }
        _ws = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
