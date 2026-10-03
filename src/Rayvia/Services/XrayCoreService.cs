using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using Rayvia.Models;

namespace Rayvia.Services;

public sealed class XrayExitedEventArgs(int processId, bool expected) : EventArgs
{
    public int ProcessId { get; } = processId;
    public bool Expected { get; } = expected;
}

public sealed record ActiveCore(string Version, string Path, Dictionary<string, string>? Hashes);

public interface IXrayCoreService : IDisposable
{
    event EventHandler<XrayExitedEventArgs>? Exited;
    bool IsRunning { get; }
    int? ProcessId { get; }
    string? ExecutablePath { get; }
    string AccessLogPath { get; }
    Task ConnectAsync(ProxyNode node, AppSettings settings, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed class XrayCoreService : IXrayCoreService
{
    private readonly HttpClient _http;
    private readonly LogService _log;
    private Process? _process;
    private bool _expectedStop;
    private readonly string _coreDirectory;
    private readonly string _logDirectory;

    public event EventHandler<XrayExitedEventArgs>? Exited;
    public bool IsRunning => _process is { HasExited: false };
    public int? ProcessId => _process?.HasExited == false ? _process.Id : null;
    public string? ExecutablePath => _process?.StartInfo.FileName;
    public string AccessLogPath => Path.Combine(_logDirectory, "access.log");
    public string ErrorLogPath => Path.Combine(_logDirectory, "xray-error.log");
    public string CoreDirectory => _coreDirectory;

    public XrayCoreService(LogService log, HttpClient? http = null, string? dataDirectory = null)
    {
        _log = log;
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Rayvia/0.4");
        _http.Timeout = TimeSpan.FromSeconds(60);
        var root = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rayvia");
        _coreDirectory = Path.Combine(root, "Core");
        _logDirectory = Path.Combine(root, "Logs");
    }

    public async Task ConnectAsync(ProxyNode node, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var executable = await EnsureCoreAsync(settings.ConnectionMode == ConnectionMode.Tun, cancellationToken);
        await DisconnectAsync(cancellationToken);
        Directory.CreateDirectory(_logDirectory);
        TryDelete(AccessLogPath); TryDelete(ErrorLogPath);
        var configPath = Path.Combine(_coreDirectory, "rayvia-config.json");
        var includeCommunityRuList = settings.RoutingMode == RoutingMode.Smart;
        var includeRuGeoIp = settings.RoutingMode == RoutingMode.Smart;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var config = XrayConfigBuilder.Build(node, settings, AccessLogPath, ErrorLogPath, includeCommunityRuList, includeRuGeoIp);
            await File.WriteAllTextAsync(configPath, config, cancellationToken);
            var validation = await ValidateConfigAsync(executable, configPath, cancellationToken);
            if (validation.Success) break;
            if (includeCommunityRuList && IsGeositeLoadError(validation.Detail)) { includeCommunityRuList = false; _log.Write("Smart Routing: geosite fallback."); continue; }
            if (includeRuGeoIp && IsGeoIpLoadError(validation.Detail)) { includeRuGeoIp = false; _log.Write("Smart Routing: geoip fallback."); continue; }
            _log.Write("Xray config validation: " + validation.Detail);
            throw new InvalidOperationException("Xray не принял конфигурацию. Подробности записаны в журнале.");
        }

        _expectedStop = false;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo { FileName = executable, Arguments = $"run -c \"{configPath}\"", WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true },
            EnableRaisingEvents = true
        };
        _process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.Write("Xray: " + e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.Write("Xray: " + e.Data); };
        _process.Exited += ProcessOnExited;
        if (!_process.Start()) throw new InvalidOperationException("Не удалось запустить Xray.");
        _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        await Task.Delay(settings.ConnectionMode == ConnectionMode.Tun ? 1200 : 500, cancellationToken);
        if (_process.HasExited) throw new InvalidOperationException("Xray завершился при запуске. Подробности записаны в журнале.");
        _log.Write($"Подключение: {node.Name} · {node.Protocol.ToUpperInvariant()} · {(settings.ConnectionMode == ConnectionMode.Tun ? "TUN" : "System Proxy")}.");
    }

    private void ProcessOnExited(object? sender, EventArgs e)
    {
        if (sender is not Process process) return;
        var expected = _expectedStop;
        _log.Write(expected ? "Xray остановлен." : "Xray неожиданно завершился.");
        Exited?.Invoke(this, new XrayExitedEventArgs(process.Id, expected));
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null) return;
        _expectedStop = true;
        try
        {
            if (!process.HasExited)
            {
                try { process.CloseMainWindow(); } catch { }
                try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
        }
        catch (Exception ex) { _log.Write("Ошибка остановки Xray: " + ex.Message); }
        finally { process.Dispose(); if (ReferenceEquals(_process, process)) _process = null; }
    }

