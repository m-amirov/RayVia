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

    public static Process RestartElevated(bool autoConnect, ElevationHandoff handoff)
    {
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("Не удалось определить путь Rayvia.");

        return Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = string.Join(" ", handoff.ToArguments(autoConnect)),
            Verb = "runas",
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Не удалось запустить повышенный Rayvia.");
    }
}
