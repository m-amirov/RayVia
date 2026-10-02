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

    private sealed class TemporaryFile : IDisposable { public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rayvia-test-" + Guid.NewGuid().ToString("N")); public void Dispose() { try { File.Delete(Path); } catch { } } }
}
