namespace Rayvia.Models;

public enum RoutingMode
{
    Smart,
    ProxyAll,
    DirectAll
}

public sealed class AppSettings
{
    public bool AutoUpdate { get; set; } = true;
    public RoutingMode RoutingMode { get; set; } = RoutingMode.Smart;
    public string? SelectedNodeId { get; set; }
    public int SocksPort { get; set; } = 10808;
    public int HttpPort { get; set; } = 10809;
    public List<SubscriptionDefinition> Subscriptions { get; set; } = [];
    public List<ProxyNode> Nodes { get; set; } = [];
    public List<RoutingRule> Rules { get; set; } = [];
}

public sealed class SubscriptionDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Подписка";
    public string Url { get; set; } = "";
    public DateTimeOffset? LastUpdated { get; set; }
}

public sealed class ProxyNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Сервер";
    public string Protocol { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string? UserId { get; set; }
    public string? Password { get; set; }
    public string? Cipher { get; set; }
    public int AlterId { get; set; }
    public string Network { get; set; } = "tcp";
    public string Security { get; set; } = "none";
    public string? Sni { get; set; }
    public string? Fingerprint { get; set; }
    public string? PublicKey { get; set; }
    public string? ShortId { get; set; }
    public string? Flow { get; set; }
    public string? Path { get; set; }
    public string? HostHeader { get; set; }
    public string? ServiceName { get; set; }
    public string? SourceSubscriptionId { get; set; }

    public string Display => $"{Name}  ·  {Protocol.ToUpperInvariant()}  ·  {Host}:{Port}";
}

public sealed class RoutingRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Pattern { get; set; } = "";
    public string Action { get; set; } = "proxy";
}

public sealed record UpdateInfo(Version Version, string DownloadUrl, string LocalInstallerPath);
