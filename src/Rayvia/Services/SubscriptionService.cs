using System.Text;
using System.Text.Json;
using Rayvia.Models;

namespace Rayvia.Services;

public sealed class SubscriptionService
{
    private readonly HttpClient _http = new();

    public SubscriptionService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Rayvia/0.1");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<List<ProxyNode>> RefreshAsync(SubscriptionDefinition subscription)
    {
        var source = subscription.Url.Trim();
        var payload = IsNodeLink(source)
            ? source
            : await _http.GetStringAsync(source);
        var normalized = NormalizePayload(payload);
        var nodes = new List<ProxyNode>();

        foreach (var rawLine in normalized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            ProxyNode? node = line switch
            {
                var s when s.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) => ParseVless(s),
                var s when s.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase) => ParseVmess(s),
                var s when s.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase) => ParseTrojan(s),
                var s when s.StartsWith("ss://", StringComparison.OrdinalIgnoreCase) => ParseShadowsocks(s),
                _ => null
            };

            if (node is null)
                continue;

            node.SourceSubscriptionId = subscription.Id;
            nodes.Add(node);
        }

        subscription.LastUpdated = DateTimeOffset.Now;
        return nodes;
    }

    private static bool IsNodeLink(string value)
        => value.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("ss://", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePayload(string payload)
    {
        var trimmed = payload.Trim();

        if (trimmed.Contains("vless://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("vmess://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("trojan://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("ss://", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return TryDecodeBase64(trimmed) ?? trimmed;
    }

    private static ProxyNode? ParseVless(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;

        var query = ParseQuery(uri.Query);
        return new ProxyNode
        {
            Name = GetName(uri, query, "VLESS"),
            Protocol = "vless",
            Host = uri.Host,
            Port = uri.Port,
            UserId = Uri.UnescapeDataString(uri.UserInfo),
            Network = Get(query, "type") ?? "tcp",
            Security = Get(query, "security") ?? "none",
            Sni = Get(query, "sni"),
            Fingerprint = Get(query, "fp"),
            PublicKey = Get(query, "pbk"),
            ShortId = Get(query, "sid"),
            Flow = Get(query, "flow"),
            Path = Get(query, "path"),
            HostHeader = Get(query, "host"),
            ServiceName = Get(query, "serviceName")
        };
    }

    private static ProxyNode? ParseTrojan(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;

        var query = ParseQuery(uri.Query);
        return new ProxyNode
        {
            Name = GetName(uri, query, "Trojan"),
            Protocol = "trojan",
            Host = uri.Host,
            Port = uri.Port,
            Password = Uri.UnescapeDataString(uri.UserInfo),
            Network = Get(query, "type") ?? "tcp",
            Security = Get(query, "security") ?? "tls",
            Sni = Get(query, "sni"),
            Fingerprint = Get(query, "fp"),
            Path = Get(query, "path"),
            HostHeader = Get(query, "host"),
            ServiceName = Get(query, "serviceName")
        };
    }

    private static ProxyNode? ParseVmess(string value)
    {
        try
        {
            var json = TryDecodeBase64(value["vmess://".Length..]);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? Read(string name)
            {
                if (!root.TryGetProperty(name, out var property))
                    return null;
                return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
            }

            if (!int.TryParse(Read("port"), out var port))
                return null;

            int.TryParse(Read("aid"), out var alterId);

            return new ProxyNode
            {
                Name = Read("ps") ?? "VMess",
                Protocol = "vmess",
                Host = Read("add") ?? "",
                Port = port,
                UserId = Read("id"),
                AlterId = alterId,
                Network = Read("net") ?? "tcp",
                Security = string.IsNullOrWhiteSpace(Read("tls")) ? "none" : Read("tls")!,
                Sni = Read("sni"),
                Path = Read("path"),
                HostHeader = Read("host"),
                ServiceName = Read("path")
            };
        }
        catch
        {
            return null;
        }
    }

    private static ProxyNode? ParseShadowsocks(string value)
    {
        try
        {
            var body = value["ss://".Length..];
            var hashIndex = body.IndexOf('#');
            var name = hashIndex >= 0
                ? Uri.UnescapeDataString(body[(hashIndex + 1)..])
                : "Shadowsocks";

            if (hashIndex >= 0)
                body = body[..hashIndex];

            var queryIndex = body.IndexOf('?');
            if (queryIndex >= 0)
                body = body[..queryIndex];

            string credentials;
            string endpoint;

            var at = body.LastIndexOf('@');
            if (at >= 0)
            {
                credentials = body[..at];
                endpoint = body[(at + 1)..];
                if (!credentials.Contains(':'))
                    credentials = TryDecodeBase64(credentials) ?? credentials;
            }
            else
            {
                var decoded = TryDecodeBase64(body);
                if (string.IsNullOrWhiteSpace(decoded))
                    return null;

                var decodedAt = decoded.LastIndexOf('@');
                if (decodedAt < 0)
                    return null;

                credentials = decoded[..decodedAt];
                endpoint = decoded[(decodedAt + 1)..];
            }

            var colon = credentials.IndexOf(':');
            if (colon <= 0)
                return null;

            if (!Uri.TryCreate("tcp://" + endpoint, UriKind.Absolute, out var uri))
                return null;

            return new ProxyNode
            {
                Name = name,
                Protocol = "shadowsocks",
                Host = uri.Host,
                Port = uri.Port,
                Cipher = credentials[..colon],
                Password = Uri.UnescapeDataString(credentials[(colon + 1)..])
            };
        }
        catch
        {
            return null;
        }
    }

    private static string GetName(Uri uri, Dictionary<string, string> query, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(uri.Fragment))
            return Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

        return Get(query, "remarks") ?? fallback;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            result[key] = value;
        }

        return result;
    }

    private static string? Get(Dictionary<string, string> query, string key)
        => query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string? TryDecodeBase64(string value)
    {
        try
        {
            var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
            normalized += normalized.Length % 4 switch
            {
                2 => "==",
                3 => "=",
                _ => ""
            };

            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch
        {
            return null;
        }
    }
}
