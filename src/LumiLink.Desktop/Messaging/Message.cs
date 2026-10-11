using System.Text.Json;
using System.Text.Json.Serialization;

namespace LumiLink.Desktop.Messaging;

// Formato único de mensagem: { "type": "...", "id": "...", "payload": { } }
public sealed record Message(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("payload")] JsonElement Payload)
{
    public static Message Create(string type, object? payload = null)
    {
        var el = payload is null
            ? JsonDocument.Parse("{}").RootElement.Clone()
            : JsonSerializer.SerializeToElement(payload);
        return new Message(type, Guid.NewGuid().ToString("N"), el);
    }

    public T PayloadAs<T>() => Payload.Deserialize<T>(PayloadOptions)!;
    public static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };
    public string ToJson() => JsonSerializer.Serialize(this);
    public static Message? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<Message>(json); }
        catch { return null; }
    }
}

// Payloads tipados (evita magic strings espalhadas)
public sealed record HelloPayload(string? Token, string? Code, string DeviceId, string DeviceName, ProfilePayload? Profile, string? Fingerprint, int Port, List<string>? Ips);
public sealed record ProfilePayload(string Name, string PhotoHash);
public sealed record AuthChallengePayload(string Nonce);
public sealed record AuthResponsePayload(string DeviceId, string Hmac, string? Nonce);
public sealed record AuthOkPayload(string? Secret = null, string? DeviceId = null, ProfilePayload? Profile = null, string? Hmac = null);
public sealed record ProfilePhotoPayload(string PhotoHash, string Mime, string DataBase64);
public sealed record ByePayload(string Reason);
