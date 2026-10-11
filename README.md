# lumilink-pc — servidor (C# .NET 8 WPF)

Lado PC do LumiLink: conecta, pareia uma vez (QR) e mantém conexão persistente
com o celular. App irmão: `lumilink-android`.

## O que faz (só a base)

- Servidor `wss` (Kestrel) com certificado autoassinado gerado na 1ª execução.
- Pareamento via QR (`ip, porta, token 2min, fingerprint, deviceId`) + pinning manual.
- Reconexão sem QR: desafio `auth` com HMAC-SHA256 do segredo de 32 bytes.
- `ping/pong` 15s (3 faltas = caída), anúncio mDNS `_lumilink._tcp`.
- Perfil `{ deviceId, name, photoHash, photo }` com troca `profile`/`profile_photo*`.
- Bandeja (tray) com tooltip do parceiro, autostart, lista de pareados + "Esquecer".

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
