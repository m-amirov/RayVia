using Rayvia.Models;
using Rayvia.Services;

namespace Rayvia.Tests;

public sealed class SettingsTests
{
    [Fact]
    public async Task SaveLoadUsesSchemaAndBackup()
    {
        using var temp = new TemporaryDirectory(); var service = new SettingsService(temp.Path);
        await service.SaveAsync(new AppSettings { AutoUpdate = false });
        var loaded = await service.LoadAsync();
        Assert.False(loaded.AutoUpdate); Assert.Equal(SettingsService.CurrentSchemaVersion, loaded.SchemaVersion);
        await service.SaveAsync(new AppSettings { SocksPort = 10900 });
        Assert.True(File.Exists(service.BackupPath));
    }

    [Fact]
    public async Task CorruptJsonIsQuarantinedAndBackupRestored()
    {
        using var temp = new TemporaryDirectory(); var service = new SettingsService(temp.Path);
        await service.SaveAsync(new AppSettings { SocksPort = 10810 }); await service.SaveAsync(new AppSettings { SocksPort = 10811 });
        await File.WriteAllTextAsync(service.FilePath, "{");
        var loaded = await service.LoadAsync();
        Assert.Equal(10810, loaded.SocksPort);
        Assert.NotEmpty(Directory.GetFiles(temp.Path, "settings.json.bad-*"));
    }

    [Fact]
    public async Task CorruptJsonWithoutBackupRequiresExplicitRecovery()
    {
        using var temp = new TemporaryDirectory(); var service = new SettingsService(temp.Path); Directory.CreateDirectory(temp.Path); await File.WriteAllTextAsync(service.FilePath, "not-json");
        await Assert.ThrowsAsync<SettingsRecoveryRequiredException>(() => service.LoadAsync());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rayvia-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
