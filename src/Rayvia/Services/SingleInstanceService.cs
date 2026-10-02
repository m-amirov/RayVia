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
    public const string PipeName = "Rayvia.SingleInstance.v1";
    private const string HandoffEventPrefix = "Local\\Rayvia.ElevationHandoff.";

    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listener;

    public event EventHandler<InstanceCommandEventArgs>? CommandReceived;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _listenerCancellation = new CancellationTokenSource();
        _listener = ListenAsync(_listenerCancellation.Token);
        return true;
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

    public static async Task<bool> WaitForParentExitAsync(
        ElevationHandoff handoff,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            return false;

        var deadline = DateTime.UtcNow + timeout;
        var signaled = false;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(GetHandoffEventName(handoff.Token));
                var remaining = deadline - DateTime.UtcNow;
                signaled = await Task.Run(() => signal.WaitOne(remaining), cancellationToken);
                break;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, remaining.TotalMilliseconds)), cancellationToken);
            }
        }

        if (!signaled || cancellationToken.IsCancellationRequested)
            return false;

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var parent = Process.GetProcessById(handoff.ParentProcessId);
                if (parent.HasExited)
                    return true;

                var remaining = deadline - DateTime.UtcNow;
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
        _listenerCancellation?.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _listenerCancellation?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }
}
