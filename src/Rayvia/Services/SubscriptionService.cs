using System.Net.Http;
using System.Text;
using System.Text.Json;
using Rayvia.Models;

namespace Rayvia.Services;

public sealed class SubscriptionParseException(string message) : FormatException(message);

public static class SubscriptionParser
{
    private static readonly HashSet<string> Networks = new(StringComparer.OrdinalIgnoreCase) { "tcp", "raw", "kcp", "ws", "http", "grpc", "quic", "httpupgrade", "xhttp", "splithttp" };
    private static readonly HashSet<string> Security = new(StringComparer.OrdinalIgnoreCase) { "none", "tls", "reality" };
    private static readonly HashSet<string> Ciphers = new(StringComparer.OrdinalIgnoreCase) { "aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305" };

    public static ProxyNode Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new SubscriptionParseException("Пустая ссылка сервера.");
        if (value.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return ParseVless(value);
        if (value.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return ParseVmess(value);
        if (value.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) return ParseTrojan(value);
        if (value.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) return ParseShadowsocks(value);
        throw new SubscriptionParseException("Неизвестный тип transport/protocol: ссылка отклонена.");
    }

    private static ProxyNode ParseVless(string value)
    {
        var uri = ParseUri(value, "vless"); var query = ParseQuery(uri.Query); var id = Uri.UnescapeDataString(uri.UserInfo);
        RequireUuid(id, "VLESS UUID"); ValidateEndpoint(uri.Host, uri.Port);
        var network = Get(query, "type") ?? "tcp"; var security = Get(query, "security") ?? "none"; ValidateTransport(network, security);
        if (security.Equals("reality", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Get(query, "pbk"))) throw new SubscriptionParseException("VLESS Reality требует public key (pbk).");
        return new ProxyNode { Name = GetName(uri, query, "VLESS"), Protocol = "vless", Host = NormalizeHost(uri.Host), Port = uri.Port, UserId = id, Network = network, Security = security, Sni = Get(query, "sni"), Fingerprint = Get(query, "fp"), PublicKey = Get(query, "pbk"), ShortId = Get(query, "sid"), Flow = Get(query, "flow"), Path = Get(query, "path"), HostHeader = Get(query, "host"), ServiceName = Get(query, "serviceName") };
    }

    private static ProxyNode ParseTrojan(string value)
    {
        var uri = ParseUri(value, "trojan"); var query = ParseQuery(uri.Query); var password = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrWhiteSpace(password)) throw new SubscriptionParseException("Trojan password обязателен."); ValidateEndpoint(uri.Host, uri.Port);
        var network = Get(query, "type") ?? "tcp"; var security = Get(query, "security") ?? "tls"; ValidateTransport(network, security);
        return new ProxyNode { Name = GetName(uri, query, "Trojan"), Protocol = "trojan", Host = NormalizeHost(uri.Host), Port = uri.Port, Password = password, Network = network, Security = security, Sni = Get(query, "sni"), Fingerprint = Get(query, "fp"), Path = Get(query, "path"), HostHeader = Get(query, "host"), ServiceName = Get(query, "serviceName") };
    }

