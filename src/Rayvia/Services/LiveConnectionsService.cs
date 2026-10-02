using System.IO;
using System.Text.RegularExpressions;
using Rayvia.Models;

namespace Rayvia.Services;

public sealed class LiveConnectionsService : IDisposable
{
    private static readonly Regex AccessPattern = new(
        @"^(?<time>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?)\s+from\s+(?<source>\S+)\s+accepted\s+(?<destination>\S+)\s+\[(?<route>[^\]]+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private Timer? _timer;
    private string? _path;
    private long _position;
    private string _remainder = "";

    public event Action<ConnectionEntry>? EntryAdded;

    public void Start(string path)
    {
        Stop();
        _path = path;
        _position = 0;
        _remainder = "";
        _timer = new Timer(_ => _ = PollAsync(), null, 350, 600);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _path = null;
        _position = 0;
        _remainder = "";
    }

    private async Task PollAsync()
    {
        if (_path is null || !await _pollLock.WaitAsync(0))
            return;

        try
        {
            if (!File.Exists(_path))
                return;

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (_position > stream.Length)
            {
                _position = 0;
                _remainder = "";
            }

            stream.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            _position = stream.Length;

            if (string.IsNullOrEmpty(text))
                return;

            var combined = _remainder + text;
            var lines = combined.Split('\n');

            if (!combined.EndsWith('\n'))
            {
                _remainder = lines[^1];
                lines = lines[..^1];
            }
            else
            {
                _remainder = "";
            }

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                var entry = Parse(line);
                if (entry is not null)
                    EntryAdded?.Invoke(entry);
            }
        }
        catch
        {
            // Live view is diagnostic only. Connection handling must not depend on it.
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private static ConnectionEntry? Parse(string line)
    {
        var match = AccessPattern.Match(line);
        if (!match.Success)
            return null;

        var routeFlow = match.Groups["route"].Value;
        var route = routeFlow.Split("->", StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Trim() ?? routeFlow.Trim();

        return new ConnectionEntry
        {
            Timestamp = DateTimeOffset.Now,
            Source = StripNetworkPrefix(match.Groups["source"].Value),
            Destination = StripNetworkPrefix(match.Groups["destination"].Value),
            Route = route,
            Raw = line
        };
    }

    private static string StripNetworkPrefix(string value)
    {
        if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("udp:", StringComparison.OrdinalIgnoreCase))
            return value[4..];

        return value;
    }

    public void Dispose()
    {
        Stop();
        _pollLock.Dispose();
    }
}
