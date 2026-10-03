using Rayvia.Services;

namespace Rayvia.Tests;

public sealed class SystemProxyTests
{
    [Fact]
    public void EnableWritesFullProxyState()
    {
        var store = new FakeProxyStore { Current = new ProxySettings(new(true, "DWord", "0"), new(false, null, null), new(true, "String", "<local>")) };
        var backup = new FakeBackupStore();
        var service = new SystemProxyService(store, backup, sessionId: "test");
        service.Enable(10809);
        Assert.Equal("1", store.Current.ProxyEnable.Value);
        Assert.Equal("127.0.0.1:10809", store.Current.ProxyServer.Value);
        Assert.Equal("<local>;localhost;127.*;[::1]", store.Current.ProxyOverride.Value);
        Assert.Equal(1, store.RefreshCount);
    }

    [Fact]
    public void DisableRestoresOnlyWhenAllValuesStillOwned()
    {
        var store = new FakeProxyStore { Current = ProxySettings.Empty };
        var backup = new FakeBackupStore();
        var service = new SystemProxyService(store, backup, sessionId: "test");
        service.Enable(10809);
        Assert.True(service.Disable());
        Assert.Equal(ProxySettings.Empty, store.Current);
        Assert.Null(backup.Snapshot);
    }

    [Fact]
    public void EnableAndDisableRestoreExistingProxyStateExactly()
    {
        var original = new ProxySettings(
            new(true, "DWord", "1"),
            new(true, "String", "127.0.0.1:10809"),
            new(true, "String", "<existing value>"));
        var store = new FakeProxyStore { Current = original };
        var backup = new FakeBackupStore();
        var service = new SystemProxyService(store, backup, sessionId: "test");

        service.Enable(10909);

        Assert.Equal("1", store.Current.ProxyEnable.Value);
        Assert.Equal("127.0.0.1:10909", store.Current.ProxyServer.Value);
        Assert.Equal("<local>;localhost;127.*;[::1]", store.Current.ProxyOverride.Value);

        Assert.True(service.Disable());
        Assert.Equal(original, store.Current);
    }

    [Fact]
    public void ExternalProxyOverridePreventsOverwrite()
    {
        var store = new FakeProxyStore(); var backup = new FakeBackupStore(); var service = new SystemProxyService(store, backup, sessionId: "test");
        service.Enable(10809);
        store.Current = store.Current with { ProxyOverride = new(true, "String", "user-value") };
        Assert.False(service.Disable());
        Assert.Equal("user-value", store.Current.ProxyOverride.Value);
    }

    [Fact]
    public void ExternalProxyServerChangePreventsOverwrite()
    {
        var store = new FakeProxyStore
        {
            Current = new ProxySettings(
                new(true, "DWord", "1"),
                new(true, "String", "127.0.0.1:10809"),
                new(true, "String", "<existing value>"))
        };
        var backup = new FakeBackupStore();
        var service = new SystemProxyService(store, backup, sessionId: "test");
        service.Enable(10909);

        store.Current = store.Current with
        {
            ProxyServer = new ProxySettingsSnapshot(true, "String", "127.0.0.1:10809")
        };

        Assert.False(service.Disable());
        Assert.Equal("127.0.0.1:10809", store.Current.ProxyServer.Value);
        Assert.Equal("<local>;localhost;127.*;[::1]", store.Current.ProxyOverride.Value);
        Assert.Null(backup.Snapshot);
    }
}
