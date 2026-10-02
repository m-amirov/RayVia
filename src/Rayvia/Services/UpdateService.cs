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

    private readonly HttpClient _http = new();

    public UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Rayvia-Updater/0.2");
        _http.Timeout = TimeSpan.FromSeconds(45);
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
            return null;

        var expectedHash = string.IsNullOrWhiteSpace(checksumsUrl)
            ? null
            : await ReadExpectedHashAsync(checksumsUrl, cancellationToken);

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rayvia",
            "Updates");

        Directory.CreateDirectory(directory);
        var installer = Path.Combine(directory, $"Rayvia-Setup-x64-{latest}.exe");

        if (!File.Exists(installer) ||
            (expectedHash is not null && !await VerifyHashAsync(installer, expectedHash, cancellationToken)))
        {
            try { File.Delete(installer); } catch { }

            using var download = await _http.GetAsync(
                installerUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            download.EnsureSuccessStatusCode();

            await using var input = await download.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(installer);
            await input.CopyToAsync(output, cancellationToken);
        }

        if (expectedHash is not null &&
            !await VerifyHashAsync(installer, expectedHash, cancellationToken))
        {
            try { File.Delete(installer); } catch { }
            throw new InvalidOperationException("Контрольная сумма обновления не совпала.");
        }

        return new UpdateInfo(latest, installerUrl, installer);
    }

    private async Task<string?> ReadExpectedHashAsync(string url, CancellationToken cancellationToken)
    {
        var content = await _http.GetStringAsync(url, cancellationToken);

        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                parts[^1].Equals(InstallerAssetName, StringComparison.OrdinalIgnoreCase) &&
                parts[0].Length == 64)
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
        Process.Start(new ProcessStartInfo
        {
            FileName = update.LocalInstallerPath,
            Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true
        });
    }
}
