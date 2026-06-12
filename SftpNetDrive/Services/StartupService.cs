using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SftpNetDrive.Services;

/// <summary>
/// Registers / removes the app as a Windows logon startup entry.
/// Primary: Task Scheduler with elevated rights (no UAC prompt at boot).
/// Fallback: HKCU\Run registry key — used on ARM where schtasks may fail.
/// Both methods pass --autostart so App.xaml.cs can suppress the window.
/// </summary>
public static class StartupService
{
    private const string TaskName    = "SftpNetDrive";
    private const string RegRunKey   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    internal const string AutostartArg = "--autostart";

    public static bool IsEnabled() =>
        Schtasks($"/Query /TN \"{TaskName}\"") == 0 || IsInRegistry();

    public static bool Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

        // /SC ONLOGON  — fires at any user logon
        // /RL HIGHEST  — runs elevated, bypassing UAC prompt
        // /F           — overwrite if already exists
        var taskOk = Schtasks(
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" {AutostartArg}\" /SC ONLOGON /RL HIGHEST /F") == 0;
        if (taskOk) return true;

        // Fallback for ARM and other environments where schtasks fails
        return SetRegistry($"\"{exe}\" {AutostartArg}");
    }

    public static bool Disable()
    {
        Schtasks($"/Delete /TN \"{TaskName}\" /F");
        RemoveRegistry();
        return !IsEnabled();
    }

    private static bool IsInRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegRunKey);
        return key?.GetValue(TaskName) is not null;
    }

    private static bool SetRegistry(string command)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegRunKey, writable: true);
            if (key is null) return false;
            key.SetValue(TaskName, command);
            return true;
        }
        catch { return false; }
    }

    private static void RemoveRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegRunKey, writable: true);
            key?.DeleteValue(TaskName, throwOnMissingValue: false);
        }
        catch { }
    }

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
