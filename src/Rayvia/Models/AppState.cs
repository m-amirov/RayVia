namespace Rayvia.Models;

public enum RoutingMode
{
    Smart,
    ProxyAll,
    DirectAll
}

public enum ConnectionMode
{
    SystemProxy,
    Tun
}

public sealed class AppSettings
{
    public bool AutoUpdate { get; set; } = true;
    public bool AutoSelectBestServer { get; set; }
    public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.SystemProxy;
    public RoutingMode RoutingMode { get; set; } = RoutingMode.Smart;
    public string? SelectedNodeId { get; set; }
    public string? SelectedGroupId { get; set; }
    public int SocksPort { get; set; } = 10808;
    public int HttpPort { get; set; } = 10809;
    public List<SubscriptionDefinition> Subscriptions { get; set; } = [];
    public List<ProxyNode> Nodes { get; set; } = [];
    public List<ServerGroup> Groups { get; set; } = [];
    public List<RoutingRule> Rules { get; set; } = [];
}

public sealed class SubscriptionDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Подписка";
    public string Url { get; set; } = "";
    public DateTimeOffset? LastUpdated { get; set; }

    public string UpdatedDisplay => LastUpdated is null
        ? "Не обновлялась"
        : $"Обновлено {LastUpdated.Value:dd.MM HH:mm}";
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
    public int? LatencyMs { get; set; }
    public DateTimeOffset? LatencyCheckedAt { get; set; }

    public string Endpoint => $"{Host}:{Port}";
    public string LatencyDisplay => LatencyMs is int value ? $"{value} ms" : "—";
    public string Display => $"{Name}  ·  {Protocol.ToUpperInvariant()}  ·  {Endpoint}  ·  {LatencyDisplay}";
}

public sealed class ServerGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Группа";
    public List<string> NodeIds { get; set; } = [];

    public string Display => $"{Name} ({NodeIds.Count})";
}

public sealed class RoutingRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Pattern { get; set; } = "";
    public string Action { get; set; } = "proxy";

    public string ActionDisplay => Action.ToLowerInvariant() switch
    {
        "direct" => "Напрямую",
        "block" => "Блокировать",
        _ => "Через прокси"
    };
}

public sealed record UpdateInfo(Version Version, string DownloadUrl, string LocalInstallerPath);

public sealed record RoutingInspectionResult(
    string Target,
    string Resolved,
    string Route,
    string Rule,
    string Note);

public sealed class ConnectionEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
    public string Route { get; init; } = "";
    public string Raw { get; init; } = "";

    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");
    public string RouteDisplay => Route.ToLowerInvariant() switch
    {
        "direct" => "DIRECT",
        "block" => "BLOCK",
        "proxy" => "PROXY",
        _ => Route.ToUpperInvariant()
    };
}
