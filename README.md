# lumilink-pc — nó P2P (C# .NET 8 WPF)

Nó simétrico do LumiLink: **servidor (wss + mDNS) e cliente** ao mesmo tempo.
Conversa com PC, Android ou ambos. App irmão: `lumilink-android`.
Protocolo: `PROTOCOL.md` (idêntico nas 3 combinações).

## O que faz (só a base)

- Servidor `wss` (Kestrel, porta aleatória persistida) + cliente `ClientWebSocket` com pinning.
- Anuncia e descobre `_lumilink._tcp` (mDNS); só o menor `deviceId` disca (regra 10 s).
- Pareamento via QR **ou manual** (IP:porta + código 6 dígitos); auth mútua HMAC.
- Vários pares: uma conexão por par, backoff independente, rate limit.
- Perfil com troca `profile`/`profile_photo*`; `profile_update` ao salvar.
- Tela principal: lista de pareados (foto, nome, estado) + Parear / Manual / Atualizar;
  detalhe do par (último IP, última conexão, Reconectar agora, Esquecer).

## Protocolo (resumo)

JSON `{ "type", "id", "payload" }`. Tipos: `hello`, `auth`, `auth_ok`,
`ping`, `pong`, `bye`, `profile`, `profile_photo_request`, `profile_photo`,
`profile_update`. Detalhe completo no `README.md` da pasta anterior
(ou veja o `MessageRouter`, que é o ponto de extensão).

## Como compilar e rodar

Pré-requisito: [.NET 8 SDK](https://aka.ms/dotnet-download).

```powershell
dotnet restore
dotnet run --project src\LumiLink.Desktop
```

1ª execução: gera `%AppData%\LumiLink\server.pfx`, mostra o fingerprint no log
e pede regra no Firewall do Windows (porta TCP `52431`).

## Estrutura

```
src/LumiLink.Desktop/
  Transport/   CertManager.cs, SecureWebSocketServer.cs (Kestrel + wss)
  Pairing/     PairingManager.cs (token 2min, QR JSON, nonce/HMAC)
  Storage/     PairStore.cs (pairs.json + DPAPI)
  Profile/     ProfileStore.cs, ImageUtils.cs (avatar 256x256 ~50KB)
  Messaging/   Message.cs, MessageRouter.cs (+ EchoExample)
  Core/        ConnectionManager.cs, MdnsAdvertiser.cs
  UI/          TrayIconManager.cs
  MainWindow.*, ProfileWindow.*, App.*
```

## Registrar função nova (exemplo "echo")

```csharp
router.Register("echo", async (ctx, msg) =>
{
    if (!ctx.Authenticated) return;
    var text = msg.Payload.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
    await ctx.SendAsync(Message.Create("echo_response", new { text }));
});
```

Sem tocar em Transport/Pairing/Storage.

## Persistência

`%AppData%\LumiLink\`: `pairs.json` (segredo via DPAPI), `profile.json` +
`profile.png`, `peers/<id>.json` + `.png`, `device.id`, `server.pfx`.
