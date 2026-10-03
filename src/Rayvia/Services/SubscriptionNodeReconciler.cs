using Rayvia.Models;

namespace Rayvia.Services;

public static class SubscriptionNodeReconciler
{
    public static void ApplyRefresh(AppSettings settings, string subscriptionId, IReadOnlyCollection<ProxyNode> refreshedNodes)
    {
        var refreshedByCanonical = refreshedNodes
            .GroupBy(CanonicalNodeIdentity.Build, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(node => node.Id, StringComparer.Ordinal).First(), StringComparer.Ordinal);
        var existingByCanonical = settings.Nodes
            .GroupBy(CanonicalNodeIdentity.Build, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var allKeys = existingByCanonical.Keys.Concat(refreshedByCanonical.Keys).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        Rebuild(settings, subscriptionId, refreshedByCanonical, existingByCanonical, allKeys);
    }

    public static void RemoveSubscription(AppSettings settings, string subscriptionId)
    {
        var existingByCanonical = settings.Nodes
            .GroupBy(CanonicalNodeIdentity.Build, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        Rebuild(settings, subscriptionId, new Dictionary<string, ProxyNode>(StringComparer.Ordinal), existingByCanonical, existingByCanonical.Keys.OrderBy(x => x, StringComparer.Ordinal));
    }

    private static void Rebuild(
        AppSettings settings,
        string subscriptionId,
        IReadOnlyDictionary<string, ProxyNode> refreshedByCanonical,
        IReadOnlyDictionary<string, List<ProxyNode>> existingByCanonical,
        IEnumerable<string> canonicalKeys)
    {
        var idMigration = new Dictionary<string, string?>(StringComparer.Ordinal);
        var rebuilt = new List<ProxyNode>();

        foreach (var canonical in canonicalKeys)
        {
            existingByCanonical.TryGetValue(canonical, out var oldNodes);
            refreshedByCanonical.TryGetValue(canonical, out var refreshed);
            oldNodes ??= [];

            var memberships = oldNodes
                .SelectMany(GetSubscriptionIds)
                .ToHashSet(StringComparer.Ordinal);
            var wasInSubscription = memberships.Remove(subscriptionId);
            if (refreshed is not null)
                memberships.Add(subscriptionId);

            if (refreshed is null && wasInSubscription && memberships.Count == 0)
            {
                foreach (var old in oldNodes)
                    idMigration[old.Id] = null;
                continue;
            }

            var node = refreshed ?? oldNodes.FirstOrDefault();
            if (node is null)
                continue;

            if (refreshed is not null)
            {
                var previous = oldNodes.FirstOrDefault(x => x.LatencyMs is not null);
                if (refreshed.LatencyMs is null && previous is not null)
                {
                    refreshed.LatencyMs = previous.LatencyMs;
                    refreshed.LatencyCheckedAt = previous.LatencyCheckedAt;
                }
            }

            var refreshedId = refreshed?.Id;
            var canonicalId = CanonicalNodeIdentity.CreateId(node);
            node.Id = canonicalId;
            node.SourceSubscriptionIds = memberships.OrderBy(x => x, StringComparer.Ordinal).ToList();
            node.SourceSubscriptionId = node.SourceSubscriptionIds.FirstOrDefault();
            rebuilt.Add(node);

            foreach (var old in oldNodes)
                idMigration[old.Id] = canonicalId;
            if (refreshedId is not null)
                idMigration[refreshedId] = canonicalId;
        }

        settings.Nodes.Clear();
        settings.Nodes.AddRange(rebuilt);
        RewriteGroupReferences(settings, idMigration);

        if (settings.SelectedNodeId is string selected && idMigration.TryGetValue(selected, out var migrated))
            settings.SelectedNodeId = migrated;
    }

    private static IReadOnlyCollection<string> GetSubscriptionIds(ProxyNode node)
    {
        var ids = (node.SourceSubscriptionIds ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(node.SourceSubscriptionId))
            ids.Add(node.SourceSubscriptionId);
        return ids;
    }

    private static void RewriteGroupReferences(AppSettings settings, IReadOnlyDictionary<string, string?> idMigration)
    {
        var validIds = settings.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var group in settings.Groups)
        {
            var rewritten = new List<string>();
            foreach (var id in group.NodeIds)
            {
                var migrated = idMigration.TryGetValue(id, out var target) ? target : id;
                if (migrated is not null && validIds.Contains(migrated) && !rewritten.Contains(migrated, StringComparer.Ordinal))
                    rewritten.Add(migrated);
            }
            group.NodeIds = rewritten;
        }
    }
}
