using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rayvia.Models;

namespace Rayvia.Services;

public static class XrayConfigBuilder
{
    public static string Build(ProxyNode node, AppSettings settings, string accessLogPath, string errorLogPath, bool includeCommunityRuList = true, bool includeRuGeoIp = true)
    {
        if (string.IsNullOrWhiteSpace(node.Host) || node.Port is < 1 or > 65535)
            throw new InvalidOperationException("Xray endpoint некорректен.");
        var rules = new List<object>();
        foreach (var rule in settings.Rules.Where(x => !string.IsNullOrWhiteSpace(x.Pattern)))
        {
            var outbound = rule.Action.ToLowerInvariant() switch { "direct" => "direct", "block" => "block", _ => "proxy" };
            var pattern = rule.Pattern.Trim();
            rules.Add(IsIpRule(pattern)
                ? new Dictionary<string, object?> { ["type"] = "field", ["ip"] = new[] { pattern }, ["outboundTag"] = outbound }
                : new Dictionary<string, object?> { ["type"] = "field", ["domain"] = new[] { HasDomainPrefix(pattern) ? pattern : "domain:" + pattern }, ["outboundTag"] = outbound });
        }

        if (settings.RoutingMode == RoutingMode.Smart)
        {
            rules.Add(new Dictionary<string, object?> { ["type"] = "field", ["ip"] = new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "169.254.0.0/16", "::1/128", "fc00::/7", "fe80::/10" }, ["outboundTag"] = "direct" });
            if (includeRuGeoIp) rules.Add(new Dictionary<string, object?> { ["type"] = "field", ["ip"] = new[] { "geoip:ru" }, ["outboundTag"] = "direct" });
            var domains = new List<string> { "domain:ru", "domain:su", "domain:xn--p1ai" }; if (includeCommunityRuList) domains.Add("geosite:category-ru");
            rules.Add(new Dictionary<string, object?> { ["type"] = "field", ["domain"] = domains, ["outboundTag"] = "direct" });
        }
        else if (settings.RoutingMode == RoutingMode.DirectAll)
        {
            rules.Add(new Dictionary<string, object?> { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = "direct" });
        }

        // The last rule is intentional: Xray must never rely on the first outbound as an implicit default.
        rules.Add(new Dictionary<string, object?> { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = "proxy" });

