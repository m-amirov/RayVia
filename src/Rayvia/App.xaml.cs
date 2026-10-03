using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Rayvia.Services;

namespace Rayvia;

public partial class App : Application
{
    private static readonly TimeSpan ElevationHandoffTimeout = TimeSpan.FromSeconds(30);
    private SingleInstanceService? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            if (ElevationHandoff.TryParse(e.Args, out var handoff))
            {
                _singleInstance = new SingleInstanceService();
                if (!_singleInstance.TryAcquireForHandoffAsync(handoff!, ElevationHandoffTimeout).GetAwaiter().GetResult())
                {
                    Shutdown(1);
                    return;
                }

                if (!SingleInstanceService.WaitForParentExitAsync(handoff!, ElevationHandoffTimeout).GetAwaiter().GetResult())
                {
                    _singleInstance.Dispose();
                    _singleInstance = null;
                    Shutdown(1);
                    return;
                }
                _singleInstance.StartCommandListener();
            }
            else
            {
                _singleInstance = new SingleInstanceService();
                if (!_singleInstance.TryAcquire())
                {
                    _ = SingleInstanceService.SendCommandAsync(
                        new InstanceCommand("activate", e.Args),
                        TimeSpan.FromMilliseconds(500));
                    Shutdown(0);
                    return;
                }
            }

            var recoveryLog = new LogService();
            var recoveryProxy = new SystemProxyService(log: recoveryLog);
            var recovery = new CrashRecoveryService(
                new RuntimeStateService(),
                recoveryProxy,
                new XrayProcessSupervisor(recoveryLog),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Rayvia",
                    "Core"),
                recoveryLog);
            recovery.RecoverAsync().GetAwaiter().GetResult();

            _singleInstance.CommandReceived += (_, args) =>
            {
                if (MainWindow is not MainWindow window)
                    return;

                Dispatcher.BeginInvoke(() =>
                {
                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;
                    window.Activate();
                });
            };
            Exit += (_, _) =>
            {
                _singleInstance?.Dispose();
            };

            var autoConnect = e.Args.Any(x => string.Equals(x, "--autoconnect", StringComparison.OrdinalIgnoreCase));
            var window = new MainWindow(autoConnect);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            var path = WriteCrashLog(ex);
            MessageBox.Show(
                $"Rayvia не удалось запустить.\n\nДиагностика: {path}",
                "Rayvia",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = WriteCrashLog(e.Exception);
        MessageBox.Show(
            $"Произошла ошибка.\n\nДиагностика: {path}",
            "Rayvia",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static string WriteCrashLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Rayvia",
                "Logs");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "startup.log");
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}] {exception}\n\n");
            return path;
        }
        catch
        {
            return "%LOCALAPPDATA%\\Rayvia\\Logs\\startup.log";
        }
    }

    public bool TryRestartElevatedAndShutdown(bool autoConnect)
    {
        if (_singleInstance is null)
            return false;

        var singleInstance = _singleInstance;
        var handoff = ElevationHandoff.Create(Environment.ProcessId);
        if (!singleInstance.BeginElevationHandoff(handoff))
            return false;

        Process? child = null;
        try
        {
            child = ElevationService.RestartElevated(autoConnect, handoff);
            if (!singleInstance.SignalElevationHandoff())
                throw new InvalidOperationException("Не удалось сигнализировать передачу ownership.");

            if (!singleInstance.WaitForElevationTakeover(ElevationHandoffTimeout))
            {
                try
                {
                    if (child is { HasExited: false })
                        child.Kill(entireProcessTree: true);
                    child?.WaitForExit(1000);
                }
                catch { }

                if (!singleInstance.AbortElevationHandoff())
                    throw new InvalidOperationException("Повышенный Rayvia не подтвердил передачу ownership.");
                return false;
            }

            singleInstance.CompleteElevationHandoff();
            _singleInstance = null;
            Shutdown(0);
            return true;
        }
        catch
        {
            try { singleInstance.AbortElevationHandoff(); } catch { }
            return false;
        }
    }
}
