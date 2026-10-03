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
    private ActiveNodeSnapshot? _activeNode;
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
    public ActiveNodeSnapshot? ActiveNode => _activeNode;
    public string? ActiveNodeId => _activeNode?.Id;
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
                _activeNode = ActiveNodeSnapshot.From(node);
                SetState(ConnectionState.Connected);
                return true;
            }
            catch (Exception connectionException)
            {
                SetState(ConnectionState.Failed);
                var cleanup = await CleanupAsync(cancellationToken);
                if (cleanup.XrayStopped)
                    _activeNode = null;
                if (cleanup.Succeeded)
                {
                    _runtime.Delete();
                    throw;
                }

                throw new AggregateException(
                    "Подключение и последующая очистка завершились с ошибкой.",
                    new[] { connectionException }.Concat(cleanup.Errors));
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
            var cleanup = await CleanupAsync(cancellationToken);
            if (cleanup.XrayStopped)
                _activeNode = null;
            if (cleanup.Succeeded)
            {
                _runtime.Delete();
                SetState(ConnectionState.Disconnected);
                return;
            }

            SetState(ConnectionState.Failed);
            throw cleanup.ToException();
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
            var cleanup = await CleanupAsync(CancellationToken.None, stopXray: false);
            _activeNode = null;
            if (cleanup.Succeeded)
                _runtime.Delete();
            _log.Write($"Xray PID {e.ProcessId} завершился неожиданно; соединение переведено в Failed.");
        }
        finally { _operationGate.Release(); }
    }

    private async Task<CleanupResult> CleanupAsync(CancellationToken cancellationToken, bool stopXray = true)
    {
        var errors = new List<Exception>();
        var xrayStopped = !stopXray;

        try
        {
            if (!_proxy.Disable())
                throw new InvalidOperationException("System Proxy restore did not confirm ownership.");
        }
        catch (Exception ex)
        {
            errors.Add(ex);
            _log.Write("Ошибка cleanup System Proxy: " + ex.Message);
        }

        if (stopXray)
        {
            try
            {
                await _xray.DisconnectAsync(cancellationToken);
                xrayStopped = true;
            }
            catch (Exception ex)
            {
                errors.Add(ex);
                _log.Write("Ошибка cleanup Xray: " + ex.Message);
            }
        }

        return new CleanupResult(xrayStopped, errors);
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

    private sealed record CleanupResult(bool XrayStopped, IReadOnlyList<Exception> Errors)
    {
        public bool Succeeded => Errors.Count == 0;
        public Exception ToException()
            => new AggregateException("Очистка connection lifecycle завершилась с ошибкой.", Errors);
    }
}
