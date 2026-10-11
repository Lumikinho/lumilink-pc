using System.Net.WebSockets;
using System.Text;

namespace LumiLink.Desktop.Messaging;

// Roteador por `type`: registrar handlers novos sem mexer na base.
// Exemplo no final deste arquivo (EchoHandler).
public sealed class MessageRouter
{
    private readonly Dictionary<string, Func<MessageContext, Message, Task>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;

    public MessageRouter(Action<string> log) => _log = log;

    public void Register(string type, Func<MessageContext, Message, Task> handler)
        => _handlers[type] = handler;

    public async Task RouteAsync(MessageContext ctx, Message msg)
    {
        if (_handlers.TryGetValue(msg.Type, out var h))
        {
            try { await h(ctx, msg); }
            catch (Exception ex) { _log($"[router] handler '{msg.Type}' falhou: {ex.Message}"); }
        }
        else
        {
            _log($"[router] tipo desconhecido '{msg.Type}' ignorado.");
        }
    }
}

public sealed class MessageContext
{
    public WebSocket Socket { get; }
    public string? DeviceId { get; set; }
    public bool Authenticated { get; set; }
    public Func<Message, Task> SendAsync { get; }

    public MessageContext(WebSocket socket, Func<Message, Task> send)
    { Socket = socket; SendAsync = send; }
}

// ---------------------------------------------------------------------------
// EXEMPLO COMENTADO: como registrar uma função nova ("echo").
// Em ConnectionManager (ou App startup), após criar o router:
//
//   router.Register("echo", async (ctx, msg) =>
//   {
//       // 1. Só responde se já autenticado (boa prática p/ funções novas).
//       if (!ctx.Authenticated) return;
//       // 2. Lê o payload: { "text": "oi" }
//       var text = msg.Payload.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
//       Log($"[echo] de {ctx.DeviceId}: {text}");
//       // 3. Responde com o mesmo tipo ou "echo_response".
//       await ctx.SendAsync(Message.Create("echo_response", new { text }));
//   });
//
// No Android, o espelho é: router.register("echo_response") { ... }.
// Pronto: sem tocar em Transport/Pairing/Storage.
// ---------------------------------------------------------------------------
public static class EchoExample
{
    public static void Register(MessageRouter router, Action<string> log)
    {
        router.Register("echo", async (ctx, msg) =>
        {
            if (!ctx.Authenticated) return;
            var text = msg.Payload.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            log($"[echo] de {ctx.DeviceId}: {text}");
            await ctx.SendAsync(Message.Create("echo_response", new { text }));
        });
    }
}
