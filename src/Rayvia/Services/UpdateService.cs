using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Rayvia.Models;

namespace Rayvia.Services;

public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/m-amirov/Rayvia/releases/latest";
    private const string InstallerAssetName = "Rayvia-Setup-x64.exe";
    private const string ChecksumsAssetName = "SHA256SUMS.txt";

    private readonly HttpClient _http;
    private readonly string? _updatesDirectory;

    public UpdateService(HttpClient? http = null, string? updatesDirectory = null)
    {
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Rayvia-Updater/0.3.1");
        _http.Timeout = TimeSpan.FromSeconds(45);
        _updatesDirectory = updatesDirectory;
    }

    public async Task<UpdateInfo?> CheckAndDownloadAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(LatestReleaseApi, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            return null;

        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
            return null;

        var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var latest) || latest <= currentVersion)
            return null;

        string? installerUrl = null;
        string? checksumsUrl = null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();

            if (string.Equals(name, InstallerAssetName, StringComparison.OrdinalIgnoreCase))
                installerUrl = url;
            else if (string.Equals(name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
                checksumsUrl = url;
        }

        if (string.IsNullOrWhiteSpace(installerUrl))
            throw new InvalidOperationException("В релизе отсутствует установщик Rayvia.");
        if (string.IsNullOrWhiteSpace(checksumsUrl))
            throw new InvalidOperationException("Обновление отклонено: SHA256SUMS.txt отсутствует.");

        var expectedHash = await ReadExpectedHashAsync(checksumsUrl, cancellationToken)
                           ?? throw new InvalidOperationException("Обновление отклонено: SHA256SUMS.txt не содержит валидный hash установщика.");

        var directory = _updatesDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rayvia", "Updates");

        Directory.CreateDirectory(directory);
        foreach (var stalePart in Directory.EnumerateFiles(directory, "*.part"))
        {
            try { File.Delete(stalePart); } catch { }
        }
        var installer = Path.Combine(directory, $"Rayvia-Setup-x64-{latest}.exe");

        var part = installer + ".part";
        try { File.Delete(part); } catch { }

        if (!File.Exists(installer) || !await VerifyHashAsync(installer, expectedHash, cancellationToken))
        {
            try { File.Delete(installer); } catch { }

            try
            {
                using var download = await _http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                download.EnsureSuccessStatusCode();

                await using var input = await download.Content.ReadAsStreamAsync(cancellationToken);
                await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }

                if (!await VerifyHashAsync(part, expectedHash, cancellationToken))
                    throw new InvalidOperationException("Контрольная сумма обновления не совпала.");

                File.Move(part, installer, overwrite: true);
            }
            catch
            {
                try { File.Delete(part); } catch { }
                throw;
            }
        }

        return new UpdateInfo(latest, installerUrl, installer, expectedHash);
    }

    private async Task<string?> ReadExpectedHashAsync(string url, CancellationToken cancellationToken)
    {
        var content = await _http.GetStringAsync(url, cancellationToken);
        return ParseExpectedHash(content);
    }

    public static string? ParseExpectedHash(string content)
    {

        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 ||
                !parts[1].TrimStart('*').Equals(InstallerAssetName, StringComparison.OrdinalIgnoreCase) ||
                parts[0].Length != 64 ||
                !parts[0].All(Uri.IsHexDigit))
                continue;

            return parts[0].ToLowerInvariant();
        }

        return null;
    }

    private static async Task<bool> VerifyHashAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    public static void StartInstaller(UpdateInfo update)
    {
        if (!File.Exists(update.LocalInstallerPath))
            throw new FileNotFoundException("Проверенный установщик не найден.", update.LocalInstallerPath);
        if (!VerifySha256(update.LocalInstallerPath, update.ExpectedSha256))
            throw new InvalidOperationException("Установщик изменился после проверки SHA-256 и не будет запущен.");

        Process.Start(new ProcessStartInfo
        {
            FileName = update.LocalInstallerPath,
            Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true
        });
    }

    public static bool VerifySha256(string path, string expectedHash)
    {
        if (!File.Exists(path) || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            return false;
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}
