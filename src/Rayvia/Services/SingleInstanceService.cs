using System.IO.Pipes;
using System.Text.Json;

namespace Rayvia.Services;

public sealed record InstanceCommand(string Name, string[] Arguments);

public sealed class InstanceCommandEventArgs(InstanceCommand command) : EventArgs
{
    public InstanceCommand Command { get; } = command;
}

public sealed class SingleInstanceService : IDisposable
{
    public const string MutexName = "Local\\Rayvia.SingleInstance.v1";
    public const string PipeName = "Rayvia.SingleInstance.v1";

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
