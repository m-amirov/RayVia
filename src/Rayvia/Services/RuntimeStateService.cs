using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Rayvia.Models;

namespace Rayvia.Services;

public interface IRuntimeStateStore
{
    RuntimeState? Load();
    void Save(RuntimeState state);
    void Delete();
}

public sealed class RuntimeStateService : IRuntimeStateStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public RuntimeStateService(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rayvia", "runtime-state.json");
    }

    public string FilePath => _path;

    public RuntimeState? Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(_path), JsonOptions)
                : null;
        }
        catch { return null; }
    }

    public void Save(RuntimeState state)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, JsonOptions);
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

public sealed class XrayProcessSupervisor
{
    private readonly LogService _log;

    public XrayProcessSupervisor(LogService log) { _log = log; }

    public bool IsOwnedProcess(int pid, string coreDirectory)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return IsOwnedProcess(process, coreDirectory);
        }
        catch { return false; }
    }

    public bool IsOwnedProcess(Process process, string coreDirectory)
    {
        if (!string.Equals(process.ProcessName, "xray", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var executable = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(executable)
                   && IsPathUnderDirectory(executable, coreDirectory);
        }
        catch { return false; }
    }

    public int[] FindOwnedOrphans(string coreDirectory, int? knownPid = null)
    {
        var result = new List<int>();
        foreach (var process in Process.GetProcessesByName("xray"))
        {
            using (process)
            {
                if ((knownPid is null || process.Id == knownPid.Value)
                    && IsOwnedProcess(process, coreDirectory))
                    result.Add(process.Id);
            }
        }
        return result.ToArray();
    }

    public async Task StopOwnedAsync(int pid, string coreDirectory, TimeSpan timeout)
    {
        if (!IsOwnedProcess(pid, coreDirectory))
        {
            _log.Write($"Xray PID {pid} не признан процессом Rayvia и не остановлен.");
            return;
        }

        using var process = Process.GetProcessById(pid);
        try { process.CloseMainWindow(); } catch { }
        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cancellation.Token);
            return;
        }
        catch (OperationCanceledException) { }
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { _log.Write($"Не удалось завершить orphan Xray PID {pid}: {ex.Message}"); }
    }

    public static bool IsPathUnderDirectory(string path, string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

public sealed class CrashRecoveryService
{
    private readonly IRuntimeStateStore _runtime;
    private readonly SystemProxyService _proxy;
    private readonly XrayProcessSupervisor _supervisor;
    private readonly string _coreDirectory;
    private readonly LogService _log;

    public CrashRecoveryService(IRuntimeStateStore runtime, SystemProxyService proxy, XrayProcessSupervisor supervisor, string coreDirectory, LogService log)
    {
        _runtime = runtime;
        _proxy = proxy;
        _supervisor = supervisor;
        _coreDirectory = coreDirectory;
        _log = log;
    }

    public async Task RecoverAsync()
    {
        var state = _runtime.Load();
        var recovered = false;
        try
        {
            foreach (var pid in _supervisor.FindOwnedOrphans(_coreDirectory))
                await _supervisor.StopOwnedAsync(pid, _coreDirectory, TimeSpan.FromSeconds(3));
            // The backup is authoritative ownership evidence, including a crash
            // between applying WinINet state and writing runtime-state.json.
            _proxy.RecoverStaleState();
            if (state is not null)
                _log.Write($"Выполнено recovery stale session {state.SessionId}.");
            recovered = true;
        }
        finally
        {
            // Keep the marker when recovery itself failed so the next launch can retry.
            if (recovered)
                _runtime.Delete();
        }
    }
}