    private static ProxyNode ParseVmess(string value)
    {
        var json = DecodeBase64(value["vmess://".Length..], "VMess base64");
        try
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            string Read(string name, bool required = false)
            {
                if (!root.TryGetProperty(name, out var property)) { if (required) throw new SubscriptionParseException($"VMess поле {name} обязательно."); return ""; }
                var result = property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : property.ToString();
                if (required && string.IsNullOrWhiteSpace(result)) throw new SubscriptionParseException($"VMess поле {name} обязательно."); return result;
            }
            var host = Read("add", true); if (!int.TryParse(Read("port", true), out var port)) throw new SubscriptionParseException("VMess port должен быть числом.");
            ValidateEndpoint(host, port); var id = Read("id", true); RequireUuid(id, "VMess UUID");
            var network = string.IsNullOrWhiteSpace(Read("net")) ? "tcp" : Read("net"); var tls = Read("tls"); var security = string.IsNullOrWhiteSpace(tls) ? "none" : tls; ValidateTransport(network, security);
            int.TryParse(Read("aid"), out var alterId);
            return new ProxyNode { Name = string.IsNullOrWhiteSpace(Read("ps")) ? "VMess" : Read("ps"), Protocol = "vmess", Host = NormalizeHost(host), Port = port, UserId = id, AlterId = alterId, Network = network, Security = security, Sni = NullIfEmpty(Read("sni")), Path = NullIfEmpty(Read("path")), HostHeader = NullIfEmpty(Read("host")), ServiceName = NullIfEmpty(Read("path")) };
        }
        catch (JsonException ex) { throw new SubscriptionParseException("VMess JSON повреждён: " + ex.Message); }
    }

    private static ProxyNode ParseShadowsocks(string value)
    {
        var body = value["ss://".Length..]; var hash = body.IndexOf('#'); var name = hash >= 0 ? Uri.UnescapeDataString(body[(hash + 1)..]) : "Shadowsocks"; if (hash >= 0) body = body[..hash];
        var queryIndex = body.IndexOf('?'); var query = queryIndex >= 0 ? ParseQuery(body[queryIndex..]) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); if (queryIndex >= 0) body = body[..queryIndex];
        if (query.TryGetValue("plugin", out var plugin) && !string.IsNullOrWhiteSpace(plugin)) throw new SubscriptionParseException("Shadowsocks plugin не поддерживается и ссылка отклонена.");
        string credentials; string endpoint; var at = body.LastIndexOf('@');
        if (at >= 0) { credentials = body[..at]; endpoint = body[(at + 1)..]; if (!credentials.Contains(':')) credentials = DecodeBase64(credentials, "Shadowsocks credentials"); }
        else { var decoded = DecodeBase64(body, "Shadowsocks link"); var decodedAt = decoded.LastIndexOf('@'); if (decodedAt < 0) throw new SubscriptionParseException("Shadowsocks endpoint отсутствует."); credentials = decoded[..decodedAt]; endpoint = decoded[(decodedAt + 1)..]; }
        var colon = credentials.IndexOf(':'); if (colon <= 0 || colon == credentials.Length - 1) throw new SubscriptionParseException("Shadowsocks cipher/password обязательны.");
        if (!Ciphers.Contains(credentials[..colon])) throw new SubscriptionParseException("Shadowsocks cipher не входит в поддерживаемый whitelist.");
        if (!Uri.TryCreate("tcp://" + endpoint, UriKind.Absolute, out var uri)) throw new SubscriptionParseException("Shadowsocks endpoint некорректен."); ValidateEndpoint(uri.Host, uri.Port);
        return new ProxyNode { Name = string.IsNullOrWhiteSpace(name) ? "Shadowsocks" : name, Protocol = "shadowsocks", Host = NormalizeHost(uri.Host), Port = uri.Port, Cipher = credentials[..colon], Password = Uri.UnescapeDataString(credentials[(colon + 1)..]) };
    }

    private static Uri ParseUri(string value, string scheme) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase) ? uri : throw new SubscriptionParseException($"Некорректная {scheme} ссылка.");
    private static void ValidateEndpoint(string host, int port) { if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace)) throw new SubscriptionParseException("Host обязателен."); if (port is < 1 or > 65535) throw new SubscriptionParseException("Port должен быть в диапазоне 1..65535."); }
    private static void ValidateTransport(string network, string security) { if (!Networks.Contains(network)) throw new SubscriptionParseException($"Неизвестный transport type: {network}."); if (!Security.Contains(security)) throw new SubscriptionParseException($"Неизвестный security type: {security}."); }
    private static void RequireUuid(string value, string field) { if (!Guid.TryParse(value, out _)) throw new SubscriptionParseException($"{field} имеет некорректный формат."); }
    private static string GetName(Uri uri, Dictionary<string, string> query, string fallback) => !string.IsNullOrWhiteSpace(uri.Fragment) ? Uri.UnescapeDataString(uri.Fragment.TrimStart('#')) : Get(query, "remarks") ?? fallback;
    private static Dictionary<string, string> ParseQuery(string query) { var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) { var parts = pair.Split('=', 2); result[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : ""; } return result; }
    private static string? Get(Dictionary<string, string> query, string key) => query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string DecodeBase64(string value, string label) { try { var normalized = value.Trim().Replace('-', '+').Replace('_', '/'); normalized += (normalized.Length % 4) switch { 2 => "==", 3 => "=", _ => "" }; return Encoding.UTF8.GetString(Convert.FromBase64String(normalized)); } catch (Exception ex) { throw new SubscriptionParseException($"{label}: malformed base64 ({ex.Message})."); } }
    private static string NormalizeHost(string host) => CanonicalNodeIdentity.NormalizeHost(host);
}

public sealed class SubscriptionService
{
    private readonly HttpClient _http;
    public SubscriptionService(HttpClient? http = null) { _http = http ?? new HttpClient(); _http.DefaultRequestHeaders.UserAgent.ParseAdd("Rayvia/0.4"); _http.Timeout = TimeSpan.FromSeconds(20); }

    public async Task<List<ProxyNode>> RefreshAsync(SubscriptionDefinition subscription)
    {
        var source = subscription.Url.Trim(); var payload = IsNodeLink(source) ? source : await _http.GetStringAsync(source); var normalized = NormalizePayload(payload); var nodes = new List<ProxyNode>();
        foreach (var rawLine in normalized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) { var line = rawLine.Trim(); if (!IsNodeLink(line)) continue; var node = SubscriptionParser.Parse(line); node.Id = CanonicalNodeIdentity.CreateId(node); node.SourceSubscriptionId = subscription.Id; nodes.Add(node); }
        subscription.LastUpdated = DateTimeOffset.Now; return nodes.GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First()).ToList();
    }

    private static bool IsNodeLink(string value) => value.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("ss://", StringComparison.OrdinalIgnoreCase);
    private static string NormalizePayload(string payload) { var trimmed = payload.Trim(); if (trimmed.Contains("vless://", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("vmess://", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("trojan://", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("ss://", StringComparison.OrdinalIgnoreCase)) return trimmed; try { var normalized = trimmed.Replace('-', '+').Replace('_', '/'); normalized += (normalized.Length % 4) switch { 2 => "==", 3 => "=", _ => "" }; return Encoding.UTF8.GetString(Convert.FromBase64String(normalized)); } catch { return trimmed; } }
}
