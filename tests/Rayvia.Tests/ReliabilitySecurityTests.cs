using Rayvia.Models;
using Rayvia.Services;

namespace Rayvia.Tests;

public sealed class ReliabilitySecurityTests
{
    [Fact]
    public void SingleInstanceAllowsOnlyOneOwner()
    {
        using var first = new SingleInstanceService();
        using var second = new SingleInstanceService();
        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());
    }

    [Fact]
    public void RuntimeStateRoundTripsWithoutUserFiles()
    {
        using var temp = new TemporaryDirectory();
        var service = new RuntimeStateService(Path.Combine(temp.Path, "runtime-state.json"));
        service.Save(new RuntimeState { RayviaPid = 42, XrayPid = 43, OwnsSystemProxy = true, SessionId = "session" });
        var loaded = service.Load();
        Assert.Equal(42, loaded?.RayviaPid);
        Assert.Equal(43, loaded?.XrayPid);
        service.Delete();
        Assert.Null(service.Load());
    }

    [Fact]
    public void MalformedOrMissingUpdateChecksumIsRejected()
    {
        Assert.Null(UpdateService.ParseExpectedHash("deadbeef  other.exe"));
        Assert.Null(UpdateService.ParseExpectedHash("not-a-hash  Rayvia-Setup-x64.exe"));
        Assert.Null(UpdateService.ParseExpectedHash(""));
    }

    [Fact]
    public void UpdateHashHelperRejectsTamperedFile()
    {
        using var file = new TemporaryFile(); File.WriteAllText(file.Path, "installer");
        Assert.False(UpdateService.VerifySha256(file.Path, new string('0', 64)));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rayvia-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private sealed class TemporaryFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rayvia-test-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }
}
