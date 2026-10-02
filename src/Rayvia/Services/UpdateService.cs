using System.Diagnostics;
using System.Text.Json;
using Rayvia.Models;

namespace Rayvia.Services;

public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/m-amirov/Rayvia/releases/latest";
    private const string InstallerAssetName = "Rayvia-Setup-x64.exe";

    private readonly HttpClient _http = new();

    public UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Rayvia-Updater/0.1");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<UpdateInfo?> CheckAndDownloadAsync(Version currentVersion, CancellationToken cancellationToken = default)
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

        string? url = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), InstallerAssetName, StringComparison.OrdinalIgnoreCase))
                continue;

            url = asset.GetProperty("browser_download_url").GetString();
            break;
        }

        if (string.IsNullOrWhiteSpace(url))
            return null;

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rayvia",
            "Updates");

        Directory.CreateDirectory(directory);
        var installer = Path.Combine(directory, $"Rayvia-Setup-x64-{latest}.exe");

        if (!File.Exists(installer))
        {
            using var download = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            download.EnsureSuccessStatusCode();

            await using var input = await download.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(installer);
            await input.CopyToAsync(output, cancellationToken);
        }

        return new UpdateInfo(latest, url, installer);
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
