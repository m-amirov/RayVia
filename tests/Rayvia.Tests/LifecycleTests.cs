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

    [Fact]
    public async Task DisconnectAttemptsXrayWhenProxyRestoreThrows()
    {
        var xray = new FakeXray();
        var proxy = new FakeProxyService { ThrowOnDisable = true };
        var runtime = new FakeRuntimeStore();
        using var coordinator = new ConnectionCoordinator(xray, proxy, runtime, new LogService());

        await coordinator.ConnectAsync(CreateNode(), new AppSettings());
        await Assert.ThrowsAsync<AggregateException>(() => coordinator.DisconnectAsync());

        Assert.Equal(1, proxy.DisableCount);
        Assert.Equal(1, xray.DisconnectCount);
        Assert.Equal(ConnectionState.Failed, coordinator.State);
        Assert.NotNull(runtime.State);
        Assert.Null(coordinator.ActiveNode);
    }

    [Fact]
    public async Task DisconnectAttemptsProxyRestoreWhenXrayShutdownThrows()
    {
        var xray = new FakeXray { ThrowOnDisconnect = true };
        var proxy = new FakeProxyService();
        var runtime = new FakeRuntimeStore();
        using var coordinator = new ConnectionCoordinator(xray, proxy, runtime, new LogService());

        await coordinator.ConnectAsync(CreateNode(), new AppSettings());
        await Assert.ThrowsAsync<AggregateException>(() => coordinator.DisconnectAsync());

        Assert.Equal(1, proxy.DisableCount);
        Assert.Equal(1, xray.DisconnectCount);
        Assert.Equal(ConnectionState.Failed, coordinator.State);
        Assert.NotNull(runtime.State);
        Assert.NotNull(coordinator.ActiveNode);
    }

    [Fact]
    public async Task SuccessfulDisconnectClearsStateAndActiveNode()
    {
        var xray = new FakeXray();
        var proxy = new FakeProxyService();
        var runtime = new FakeRuntimeStore();
        using var coordinator = new ConnectionCoordinator(xray, proxy, runtime, new LogService());

        await coordinator.ConnectAsync(CreateNode(), new AppSettings());
        await coordinator.DisconnectAsync();

        Assert.Equal(ConnectionState.Disconnected, coordinator.State);
        Assert.Null(coordinator.ActiveNode);
        Assert.Null(runtime.State);
    }

    [Fact]
    public async Task PartiallyFailedDisconnectKeepsRecoveryInformationAndAttemptsBothActions()
    {
        var xray = new FakeXray { ThrowOnDisconnect = true };
        var proxy = new FakeProxyService { ThrowOnDisable = true };
        var runtime = new FakeRuntimeStore();
        using var coordinator = new ConnectionCoordinator(xray, proxy, runtime, new LogService());

        await coordinator.ConnectAsync(CreateNode(), new AppSettings());
        var error = await Assert.ThrowsAsync<AggregateException>(() => coordinator.DisconnectAsync());

        Assert.Equal(ConnectionState.Failed, coordinator.State);
        Assert.Equal(1, proxy.DisableCount);
        Assert.Equal(1, xray.DisconnectCount);
        Assert.NotNull(runtime.State);
        Assert.Equal(2, error.InnerExceptions.Count);
    }

    private static ProxyNode CreateNode() => new()
    {
        Protocol = "vless",
        Host = "example.com",
        Port = 443,
        UserId = "11111111-1111-1111-1111-111111111111"
    };
}
