using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Rayvia.Services;

public sealed record InstanceCommand(string Name, string[] Arguments);

public sealed record ElevationHandoff(string Token, int ParentProcessId)
{
    private const string HandoffArgument = "--elevation-handoff=";
    private const string ParentArgument = "--elevation-parent-pid=";

    public static ElevationHandoff Create(int parentProcessId)
    {
        if (parentProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(parentProcessId));

        return new ElevationHandoff(Guid.NewGuid().ToString("N"), parentProcessId);
    }

    public string[] ToArguments(bool autoConnect)
    {
        var arguments = new List<string>
        {
            HandoffArgument + Token,
            ParentArgument + ParentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (autoConnect)
            arguments.Insert(0, "--autoconnect");
        return arguments.ToArray();
    }

    public static bool TryParse(IReadOnlyCollection<string> arguments, out ElevationHandoff? handoff)
    {
        handoff = null;
        var token = arguments
            .FirstOrDefault(x => x.StartsWith(HandoffArgument, StringComparison.OrdinalIgnoreCase))?
            [HandoffArgument.Length..];
        var parent = arguments
            .FirstOrDefault(x => x.StartsWith(ParentArgument, StringComparison.OrdinalIgnoreCase))?
            [ParentArgument.Length..];

        return Guid.TryParseExact(token, "N", out _)
               && int.TryParse(parent, out var parentProcessId)
               && parentProcessId > 0
               && (handoff = new ElevationHandoff(token!, parentProcessId)) is not null;
    }
}

public sealed class InstanceCommandEventArgs(InstanceCommand command) : EventArgs
{
    public InstanceCommand Command { get; } = command;
}

public sealed class SingleInstanceService : IDisposable
{
    public const string MutexName = "Local\\Rayvia.SingleInstance.v1";
    public const string ArbitrationMutexName = "Local\\Rayvia.SingleInstance.Arbitration.v1";
    public const string PipeName = "Rayvia.SingleInstance.v1";
    private const string HandoffEventPrefix = "Local\\Rayvia.ElevationHandoff.";
    private const string HandoffReadyEventPrefix = "Local\\Rayvia.ElevationHandoffReady.";

    private Mutex? _arbitrationMutex;
    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listener;
    private ElevationHandoff? _handoff;
    private EventWaitHandle? _handoffSignal;
    private EventWaitHandle? _handoffReadySignal;

    public event EventHandler<InstanceCommandEventArgs>? CommandReceived;
    public bool OwnsInstance => _mutex is not null;

    public bool TryAcquire()
    {
        if (!TryAcquireArbitrationMutex())
            return false;

        var mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
        if (!createdNew || !TryTakeMutex(mutex, TimeSpan.Zero))
        {
            mutex.Dispose();
            ReleaseArbitrationMutex();
            return false;
        }

        _mutex = mutex;
        StartCommandListener();
        return true;
    }

    public bool BeginElevationHandoff(ElevationHandoff handoff)
    {
        if (!OwnsInstance || _handoff is not null)
            return false;

        try
        {
            _handoff = handoff;
            _handoffSignal = CreateHandoffSignal(handoff);
            _handoffReadySignal = CreateHandoffReadySignal(handoff);
            return true;
        }
        catch
        {
            ClearHandoffSignals();
            _handoff = null;
            return false;
        }
    }

    public bool SignalElevationHandoff()
    {
        if (_handoff is null || _handoffSignal is null || _mutex is null)
            return false;

        _handoffSignal.Set();
        ReleaseMainMutex();
        return true;
    }

    public bool WaitForElevationTakeover(TimeSpan timeout)
        => _handoffReadySignal?.WaitOne(timeout) == true;

