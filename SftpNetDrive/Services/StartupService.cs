using System.Diagnostics;
using System.IO;

namespace SftpNetDrive.Services;

/// <summary>
/// Registers / removes a Task Scheduler logon task so the app starts
/// at login with elevated rights — no UAC prompt each boot.
/// </summary>
public static class StartupService
{
    private const string TaskName = "SftpNetDrive";

    public static bool IsEnabled()
    {
        var code = Schtasks($"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    public static bool Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
        // /SC ONLOGON  — fires at any user logon
        // /RL HIGHEST  — runs elevated, bypassing UAC prompt
        // /F           — overwrite if already exists
        return Schtasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F") == 0;
    }

    public static bool Disable() =>
        Schtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

    private static int Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit(8000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
