using System.Diagnostics;
using System.Security.Principal;

namespace Rayvia.Services;

public static class ElevationService
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RestartElevated(bool autoConnect)
    {
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("Не удалось определить путь Rayvia.");

        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = autoConnect ? "--autoconnect" : "",
            Verb = "runas",
            UseShellExecute = true
        });
    }
}
