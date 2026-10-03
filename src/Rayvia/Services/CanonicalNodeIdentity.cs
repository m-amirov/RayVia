using System.Net;
using System.Security.Cryptography;
using System.Text;
using Rayvia.Models;

namespace Rayvia.Services;

public static class CanonicalNodeIdentity
{
    public static string Build(ProxyNode node)
    {
        var fields = new[]
        {
            node.Protocol.Trim().ToLowerInvariant(), CanonicalNodeIdentity.NormalizeHost(node.Host),
            node.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), node.UserId?.Trim().ToLowerInvariant() ?? "",
            node.Password ?? "", node.Cipher?.Trim().ToLowerInvariant() ?? "", node.AlterId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            node.Network.Trim().ToLowerInvariant(), node.Security.Trim().ToLowerInvariant(), node.Sni?.Trim().ToLowerInvariant() ?? "",
            node.Fingerprint?.Trim().ToLowerInvariant() ?? "", node.PublicKey?.Trim() ?? "", node.ShortId?.Trim().ToLowerInvariant() ?? "",
            node.Flow?.Trim().ToLowerInvariant() ?? "", node.Path?.Trim() ?? "", node.HostHeader?.Trim().ToLowerInvariant() ?? "", node.ServiceName?.Trim() ?? ""
        };
        return string.Join("\n", fields);
    }

    public static string CreateId(ProxyNode node)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Build(node)))).ToLowerInvariant()[..24];

    public static string NormalizeHost(string host)
    {
        var value = host.Trim().Trim('[', ']');
        return IPAddress.TryParse(value, out var address) ? address.ToString().ToLowerInvariant() : value.TrimEnd('.').ToLowerInvariant();
    }
}
