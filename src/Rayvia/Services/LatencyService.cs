using System.Diagnostics;
using System.Net.Sockets;
using Rayvia.Models;

namespace Rayvia.Services;

public sealed class LatencyService
{
    public async Task<int?> MeasureAsync(
        ProxyNode node,
        int timeoutMilliseconds = 2500,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMilliseconds);

            var watch = Stopwatch.StartNew();
            await client.ConnectAsync(node.Host, node.Port, timeout.Token);
            watch.Stop();

            return Math.Max(1, (int)watch.ElapsedMilliseconds);
        }
        catch
        {
            return null;
        }
    }

    public async Task MeasureAllAsync(
        IEnumerable<ProxyNode> nodes,
        CancellationToken cancellationToken = default)
    {
        var targets = nodes.DistinctBy(x => x.Id).ToList();
        using var gate = new SemaphoreSlim(8);

        var tasks = targets.Select(async node =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var samples = new List<int>(2);

                for (var i = 0; i < 2; i++)
                {
                    var sample = await MeasureAsync(node, 2500, cancellationToken);
                    if (sample is int value)
                        samples.Add(value);
                }

                node.LatencyMs = samples.Count == 0
                    ? null
                    : (int)Math.Round(samples.Average());
                node.LatencyCheckedAt = DateTimeOffset.Now;
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }
}