        var inbounds = new List<object>();
        if (settings.ConnectionMode == ConnectionMode.Tun)
            inbounds.Add(new Dictionary<string, object?> { ["tag"] = "tun-in", ["protocol"] = "tun", ["settings"] = new Dictionary<string, object?> { ["name"] = "Rayvia", ["desc"] = "Rayvia", ["mtu"] = 1500, ["gateway"] = new[] { "10.66.0.1/30", "fd00:66::1/126" }, ["dns"] = new[] { "1.1.1.1", "8.8.8.8", "2606:4700:4700::1111", "2001:4860:4860::8888" }, ["autoSystemRoutingTable"] = new[] { "0.0.0.0/0", "::/0" }, ["autoOutboundsInterface"] = "auto" }, ["sniffing"] = new Dictionary<string, object?> { ["enabled"] = true, ["destOverride"] = new[] { "http", "tls", "quic" }, ["routeOnly"] = true } });
        inbounds.Add(new Dictionary<string, object?> { ["tag"] = "socks-in", ["listen"] = "127.0.0.1", ["port"] = settings.SocksPort, ["protocol"] = "socks", ["settings"] = new Dictionary<string, object?> { ["udp"] = true } });
        inbounds.Add(new Dictionary<string, object?> { ["tag"] = "http-in", ["listen"] = "127.0.0.1", ["port"] = settings.HttpPort, ["protocol"] = "http" });

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?> { ["loglevel"] = "warning", ["access"] = accessLogPath, ["error"] = errorLogPath },
            ["inbounds"] = inbounds,
            ["outbounds"] = new object[] { BuildProxyOutbound(node), new Dictionary<string, object?> { ["tag"] = "direct", ["protocol"] = "freedom" }, new Dictionary<string, object?> { ["tag"] = "block", ["protocol"] = "blackhole" } },
            ["routing"] = new Dictionary<string, object?> { ["domainStrategy"] = "IPIfNonMatch", ["rules"] = rules }
        };
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
    }

    private static Dictionary<string, object?> BuildProxyOutbound(ProxyNode node)
    {
        var outbound = new Dictionary<string, object?> { ["tag"] = "proxy", ["protocol"] = node.Protocol.ToLowerInvariant() };
        switch (node.Protocol.ToLowerInvariant())
        {
            case "vless": outbound["settings"] = new Dictionary<string, object?> { ["vnext"] = new object[] { new Dictionary<string, object?> { ["address"] = node.Host, ["port"] = node.Port, ["users"] = new object[] { new Dictionary<string, object?> { ["id"] = node.UserId, ["encryption"] = "none", ["flow"] = node.Flow } } } } }; outbound["streamSettings"] = BuildStreamSettings(node); break;
            case "vmess": outbound["settings"] = new Dictionary<string, object?> { ["vnext"] = new object[] { new Dictionary<string, object?> { ["address"] = node.Host, ["port"] = node.Port, ["users"] = new object[] { new Dictionary<string, object?> { ["id"] = node.UserId, ["alterId"] = node.AlterId, ["security"] = "auto" } } } } }; outbound["streamSettings"] = BuildStreamSettings(node); break;
            case "trojan": outbound["settings"] = new Dictionary<string, object?> { ["servers"] = new object[] { new Dictionary<string, object?> { ["address"] = node.Host, ["port"] = node.Port, ["password"] = node.Password } } }; outbound["streamSettings"] = BuildStreamSettings(node); break;
            case "shadowsocks": outbound["settings"] = new Dictionary<string, object?> { ["servers"] = new object[] { new Dictionary<string, object?> { ["address"] = node.Host, ["port"] = node.Port, ["method"] = node.Cipher, ["password"] = node.Password } } }; break;
            default: throw new NotSupportedException($"Протокол {node.Protocol} пока не поддерживается.");
        }
        return outbound;
    }

    private static Dictionary<string, object?> BuildStreamSettings(ProxyNode node)
    {
        var stream = new Dictionary<string, object?> { ["network"] = string.IsNullOrWhiteSpace(node.Network) ? "tcp" : node.Network, ["security"] = string.IsNullOrWhiteSpace(node.Security) ? "none" : node.Security };
        if (node.Security.Equals("reality", StringComparison.OrdinalIgnoreCase)) stream["realitySettings"] = new Dictionary<string, object?> { ["serverName"] = node.Sni, ["fingerprint"] = node.Fingerprint ?? "chrome", ["publicKey"] = node.PublicKey, ["shortId"] = node.ShortId, ["spiderX"] = "/" };
        else if (node.Security.Equals("tls", StringComparison.OrdinalIgnoreCase)) stream["tlsSettings"] = new Dictionary<string, object?> { ["serverName"] = node.Sni ?? node.Host, ["fingerprint"] = node.Fingerprint };
        if (node.Network.Equals("ws", StringComparison.OrdinalIgnoreCase)) stream["wsSettings"] = new Dictionary<string, object?> { ["path"] = node.Path ?? "/", ["headers"] = string.IsNullOrWhiteSpace(node.HostHeader) ? null : new Dictionary<string, string> { ["Host"] = node.HostHeader } };
        else if (node.Network.Equals("grpc", StringComparison.OrdinalIgnoreCase)) stream["grpcSettings"] = new Dictionary<string, object?> { ["serviceName"] = node.ServiceName ?? "" };
        return stream;
    }

    private static bool HasDomainPrefix(string value) => value.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("full:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("ext:", StringComparison.OrdinalIgnoreCase);
    private static bool IsIpRule(string value) => value.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(value.Split('/', 2)[0], out _);
}
