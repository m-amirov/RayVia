using System.Threading;
using System.Windows;

using Rayvia;

namespace Rayvia.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public void ClosingWindowDoesNotRaiseReentrantWpfCloseFailure()
    {
        Exception? dispatcherException = null;
        using var completed = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            var application = new App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            try
            {
                application.InitializeComponent();
                application.DispatcherUnhandledException += (_, args) =>
                {
                    dispatcherException = args.Exception;
                    args.Handled = true;
                    application.Shutdown();
                };

                var window = new MainWindow();
                application.MainWindow = window;
                var closeTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                closeTimer.Tick += (_, _) =>
                {
                    closeTimer.Stop();
                    window.Close();
                };
                window.Closed += (_, _) => application.Shutdown();
                window.Show();
                closeTimer.Start();
                application.Run();
            }
            catch (Exception exception)
            {
                dispatcherException = exception;
            }
            finally
            {
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)));
        thread.Join();
        Assert.Null(dispatcherException);
    }
}
