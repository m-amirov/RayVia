using Rayvia.Models;

namespace Rayvia.Services;

public sealed class ConnectionCoordinator : IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IXrayCoreService _xray;
    private readonly ISystemProxyService _proxy;
    private readonly IRuntimeStateStore _runtime;
    private readonly LogService _log;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _disposed;

    public ConnectionCoordinator(IXrayCoreService xray, ISystemProxyService proxy, IRuntimeStateStore runtime, LogService log)
    {
        _xray = xray;
        _proxy = proxy;
        _runtime = runtime;
        _log = log;
        _xray.Exited += XrayOnExited;
    }

    public ConnectionState State => _state;
    public event EventHandler<ConnectionState>? StateChanged;

    public async Task<bool> ConnectAsync(ProxyNode node, AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (_state is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Stopping)
                return false;
            SetState(ConnectionState.Connecting);
            SaveRuntime(settings, ownsProxy: false);
            try
            {
                await _xray.ConnectAsync(node, settings, cancellationToken);
                SaveRuntime(settings, ownsProxy: false);
                if (settings.ConnectionMode == ConnectionMode.SystemProxy)
                {
                    _proxy.Enable(settings.HttpPort);
                    SaveRuntime(settings, ownsProxy: true);
                }
                else
                {
                    _proxy.Disable();
                }
                SetState(ConnectionState.Connected);
                return true;
            }
            catch
            {
                SetState(ConnectionState.Failed);
                await CleanupUnsafeAsync();
                _runtime.Delete();
                throw;
            }
        }
        finally { _operationGate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (_state == ConnectionState.Disconnected) return;
            SetState(ConnectionState.Stopping);
            try
            {
                _proxy.Disable();
                await _xray.DisconnectAsync(cancellationToken);
                _runtime.Delete();
                SetState(ConnectionState.Disconnected);
            }
            catch
            {
                SetState(ConnectionState.Failed);
                throw;
            }
        }
        finally { _operationGate.Release(); }
    }

    public async Task RunExclusiveAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try { await operation(); }
        finally { _operationGate.Release(); }
    }

    private async void XrayOnExited(object? sender, XrayExitedEventArgs e)
    {
        if (_disposed || e.Expected) return;
        await _operationGate.WaitAsync();
        try
        {
            if (_state is ConnectionState.Stopping or ConnectionState.Disconnected) return;
            SetState(ConnectionState.Failed);
            try { _proxy.Disable(); } catch (Exception ex) { _log.Write("Не удалось отключить System Proxy после падения Xray: " + ex.Message); }
            _runtime.Delete();
            _log.Write($"Xray PID {e.ProcessId} завершился неожиданно; соединение переведено в Failed.");
        }
        finally { _operationGate.Release(); }
    }

    private async Task CleanupUnsafeAsync()
    {
        try { _proxy.Disable(); } catch (Exception ex) { _log.Write("Ошибка cleanup System Proxy: " + ex.Message); }
        try { await _xray.DisconnectAsync(); } catch (Exception ex) { _log.Write("Ошибка cleanup Xray: " + ex.Message); }
    }

    private void SaveRuntime(AppSettings settings, bool ownsProxy)
        => _runtime.Save(new RuntimeState { RayviaPid = Environment.ProcessId, XrayPid = _xray.ProcessId, ConnectionMode = settings.ConnectionMode, OwnsSystemProxy = ownsProxy, AppliedProxyServer = _proxy.AppliedProxyServer, Timestamp = DateTimeOffset.UtcNow, SessionId = _sessionId });

    private void SetState(ConnectionState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _xray.Exited -= XrayOnExited;
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch { }
        _operationGate.Dispose();
    }
}
