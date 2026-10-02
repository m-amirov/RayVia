using Rayvia.Models;
using Rayvia.Services;

namespace Rayvia.Tests;

public sealed class SubscriptionReconciliationTests
{
    [Fact]
    public void DuplicateLegacyCanonicalNodesCollapseAndMigrateGroupsAndSelection()
    {
        const string subscriptionId = "subscription-a";
        var first = CreateNode("legacy-a", "Old A", subscriptionId);
        var second = CreateNode("legacy-b", "Old B", subscriptionId);
        var group = new ServerGroup { NodeIds = [first.Id, second.Id] };
        var settings = new AppSettings { SelectedNodeId = second.Id, Nodes = [first, second], Groups = [group] };

        SubscriptionNodeReconciler.ApplyRefresh(settings, subscriptionId, [CreateNode("new-id", "Fresh", subscriptionId)]);

        var canonicalId = CanonicalNodeIdentity.CreateId(first);
        Assert.Single(settings.Nodes);
        Assert.Equal(canonicalId, settings.Nodes[0].Id);
        Assert.Equal([canonicalId], settings.Groups[0].NodeIds);
        Assert.Equal(canonicalId, settings.SelectedNodeId);
    }

    [Fact]
    public void RemovingOneSubscriptionPreservesSharedCanonicalNodeAndGroupReference()
    {
        const string firstSubscription = "subscription-a";
        const string secondSubscription = "subscription-b";
        var first = CreateNode("node-a", "A", firstSubscription);
        var second = CreateNode("node-b", "B", secondSubscription);
        var group = new ServerGroup { NodeIds = [first.Id, second.Id] };
        var settings = new AppSettings { Nodes = [first, second], Groups = [group] };

        SubscriptionNodeReconciler.ApplyRefresh(settings, firstSubscription, [CreateNode("fresh", "Fresh", firstSubscription)]);
        var canonicalId = CanonicalNodeIdentity.CreateId(first);
        SubscriptionNodeReconciler.RemoveSubscription(settings, firstSubscription);

        Assert.Single(settings.Nodes);
        Assert.Equal(secondSubscription, settings.Nodes[0].SourceSubscriptionId);
        Assert.Equal([canonicalId], settings.Groups[0].NodeIds);
    }

    private static ProxyNode CreateNode(string id, string name, string subscriptionId)
        => new()
        {
            Id = id,
            Name = name,
            Protocol = "vless",
            Host = "example.com",
            Port = 443,
            UserId = "11111111-1111-1111-1111-111111111111",
            SourceSubscriptionId = subscriptionId,
            SourceSubscriptionIds = [subscriptionId]
        };
}
