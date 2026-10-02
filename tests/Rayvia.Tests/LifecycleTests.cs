using Rayvia.Models;
using Rayvia.Services;

namespace Rayvia.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task DoubleConnectIsSerializedAndSecondCallIsRejected()
    {
        var xray = new FakeXray(); var proxy = new FakeProxyStore(); var backup = new FakeBackupStore(); var proxyService = new SystemProxyService(proxy, backup, sessionId: "test"); var runtime = new FakeRuntimeStore(); using var coordinator = new ConnectionCoordinator(xray, proxyService, runtime, new LogService());
        var settings = new AppSettings(); var node = new ProxyNode { Protocol = "vless", Host = "example.com", Port = 443, UserId = "11111111-1111-1111-1111-111111111111" };
        Assert.True(await coordinator.ConnectAsync(node, settings)); Assert.False(await coordinator.ConnectAsync(node, settings)); Assert.Equal(ConnectionState.Connected, coordinator.State); await coordinator.DisconnectAsync();
    }

    [Fact]
    public async Task ActiveNodeSnapshotRemainsStableWhenNextSelectionChanges()
    {
        var xray = new FakeXray(); var proxy = new FakeProxyStore(); var backup = new FakeBackupStore(); var proxyService = new SystemProxyService(proxy, backup, sessionId: "test"); var runtime = new FakeRuntimeStore(); using var coordinator = new ConnectionCoordinator(xray, proxyService, runtime, new LogService());
        var settings = new AppSettings();
        var active = new ProxyNode { Id = "active", Name = "Active", Protocol = "vless", Host = "active.example", Port = 443, UserId = "11111111-1111-1111-1111-111111111111" };
        var next = new ProxyNode { Id = "next", Name = "Next", Protocol = "vless", Host = "next.example", Port = 443, UserId = "22222222-2222-2222-2222-222222222222" };

        Assert.True(await coordinator.ConnectAsync(active, settings));
        settings.SelectedNodeId = next.Id;
        active.Name = "Mutated by refresh";

        Assert.Equal("active", coordinator.ActiveNodeId);
        Assert.Equal("Active", coordinator.ActiveNode?.Name);
        await coordinator.DisconnectAsync();
        Assert.Null(coordinator.ActiveNode);
    }

    [Fact]
    public async Task UnexpectedXrayExitFailsAndRestoresProxy()
    {
        var xray = new FakeXray(); var proxy = new FakeProxyStore(); var backup = new FakeBackupStore(); var proxyService = new SystemProxyService(proxy, backup, sessionId: "test"); var runtime = new FakeRuntimeStore(); using var coordinator = new ConnectionCoordinator(xray, proxyService, runtime, new LogService());
        await coordinator.ConnectAsync(new ProxyNode { Protocol = "vless", Host = "example.com", Port = 443, UserId = "11111111-1111-1111-1111-111111111111" }, new AppSettings()); xray.Crash(); await Task.Delay(50);
        Assert.Equal(ConnectionState.Failed, coordinator.State); Assert.Equal(ProxySettings.Empty, proxy.Current); Assert.Null(runtime.State); Assert.Null(coordinator.ActiveNode);
    }
}
