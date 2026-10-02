using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Rayvia.Services;

public sealed class SystemProxyService
{
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Rayvia");

    private static readonly string BackupPath = Path.Combine(
        StateDirectory,
        "system-proxy-backup.json");

    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int dwBufferLength);

    public void Enable(int httpPort)
    {
        if (httpPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(httpPort));

        using var key = OpenInternetSettings(writeable: true);
        var target = $"127.0.0.1:{httpPort}";

        var backup = LoadBackup();

        if (backup is null)
        {
            backup = CreateBackup(key);
            Directory.CreateDirectory(StateDirectory);
        }

        backup.RayviaProxyServer = target;
        SaveBackup(backup);

        // Windows' manual proxy UI expects a single host:port value when one
        // proxy endpoint is used for both HTTP requests and HTTPS CONNECT.
        // Xray's HTTP inbound supports both.
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", target, RegistryValueKind.String);
        key.SetValue(
            "ProxyOverride",
            "<local>;localhost;127.*;[::1]",
            RegistryValueKind.String);

        Refresh();
    }

    public void Disable()
    {
        using var key = OpenInternetSettings(writeable: true);
        var backup = LoadBackup();

        if (backup is null)
        {
            // Compatibility cleanup for the malformed value written by
            // Rayvia <= 0.3.0. We cannot recover settings that were already
            // overwritten by those versions, but we can safely turn them off.
            var current = key.GetValue("ProxyServer") as string;
            if (IsLegacyRayviaProxy(current))
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                Refresh();
            }

            return;
        }

        var currentServer = key.GetValue("ProxyServer") as string;

        // If the user or another application changed Windows proxy settings
        // after Rayvia enabled them, do not overwrite those newer settings.
        if (!string.Equals(
                currentServer?.Trim(),
                backup.RayviaProxyServer,
                StringComparison.OrdinalIgnoreCase))
        {
            DeleteBackup();
            return;
        }

        RestoreValue(key, "ProxyEnable", backup.ProxyEnable);
        RestoreValue(key, "ProxyServer", backup.ProxyServer);
        RestoreValue(key, "ProxyOverride", backup.ProxyOverride);

        DeleteBackup();
        Refresh();
    }

    private static RegistryKey OpenInternetSettings(bool writeable)
        => Registry.CurrentUser.OpenSubKey(
               @"Software\Microsoft\Windows\CurrentVersion\Internet Settings",
               writeable)
           ?? throw new InvalidOperationException(
               "Не удалось открыть настройки системного прокси Windows.");

    private static ProxyBackup CreateBackup(RegistryKey key)
    {
        var currentServer = key.GetValue("ProxyServer") as string;

        // Rayvia <= 0.3.0 wrote a per-protocol string that Windows 11 displays
        // incorrectly in the manual proxy UI. Do not preserve that broken
        // Rayvia value as the user's original configuration.
        if (IsLegacyRayviaProxy(currentServer))
        {
            return new ProxyBackup
            {
                ProxyEnable = new RegistryValueSnapshot
                {
                    Exists = true,
                    Kind = RegistryValueKind.DWord.ToString(),
                    Value = "0"
                },
                ProxyServer = new RegistryValueSnapshot { Exists = false },
                ProxyOverride = new RegistryValueSnapshot { Exists = false }
            };
        }

        return new ProxyBackup
        {
            ProxyEnable = CaptureValue(key, "ProxyEnable"),
            ProxyServer = CaptureValue(key, "ProxyServer"),
            ProxyOverride = CaptureValue(key, "ProxyOverride")
        };
    }

    private static void SaveBackup(ProxyBackup backup)
    {
        File.WriteAllText(
            BackupPath,
            JsonSerializer.Serialize(
                backup,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ProxyBackup? LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath))
                return null;

            return JsonSerializer.Deserialize<ProxyBackup>(
                File.ReadAllText(BackupPath));
        }
        catch
        {
            return null;
        }
    }

    private static RegistryValueSnapshot CaptureValue(
        RegistryKey key,
        string name)
    {
        var value = key.GetValue(
            name,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);

        if (value is null)
            return new RegistryValueSnapshot { Exists = false };

        return new RegistryValueSnapshot
        {
            Exists = true,
            Kind = key.GetValueKind(name).ToString(),
            Value = value.ToString()
        };
    }

    private static void RestoreValue(
        RegistryKey key,
        string name,
        RegistryValueSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            try { key.DeleteValue(name, throwOnMissingValue: false); } catch { }
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

    private static bool IsLegacyRayviaProxy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();

        return normalized.StartsWith(
                   "http=127.0.0.1:",
                   StringComparison.OrdinalIgnoreCase)
               && normalized.Contains(
                   ";https=127.0.0.1:",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteBackup()
    {
        try { File.Delete(BackupPath); } catch { }
    }

    private static void Refresh()
    {
        InternetSetOption(
            IntPtr.Zero,
            InternetOptionSettingsChanged,
            IntPtr.Zero,
            0);

        InternetSetOption(
            IntPtr.Zero,
            InternetOptionRefresh,
            IntPtr.Zero,
            0);
    }

    private sealed class ProxyBackup
    {
        public string? RayviaProxyServer { get; set; }
        public RegistryValueSnapshot ProxyEnable { get; set; } = new();
        public RegistryValueSnapshot ProxyServer { get; set; } = new();
        public RegistryValueSnapshot ProxyOverride { get; set; } = new();
    }

    private sealed class RegistryValueSnapshot
    {
        public bool Exists { get; set; }
        public string? Kind { get; set; }
        public string? Value { get; set; }
    }
}
