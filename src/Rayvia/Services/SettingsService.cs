using System.IO;
using System.Text.Json;
using Rayvia.Models;

namespace Rayvia.Services;

public sealed class SettingsRecoveryRequiredException : InvalidOperationException
{
    public SettingsRecoveryRequiredException(string message, Exception? inner = null)
        : base(message, inner) { }
}

public sealed class SettingsService
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly string _filePath;
    private readonly string _backupPath;

    public SettingsService(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rayvia");
        _filePath = Path.Combine(_directory, "settings.json");
        _backupPath = Path.Combine(_directory, "settings.json.bak");
    }

    public string FilePath => _filePath;
    public string BackupPath => _backupPath;

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return new AppSettings();

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions)
                           ?? throw new JsonException("Пустой JSON настроек.");
            return Migrate(settings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            var badPath = _filePath + ".bad-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
            try { File.Move(_filePath, badPath); } catch { }

            if (File.Exists(_backupPath))
            {
                try
                {
                    await using var backup = File.OpenRead(_backupPath);
                    var recovered = await JsonSerializer.DeserializeAsync<AppSettings>(backup, JsonOptions)
                                    ?? throw new JsonException("Пустой backup настроек.");
                    return Migrate(recovered);
                }
                catch (Exception backupException) when (
                    backupException is JsonException or IOException or NotSupportedException)
                {
                    throw new SettingsRecoveryRequiredException(
                        $"Файл настроек повреждён ({badPath}), backup также не удалось прочитать.",
                        backupException);
                }
            }

            throw new SettingsRecoveryRequiredException(
                $"Файл настроек повреждён и сохранён как {badPath}. Backup отсутствует.", ex);
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(_directory);
        settings.SchemaVersion = CurrentSchemaVersion;
        Normalize(settings);

        var temp = Path.Combine(
            _directory,
            $"settings.json.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
                File.Replace(temp, _filePath, _backupPath, ignoreMetadataErrors: true);
            else
                File.Move(temp, _filePath);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    private static AppSettings Migrate(AppSettings settings)
    {
        if (settings.SchemaVersion <= 0)
            settings.SchemaVersion = CurrentSchemaVersion;
        if (settings.SchemaVersion > CurrentSchemaVersion)
            throw new SettingsRecoveryRequiredException(
                $"Версия настроек {settings.SchemaVersion} новее поддерживаемой {CurrentSchemaVersion}.");

        Normalize(settings);
        settings.SchemaVersion = CurrentSchemaVersion;
        return settings;
    }

    private static void Normalize(AppSettings settings)
    {
        settings.Subscriptions ??= [];
        settings.Nodes ??= [];
        settings.Groups ??= [];
        settings.Rules ??= [];
    }
}
