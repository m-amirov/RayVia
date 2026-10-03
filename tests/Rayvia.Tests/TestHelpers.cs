global using Xunit;
using Rayvia.Models;
using Rayvia.Services;

namespace Rayvia.Tests;

internal sealed class FakeProxyStore : IProxySettingsStore
{
    public ProxySettings Current { get; set; } = ProxySettings.Empty;
    public int RefreshCount { get; private set; }
    public ProxySettings Read() => Current;
    public void Write(ProxySettings settings) => Current = settings;
    public void Refresh() => RefreshCount++;
}

internal sealed class FakeBackupStore : IProxyBackupStore
{
    public ProxyOwnershipSnapshot? Snapshot { get; private set; }
    public ProxyOwnershipSnapshot? Load() => Snapshot;
    public void Save(ProxyOwnershipSnapshot snapshot) => Snapshot = snapshot;
    public void Delete() => Snapshot = null;
}

internal sealed class FakeRuntimeStore : IRuntimeStateStore
{
    public RuntimeState? State { get; private set; }
    public RuntimeState? Load() => State;
    public void Save(RuntimeState state) => State = state;
    public void Delete() => State = null;
}

internal sealed class FakeProxyService : ISystemProxyService
{
    public bool ThrowOnDisable { get; init; }
    public int DisableCount { get; private set; }
    public string? AppliedProxyServer => "127.0.0.1:10809";
    public void Enable(int httpPort) { }
    public bool Disable()
    {
        DisableCount++;
        if (ThrowOnDisable)
            throw new InvalidOperationException("proxy restore failed");
        return true;
    }
    public bool RecoverStaleState() => Disable();
}

internal sealed class FakeXray : IXrayCoreService
{
    public event EventHandler<XrayExitedEventArgs>? Exited;
    public bool IsRunning { get; private set; }
    public int? ProcessId => IsRunning ? 1234 : null;
    public string? ExecutablePath => null;
    public string AccessLogPath => "access.log";
    public int ConnectCount { get; private set; }
    public int DisconnectCount { get; private set; }
    public bool ThrowOnDisconnect { get; init; }
    public Task ConnectAsync(ProxyNode node, AppSettings settings, CancellationToken cancellationToken = default) { ConnectCount++; IsRunning = true; return Task.CompletedTask; }
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        DisconnectCount++;
        if (ThrowOnDisconnect)
            throw new InvalidOperationException("xray shutdown failed");
        IsRunning = false;
        return Task.CompletedTask;
    }
    public void Crash() { IsRunning = false; Exited?.Invoke(this, new XrayExitedEventArgs(1234, false)); }
    public void Dispose() { IsRunning = false; }
}