    public async Task<bool> TryAcquireForHandoffAsync(
        ElevationHandoff handoff,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            return false;

        var deadline = DateTime.UtcNow + timeout;
        try
        {
            using var signal = EventWaitHandle.OpenExisting(GetHandoffEventName(handoff.Token));
            var remaining = Remaining(deadline);
            if (remaining <= TimeSpan.Zero || !await WaitOneAsync(signal, remaining, cancellationToken))
                return false;

            var mutex = new Mutex(initiallyOwned: false, MutexName, out _);
            var acquired = await WaitOneAsync(mutex, Remaining(deadline), cancellationToken);
            if (!acquired)
            {
                mutex.Dispose();
                return false;
            }

            try
            {
                using var ready = EventWaitHandle.OpenExisting(GetHandoffReadyEventName(handoff.Token));
                _mutex = mutex;
                ready.Set();
                return true;
            }
            catch
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Dispose();
                return false;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public bool AbortElevationHandoff()
    {
        if (_handoff is null)
            return false;

        if (_mutex is null)
        {
            var mutex = new Mutex(initiallyOwned: false, MutexName, out _);
            if (!TryTakeMutex(mutex, TimeSpan.Zero))
            {
                mutex.Dispose();
                return false;
            }
            _mutex = mutex;
        }

        ClearHandoffSignals();
        _handoff = null;
        return true;
    }

    public void CompleteElevationHandoff()
    {
        if (_handoff is null)
            return;

        ClearHandoffSignals();
        _handoff = null;
        StopCommandListener();
        ReleaseArbitrationMutex();
    }

    public void StartCommandListener()
    {
        if (_mutex is null || _listener is not null)
            return;

        _listenerCancellation = new CancellationTokenSource();
        _listener = ListenAsync(_listenerCancellation.Token);
    }

    public static async Task<bool> SendCommandAsync(InstanceCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await client.ConnectAsync(timeoutSource.Token);
            await JsonSerializer.SerializeAsync(client, command, cancellationToken: timeoutSource.Token);
            await client.FlushAsync(timeoutSource.Token);
            return true;
        }
        catch { return false; }
    }

    public static EventWaitHandle CreateHandoffSignal(ElevationHandoff handoff)
        => new(false, EventResetMode.ManualReset, GetHandoffEventName(handoff.Token), out _);

    public static EventWaitHandle CreateHandoffReadySignal(ElevationHandoff handoff)
        => new(false, EventResetMode.ManualReset, GetHandoffReadyEventName(handoff.Token), out _);

    public static async Task<bool> WaitForParentExitAsync(
        ElevationHandoff handoff,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            return false;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var parent = Process.GetProcessById(handoff.ParentProcessId);
                if (parent.HasExited)
                    return true;

                var remaining = Remaining(deadline);
                await parent.WaitForExitAsync(cancellationToken).WaitAsync(remaining, cancellationToken);
                return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        return false;
    }

    private static string GetHandoffEventName(string token) => HandoffEventPrefix + token;
    private static string GetHandoffReadyEventName(string token) => HandoffReadyEventPrefix + token;
    private static TimeSpan Remaining(DateTime deadline) => deadline - DateTime.UtcNow;

    private static async Task<bool> WaitOneAsync(WaitHandle handle, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            return false;

        return await Task.Run(() => handle.WaitOne(timeout), cancellationToken).WaitAsync(timeout, cancellationToken);
    }

    private static bool TryTakeMutex(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    private bool TryAcquireArbitrationMutex()
    {
        var mutex = new Mutex(initiallyOwned: false, ArbitrationMutexName, out var createdNew);
        if (!createdNew || !TryTakeMutex(mutex, TimeSpan.Zero))
        {
            mutex.Dispose();
            return false;
        }

        _arbitrationMutex = mutex;
        return true;
    }

    private void ReleaseMainMutex()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }

    private void ReleaseArbitrationMutex()
    {
        try { _arbitrationMutex?.ReleaseMutex(); } catch { }
        _arbitrationMutex?.Dispose();
        _arbitrationMutex = null;
    }

    private void ClearHandoffSignals()
    {
        _handoffSignal?.Dispose();
        _handoffSignal = null;
        _handoffReadySignal?.Dispose();
        _handoffReadySignal = null;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                var command = await JsonSerializer.DeserializeAsync<InstanceCommand>(server, cancellationToken: cancellationToken);
                if (command is not null)
                    CommandReceived?.Invoke(this, new InstanceCommandEventArgs(command));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
        }
    }

    public void Dispose()
    {
        StopCommandListener();
        ReleaseMainMutex();
        ClearHandoffSignals();
        _handoff = null;
        ReleaseArbitrationMutex();
    }

    private void StopCommandListener()
    {
        _listenerCancellation?.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _listenerCancellation?.Dispose();
        _listenerCancellation = null;
        _listener = null;
    }
}
