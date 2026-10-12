using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LumiLink.Desktop.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Message = LumiLink.Desktop.Messaging.Message;

namespace LumiLink.Desktop.Transport;

// Servidor wss via Kestrel. Um único endpoint /ws. Mantém referência do socket ativo.
public sealed class SecureWebSocketServer
{
    private readonly X509Certificate2 _cert;
    private readonly int _port;
    private readonly Action<string> _log;
    private WebApplication? _app;

    public event Func<MessageContext, Message, Task>? OnMessage;
    public event Func<MessageContext, Task>? OnConnected;
    public event Func<string?, Task>? OnDisconnected;

    public SecureWebSocketServer(X509Certificate2 cert, int port, Action<string> log)
    { _cert = cert; _port = port; _log = log; }

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Any, _port, lo => lo.UseHttps(_cert));
        });
        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        _app.Map("/ws", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest) { http.Response.StatusCode = 400; return; }
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            var ctx = new MessageContext(ws, async m =>
            {
                var json = m.ToJson();
                var bytes = Encoding.UTF8.GetBytes(json);
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            });
            ctx.RemoteIp = http.Connection.RemoteIpAddress?.ToString();
            _log("[net] par conectou (tcp/ws).");
            if (OnConnected is not null) await OnConnected(ctx);
            await ReceiveLoopAsync(ctx);
            if (OnDisconnected is not null) await OnDisconnected(ctx.DeviceId);
        });

        _log($"[net] wss ouvindo em 0.0.0.0:{_port}/ws");
        await _app.RunAsync(ct);
    }

    private async Task ReceiveLoopAsync(MessageContext ctx)
    {
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (ctx.Socket?.State == WebSocketState.Open)
            {
                var res = await ctx.Socket!.ReceiveAsync(buf, CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                if (res.EndOfMessage)
                {
                    var text = sb.ToString(); sb.Clear();
                    var msg = Message.FromJson(text);
                    if (msg is not null && OnMessage is not null)
                        await OnMessage(ctx, msg);
                }
            }
        }
        catch (Exception ex) { _log($"[net] loop caiu: {ex.Message}"); }
        finally
        {
            try
            {
                if (ctx.Socket?.State == WebSocketState.Open)
                    await ctx.Socket!.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
            _log("[net] socket encerrado.");
        }
    }

    public void EnsureFirewallRule(int port)
    {
        // Leitura não pede admin: só eleva (UAC) se a regra estiver faltando.
        try
        {
            if (RuleAllows(port))
            {
                _log("[fw] regra ok, sem UAC.");
                return;
            }
            // Nome estável "LumiLink" (antes criava uma por porta): apaga restos e cria uma só.
            var args = "/c netsh advfirewall firewall delete rule name=\"LumiLink\""
                + " & netsh advfirewall firewall delete rule name=\"LumiLink 52431\""
                + $" & netsh advfirewall firewall add rule name=\"LumiLink\" dir=in action=allow protocol=TCP localport={port}";
            var psi = new System.Diagnostics.ProcessStartInfo("cmd", args)
            { Verb = "runas", UseShellExecute = true, CreateNoWindow = true };
            System.Diagnostics.Process.Start(psi);
            _log("[fw] regra de firewall solicitada (aceite o UAC uma vez).");
        }
        catch (Exception ex)
        {
            _log($"[fw] AVISO: libere a porta TCP {port} no Firewall do Windows. Detalhe: {ex.Message}");
        }
    }

    private static bool RuleAllows(int port)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return false;
            dynamic policy = System.Activator.CreateInstance(type)!;
            foreach (dynamic r in policy.Rules)
            {
                try
                {
                    // IN=1, ALLOW=1, TCP=6
                    if ((string)r.Name == "LumiLink" && (bool)r.Enabled
                        && (int)r.Direction == 1 && (int)r.Action == 1 && (int)r.Protocol == 6
                        && ((string)r.LocalPorts).Split(',').Any(p => p.Trim() == port.ToString()))
                        return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }
}
