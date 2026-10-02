using System.Net;
using System.Text.RegularExpressions;
using Rayvia.Models;

namespace Rayvia.Services;

public sealed class RoutingInspectorService
{
    public async Task<RoutingInspectionResult> InspectAsync(
        string input,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var host = NormalizeTarget(input);
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Укажите домен или IP-адрес.");

        var addresses = await ResolveAsync(host, cancellationToken);
        var resolved = addresses.Count == 0
            ? "Не разрешён"
            : string.Join(", ", addresses.Select(x => x.ToString()));

        foreach (var rule in settings.Rules)
        {
            if (!Matches(rule.Pattern, host, addresses))
                continue;

            return new RoutingInspectionResult(
                host,
                resolved,
                RouteLabel(rule.Action),
                rule.Pattern,
                "Совпало пользовательское правило.");
        }

        if (settings.RoutingMode == RoutingMode.DirectAll)
        {
            return new RoutingInspectionResult(
                host,
                resolved,
                "DIRECT",
                "Режим «Всё напрямую»",
                "Пользовательские правила проверяются раньше общего режима.");
        }

        if (settings.RoutingMode == RoutingMode.ProxyAll)
        {
            return new RoutingInspectionResult(
                host,
                resolved,
                "PROXY",
                "Режим «Всё через прокси»",
                "Пользовательские правила проверяются раньше общего режима.");
        }

        if (addresses.Any(IsPrivateAddress))
        {
            return new RoutingInspectionResult(
                host,
                resolved,
                "DIRECT",
                "geoip:private",
                "Локальные и частные сети идут напрямую.");
        }

        if (LooksRussianDomain(host))
        {
            return new RoutingInspectionResult(
                host,
                resolved,
                "DIRECT",
                "Российская доменная зона",
                "Домены .ru, .su и .рф идут напрямую независимо от GeoSite.");
        }

        return new RoutingInspectionResult(
            host,
            resolved,
            "PROXY",
            "Маршрут по умолчанию",
            "Xray дополнительно проверит geosite:category-ru и geoip:ru. Если GeoSite недоступен, Rayvia автоматически использует доменный fallback.");
    }

    private static async Task<List<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var direct))
            return [direct];

        try
        {
            var result = await Dns.GetHostAddressesAsync(host, cancellationToken);
            return result.Distinct().ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool Matches(string pattern, string host, IReadOnlyCollection<IPAddress> addresses)
    {
        var value = pattern.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.StartsWith("full:", StringComparison.OrdinalIgnoreCase))
            return string.Equals(host, value[5..], StringComparison.OrdinalIgnoreCase);

        if (value.StartsWith("domain:", StringComparison.OrdinalIgnoreCase))
            return DomainMatches(host, value[7..]);

        if (value.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Regex.IsMatch(
                    host,
                    value[7..],
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch
            {
                return false;
            }
        }

        if (value.Equals("geosite:ru", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("geosite:category-ru", StringComparison.OrdinalIgnoreCase))
            return LooksRussianDomain(host);

        if (value.Equals("geoip:private", StringComparison.OrdinalIgnoreCase))
            return addresses.Any(IsPrivateAddress);

        if (value.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
            return false;

        if (TryParseCidr(value, out var network, out var prefix))
            return addresses.Any(ip => IsInCidr(ip, network, prefix));

        if (IPAddress.TryParse(value, out var address))
            return addresses.Contains(address);

        return DomainMatches(host, value);
    }

    private static bool DomainMatches(string host, string domain)
    {
        var normalized = domain.Trim().TrimStart('.').TrimEnd('.');
        return string.Equals(host, normalized, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksRussianDomain(string host)
        => host.EndsWith(".ru", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".su", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".рф", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".xn--p1ai", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                   || bytes[0] == 127
                   || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.IsIPv6LinkLocal
               || address.IsIPv6SiteLocal
               || address.Equals(IPAddress.IPv6Loopback)
               || (bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC);
    }

    private static string NormalizeTarget(string input)
    {
        var value = input.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host.TrimEnd('.');

        if (value.StartsWith("[", StringComparison.Ordinal) && value.Contains(']'))
            return value[1..value.IndexOf(']')];

        var colonCount = value.Count(x => x == ':');
        if (colonCount == 1)
        {
            var colon = value.LastIndexOf(':');
            if (colon > 0 && int.TryParse(value[(colon + 1)..], out _))
                value = value[..colon];
        }

        return value.Trim('[', ']').TrimEnd('.');
    }

    private static string RouteLabel(string action)
        => action.ToLowerInvariant() switch
        {
            "direct" => "DIRECT",
            "block" => "BLOCK",
            _ => "PROXY"
        };

    private static bool TryParseCidr(string value, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;

        var parts = value.Split('/', 2);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var parsed) ||
            !int.TryParse(parts[1], out prefix))
            return false;

        var max = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? 32
            : 128;

        if (prefix is < 0 || prefix > max)
            return false;

        network = parsed;
        return true;
    }

    private static bool IsInCidr(IPAddress address, IPAddress network, int prefix)
    {
        if (address.AddressFamily != network.AddressFamily)
            return false;

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
                return false;
        }

        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }
}