    private async Task<string> EnsureCoreAsync(bool requireWintun, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_coreDirectory);
        var stagingDirectory = Path.Combine(_coreDirectory, "staging");
        if (Directory.Exists(stagingDirectory))
        {
            foreach (var stale in Directory.EnumerateDirectories(stagingDirectory))
            {
                try { Directory.Delete(stale, true); } catch { }
            }
        }
        var activePath = Path.Combine(_coreDirectory, "active-core.json");
        if (File.Exists(activePath))
        {
            try
            {
                var record = JsonSerializer.Deserialize<ActiveCore>(await File.ReadAllTextAsync(activePath, cancellationToken));
                if (await IsActiveCoreValidAsync(record, XrayRelease.Version, _coreDirectory, requireWintun, cancellationToken))
                    return Path.Combine(record!.Path, "xray.exe");
            }
            catch { }
        }

        _log.Write($"Скачивание Xray core {XrayRelease.Version}…");
        using var release = await ReadReleaseAsync(cancellationToken);
        var asset = FindAsset(release, XrayRelease.AssetName);
        var digest = FindAsset(release, XrayRelease.DigestAssetName);
        if (asset is null || digest is null) throw new InvalidOperationException("Xray release не содержит обязательный asset или официальный digest.");
        var root = Path.Combine(_coreDirectory, "staging", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var zipPart = Path.Combine(root, XrayRelease.AssetName + ".part"); var digestPart = Path.Combine(root, XrayRelease.DigestAssetName + ".part"); var extracted = Path.Combine(root, "extracted");
        try
        {
            await DownloadToPartAsync(asset, zipPart, cancellationToken);
            await DownloadToPartAsync(digest, digestPart, cancellationToken);
            var expected = ParseSha256(await File.ReadAllTextAsync(digestPart, cancellationToken));
            if (expected is null) throw new InvalidOperationException("Xray official digest не содержит SHA2-256.");
            if (!await VerifySha256Async(zipPart, expected, cancellationToken)) throw new InvalidOperationException("SHA-256 Xray archive не совпал с официальным digest.");
            ZipFile.ExtractToDirectory(zipPart, extracted);
            var files = new[] { "xray.exe", "geoip.dat", "geosite.dat" }.ToDictionary(x => x, x => FindRequiredFile(extracted, x), StringComparer.OrdinalIgnoreCase);
            var wintun = FindOptionalFile(extracted, "wintun.dll"); var license = FindOptionalFile(extracted, "LICENSE-Wintun");
            if (requireWintun && wintun is null) throw new InvalidOperationException("Xray archive не содержит wintun.dll, необходимый для TUN.");
            var versionPath = Path.Combine(_coreDirectory, "versions", XrayRelease.Version); var next = versionPath + ".new-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(next);
            var old = versionPath + ".old-" + Guid.NewGuid().ToString("N");
            var movedOld = false;
            try
            {
                foreach (var file in files) File.Copy(file.Value, Path.Combine(next, file.Key));
                if (wintun is not null) File.Copy(wintun, Path.Combine(next, "wintun.dll")); if (license is not null) File.Copy(license, Path.Combine(next, "LICENSE-Wintun"));
                if (Directory.Exists(versionPath)) { Directory.Move(versionPath, old); movedOld = true; }
                Directory.Move(next, versionPath);

                var hashes = await ComputeCriticalHashesAsync(versionPath, cancellationToken);
                var metadata = new ActiveCore(XrayRelease.Version, versionPath, hashes);
                var tempActive = activePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using var marker = new FileStream(tempActive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
                    await JsonSerializer.SerializeAsync(marker, metadata, cancellationToken: cancellationToken);
                    await marker.FlushAsync(cancellationToken);
                    marker.Flush(flushToDisk: true);
                }
                catch
                {
                    try { File.Delete(tempActive); } catch { }
                    throw;
                }
                try
                {
                    if (File.Exists(activePath)) File.Replace(tempActive, activePath, null, true); else File.Move(tempActive, activePath);
                }
                finally { try { File.Delete(tempActive); } catch { } }

                if (movedOld)
                    try { Directory.Delete(old, true); } catch { }
            }
            catch
            {
                try { if (Directory.Exists(versionPath)) Directory.Delete(versionPath, true); } catch { }
                try { if (movedOld && Directory.Exists(old)) Directory.Move(old, versionPath); } catch { }
                throw;
            }
            finally { try { if (Directory.Exists(next)) Directory.Delete(next, true); } catch { } }
            return Path.Combine(versionPath, "xray.exe");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private async Task<JsonDocument> ReadReleaseAsync(CancellationToken cancellationToken)
    { using var response = await _http.GetAsync(XrayRelease.ReleaseApi, cancellationToken); response.EnsureSuccessStatusCode(); return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken)); }
    private static JsonElement? FindAsset(JsonDocument release, string name)
    {
        foreach (var asset in release.RootElement.GetProperty("assets").EnumerateArray())
            if (string.Equals(asset.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                return asset;
        return null;
    }
    private async Task DownloadToPartAsync(JsonElement? asset, string path, CancellationToken cancellationToken) { if (asset is null) throw new InvalidOperationException("Missing release asset."); using var response = await _http.GetAsync(asset.Value.GetProperty("browser_download_url").GetString(), HttpCompletionOption.ResponseHeadersRead, cancellationToken); response.EnsureSuccessStatusCode(); await using var input = await response.Content.ReadAsStreamAsync(cancellationToken); await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous); await input.CopyToAsync(output, cancellationToken); await output.FlushAsync(cancellationToken); output.Flush(true); }
    private static string? ParseSha256(string content) { var match = Regex.Match(content, @"(?im)^SHA2-256\s*=\s*([0-9a-f]{64})\s*$"); return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null; }
    public static async Task<bool> VerifySha256Async(string path, string expected, CancellationToken cancellationToken = default) { if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) return false; await using var stream = File.OpenRead(path); var hash = await SHA256.HashDataAsync(stream, cancellationToken); return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase); }
    public static async Task<bool> IsActiveCoreValidAsync(ActiveCore? record, string expectedVersion, string coreDirectory, bool requireWintun, CancellationToken cancellationToken = default)
    {
        if (record is null || record.Hashes is null || !string.Equals(record.Version, expectedVersion, StringComparison.Ordinal) || !IsPathUnderDirectory(record.Path, coreDirectory))
            return false;

        var required = new[] { "xray.exe", "geoip.dat", "geosite.dat" };
        if (requireWintun)
            required = [.. required, "wintun.dll"];

        foreach (var file in required)
        {
            var path = Path.Combine(record.Path, file);
            if (!File.Exists(path) || !record.Hashes.TryGetValue(file, out var expectedHash) || !await VerifySha256Async(path, expectedHash, cancellationToken))
                return false;
        }

        return true;
    }
    private static string FindRequiredFile(string root, string name) => FindOptionalFile(root, name) ?? throw new InvalidOperationException($"Архив Xray не содержит {name}.");
    private static string? FindOptionalFile(string root, string name) => Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
    private static bool IsPathUnderDirectory(string path, string directory)
    {
        try
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    private static async Task<Dictionary<string, string>> ComputeCriticalHashesAsync(string root, CancellationToken cancellationToken)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "xray.exe", "geoip.dat", "geosite.dat", "wintun.dll" })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path))
                continue;
            await using var stream = File.OpenRead(path);
            hashes[name] = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }
        return hashes;
    }
    private static bool IsGeositeLoadError(string detail) => detail.Contains("geosite", StringComparison.OrdinalIgnoreCase) && detail.Contains("failed", StringComparison.OrdinalIgnoreCase);
    private static bool IsGeoIpLoadError(string detail) => detail.Contains("geoip", StringComparison.OrdinalIgnoreCase) && detail.Contains("failed", StringComparison.OrdinalIgnoreCase);
    private static async Task<ConfigValidationResult> ValidateConfigAsync(string executable, string configPath, CancellationToken cancellationToken) { using var process = new Process { StartInfo = new ProcessStartInfo { FileName = executable, Arguments = $"run -test -c \"{configPath}\"", WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } }; if (!process.Start()) return new(false, "Не удалось запустить xray -test."); var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken); var stderr = process.StandardError.ReadToEndAsync(cancellationToken); try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(12)).Token); } catch { try { process.Kill(true); } catch { } return new(false, "Проверка конфигурации Xray превысила timeout."); } var detail = string.Join(" ", new[] { await stderr, await stdout }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())); return new(process.ExitCode == 0, detail); }
    private sealed record ConfigValidationResult(bool Success, string Detail);
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    public void Dispose() { DisconnectAsync().GetAwaiter().GetResult(); _http.Dispose(); }
}
