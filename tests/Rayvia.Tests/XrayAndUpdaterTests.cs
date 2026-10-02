using System.Net;
using Rayvia.Services;

namespace Rayvia.Tests;

public sealed class XrayAndUpdaterTests
{
    [Fact]
    public async Task XrayDigestVerificationAcceptsGoodHashAndRejectsBadHash()
    {
        using var temp = new TemporaryFile(); await File.WriteAllTextAsync(temp.Path, "rayvia"); using var sha = System.Security.Cryptography.SHA256.Create(); var expected = Convert.ToHexString(sha.ComputeHash(await File.ReadAllBytesAsync(temp.Path)));
        Assert.True(await XrayCoreService.VerifySha256Async(temp.Path, expected)); Assert.False(await XrayCoreService.VerifySha256Async(temp.Path, new string('0', 64)));
    }

    [Fact]
    public void OrphanPathCheckDoesNotAcceptOutsideCore() { Assert.True(XrayProcessSupervisor.IsPathUnderDirectory("C:\\Rayvia\\Core\\xray.exe", "C:\\Rayvia\\Core")); Assert.False(XrayProcessSupervisor.IsPathUnderDirectory("C:\\Other\\xray.exe", "C:\\Rayvia\\Core")); }

    [Fact]
    public async Task UpdaterVerifiesPartBeforeAtomicInstall()
    {
        using var temp = new TemporaryDirectory();
        var installerBytes = System.Text.Encoding.UTF8.GetBytes("verified installer");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installerBytes)).ToLowerInvariant();
        using var client = new HttpClient(new ReleaseHandler(installerBytes, $"{hash}  Rayvia-Setup-x64.exe"));
        var service = new UpdateService(client, temp.Path);
        var update = await service.CheckAndDownloadAsync(new Version(0, 0, 1));
        Assert.NotNull(update); Assert.Equal(installerBytes, await File.ReadAllBytesAsync(update!.LocalInstallerPath)); Assert.Empty(Directory.GetFiles(temp.Path, "*.part"));
    }

    [Fact]
    public async Task UpdaterRejectsMissingOrBadChecksum()
    {
        using var temp = new TemporaryDirectory();
        using var missingClient = new HttpClient(new ReleaseHandler(System.Text.Encoding.UTF8.GetBytes("installer"), null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UpdateService(missingClient, temp.Path).CheckAndDownloadAsync(new Version(0, 0, 1)));
        using var badClient = new HttpClient(new ReleaseHandler(System.Text.Encoding.UTF8.GetBytes("installer"), new string('0', 64) + "  Rayvia-Setup-x64.exe"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UpdateService(badClient, temp.Path).CheckAndDownloadAsync(new Version(0, 0, 1)));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.part"));
    }

    private sealed class TemporaryFile : IDisposable { public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rayvia-test-" + Guid.NewGuid().ToString("N")); public void Dispose() { try { File.Delete(Path); } catch { } } }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rayvia-update-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private sealed class ReleaseHandler(byte[] installer, string? checksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri.EndsWith("/releases/latest", StringComparison.Ordinal) == true)
            {
                var assets = new List<object> { new { name = "Rayvia-Setup-x64.exe", browser_download_url = "https://test.invalid/installer" } };
                if (checksum is not null) assets.Add(new { name = "SHA256SUMS.txt", browser_download_url = "https://test.invalid/checksums" });
                return Task.FromResult(Json(new { draft = false, prerelease = false, tag_name = "v9.9.9", assets }));
            }
            if (request.RequestUri?.AbsoluteUri.EndsWith("/checksums", StringComparison.Ordinal) == true)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(checksum ?? "") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
    }
}
