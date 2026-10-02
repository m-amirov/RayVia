using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Rayvia.Services;

public sealed record ProxySettingsSnapshot(bool Exists, string? Kind, string? Value)
{
    public static ProxySettingsSnapshot Missing { get; } = new(false, null, null);
}

public sealed record ProxySettings(
    ProxySettingsSnapshot ProxyEnable,
    ProxySettingsSnapshot ProxyServer,
    ProxySettingsSnapshot ProxyOverride)
{
    public static ProxySettings Empty { get; } = new(
        ProxySettingsSnapshot.Missing,
        ProxySettingsSnapshot.Missing,
        ProxySettingsSnapshot.Missing);
}

public interface IProxySettingsStore
{
    ProxySettings Read();
    void Write(ProxySettings settings);
    void Refresh();
}

public sealed class WindowsProxySettingsStore : IProxySettingsStore
{
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int dwBufferLength);

    public ProxySettings Read()
    {
        using var key = OpenInternetSettings(writeable: false);
        return new ProxySettings(
            CaptureValue(key, "ProxyEnable"),
            CaptureValue(key, "ProxyServer"),
            CaptureValue(key, "ProxyOverride"));
    }

    public void Write(ProxySettings settings)
    {
        using var key = OpenInternetSettings(writeable: true);
        RestoreValue(key, "ProxyEnable", settings.ProxyEnable);
        RestoreValue(key, "ProxyServer", settings.ProxyServer);
        RestoreValue(key, "ProxyOverride", settings.ProxyOverride);
    }

    public void Refresh()
    {
        if (!InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0))
            throw new InvalidOperationException("InternetSetOption(SettingsChanged) завершился с ошибкой.");
        if (!InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0))
            throw new InvalidOperationException("InternetSetOption(Refresh) завершился с ошибкой.");
    }

    private static RegistryKey OpenInternetSettings(bool writeable)
        => Registry.CurrentUser.OpenSubKey(
               @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writeable)
           ?? throw new InvalidOperationException(
               "Не удалось открыть настройки системного прокси Windows.");

    private static ProxySettingsSnapshot CaptureValue(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null
            ? ProxySettingsSnapshot.Missing
            : new ProxySettingsSnapshot(true, key.GetValueKind(name).ToString(), value.ToString());
    }

    private static void RestoreValue(RegistryKey key, string name, ProxySettingsSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        if (!Enum.TryParse<RegistryValueKind>(snapshot.Kind, out var kind))
            kind = RegistryValueKind.String;

        object value = kind switch
        {
            RegistryValueKind.DWord when int.TryParse(snapshot.Value, out var dword) => dword,
            RegistryValueKind.QWord when long.TryParse(snapshot.Value, out var qword) => qword,
            _ => snapshot.Value ?? string.Empty
        };
        key.SetValue(name, value, kind);
    }
}

public interface IProxyBackupStore
{
    ProxyOwnershipSnapshot? Load();
    void Save(ProxyOwnershipSnapshot snapshot);
    void Delete();
}

public sealed record ProxyOwnershipSnapshot(
    string SessionId,
    ProxySettings Original,
    ProxySettings Applied);

public sealed class FileProxyBackupStore : IProxyBackupStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public FileProxyBackupStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rayvia", "system-proxy-backup.json");
    }

    public ProxyOwnershipSnapshot? Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<ProxyOwnershipSnapshot>(File.ReadAllText(_path), JsonOptions)
                : null;
        }
        catch { return null; }
    }

    public void Save(ProxyOwnershipSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
                File.Replace(temp, _path, null, ignoreMetadataErrors: true);
            else
                File.Move(temp, _path);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    public void Delete() { try { File.Delete(_path); } catch { } }
}

public interface ISystemProxyService
{
    string? AppliedProxyServer { get; }
    void Enable(int httpPort);
    bool Disable();
    bool RecoverStaleState();
}

public sealed class SystemProxyService : ISystemProxyService
{
    private readonly IProxySettingsStore _settingsStore;
    private readonly IProxyBackupStore _backupStore;
    private readonly LogService? _log;
    private readonly string _sessionId;

    public SystemProxyService(
        IProxySettingsStore? settingsStore = null,
        IProxyBackupStore? backupStore = null,
        LogService? log = null,
        string? sessionId = null)
    {
        _settingsStore = settingsStore ?? new WindowsProxySettingsStore();
        _backupStore = backupStore ?? new FileProxyBackupStore();
        _log = log;
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
    }

    public string? AppliedProxyServer => _backupStore.Load()?.Applied.ProxyServer.Value;

    public void Enable(int httpPort)
    {
        if (httpPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(httpPort));

        var current = _settingsStore.Read();
        var existing = _backupStore.Load();
        if (existing is null || !IsOwnedState(current, existing.Applied))
        {
            var target = CreateAppliedState($"127.0.0.1:{httpPort}");
            _backupStore.Save(new ProxyOwnershipSnapshot(_sessionId, current, target));
            existing = new ProxyOwnershipSnapshot(_sessionId, current, target);
        }

        var applied = existing.Applied with
        {
            ProxyServer = new ProxySettingsSnapshot(true, "String", $"127.0.0.1:{httpPort}")
        };
        _settingsStore.Write(applied);
        _settingsStore.Refresh();
        _backupStore.Save(existing with { SessionId = _sessionId, Applied = applied });
        _log?.Write($"System Proxy включён: {applied.ProxyServer.Value}.");
    }

    public bool Disable()
    {
        var ownership = _backupStore.Load();
        if (ownership is null)
            return true;

        var current = _settingsStore.Read();
        if (!IsOwnedState(current, ownership.Applied))
        {
            _backupStore.Delete();
            _log?.Write("System Proxy не восстановлен: настройки изменены внешним процессом.");
            return false;
        }

        _settingsStore.Write(ownership.Original);
        _settingsStore.Refresh();
        _backupStore.Delete();
        _log?.Write("System Proxy восстановлен.");
        return true;
    }

    public bool RecoverStaleState() => Disable();

    public static ProxySettings CreateAppliedState(string proxyServer)
        => new(
            new ProxySettingsSnapshot(true, "DWord", "1"),
            new ProxySettingsSnapshot(true, "String", proxyServer),
            new ProxySettingsSnapshot(true, "String", "<local>;localhost;127.*;[::1]"));

    public static bool IsOwnedState(ProxySettings current, ProxySettings applied)
        => Equals(current.ProxyEnable, applied.ProxyEnable)
           && Equals(current.ProxyServer, applied.ProxyServer)
           && Equals(current.ProxyOverride, applied.ProxyOverride);
}
