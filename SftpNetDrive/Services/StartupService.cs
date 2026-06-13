using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace SftpNetDrive.Services;

/// <summary>
/// Registers / removes the app as a Windows logon startup entry via Task Scheduler.
/// Uses a task XML file so the exe path never needs command-line quoting — spaces in
/// "Program Files" paths cannot break the argument parsing.
/// The task runs with HighestAvailable privilege so no UAC prompt appears at logon.
/// </summary>
public static class StartupService
{
    // Generic task name used by older installs and the installer's Pascal code.
    // Per-user task names include the user's SID so multiple accounts on the same
    // machine each get an independent scheduled task.
    private const string LegacyTaskName   = "SftpNetDrive";
    private const string RegRunKey        = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    internal const string AutostartArg          = "--autostart";
    internal const string RegisterStartupArg    = "--register-startup";
    internal const string UnregisterStartupArg  = "--unregister-startup";

    private static string GetTaskName()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        return string.IsNullOrEmpty(sid) ? LegacyTaskName : $"SftpNetDrive_{sid}";
    }

    // Only the scheduled task is considered authoritative; the HKCU\Run key is
    // unreliable for admin-required apps (Windows silently drops the UAC elevation
    // request at logon when launching from Run keys).
    public static bool IsEnabled() =>
        Schtasks($"/Query /TN \"{GetTaskName()}\"") == 0;

    public static bool Enable()
    {
        var exe = GetCurrentExecutablePath();
        if (string.IsNullOrEmpty(exe)) return false;
        return CreateTaskViaXml(exe);
    }

    public static bool Refresh()
    {
        if (!IsEnabled()) return false;
        return Enable();
    }

    // Removes the old non-SID task that existed before per-user task names were
    // introduced. That task fired for every user's logon and ran the app with
    // whatever identity the logged-on user had — or, if /RU was specified at
    // creation time, with va_ma's identity in every session.
    public static void RemoveLegacyTaskIfExists() =>
        Schtasks($"/Delete /TN \"{LegacyTaskName}\" /F");

    public static bool Disable()
    {
        Schtasks($"/Delete /TN \"{GetTaskName()}\" /F");
        Schtasks($"/Delete /TN \"{LegacyTaskName}\" /F"); // clean up any legacy generic task
        RemoveRegistryLegacy();
        return !IsEnabled();
    }

    // ── Task creation via XML ─────────────────────────────────────────────────

    private static bool CreateTaskViaXml(string exe)
    {
        // Use the SID so the task is tied to this user regardless of
        // domain/machine name formatting differences.
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrEmpty(sid)) return false;

        // SecurityElement.Escape handles any XML-special chars in the exe path
        // (theoretical — Windows paths cannot contain < > & " in practice).
        var xmlExe = SecurityElement.Escape(exe)!;

        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts SFTP Net Drive at logon without a UAC prompt.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{sid}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Hidden>false</Hidden>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{xmlExe}</Command>
                  <Arguments>{AutostartArg}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var tempXml = Path.Combine(Path.GetTempPath(), "SftpNetDrive_startup.xml");
        try
        {
            // Task Scheduler requires UTF-16 LE with BOM when importing via /XML.
            File.WriteAllText(tempXml, xml, Encoding.Unicode);
            return Schtasks($"/Create /XML \"{tempXml}\" /TN \"{GetTaskName()}\" /F") == 0;
        }
        catch { return false; }
        finally
        {
            try { File.Delete(tempXml); } catch { }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? GetCurrentExecutablePath()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            return exe;

        exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            return exe;

        var processName = Process.GetCurrentProcess().ProcessName;
        if (!string.IsNullOrEmpty(processName))
        {
            exe = Path.Combine(AppContext.BaseDirectory, processName + ".exe");
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                return exe;
        }

        return null;
    }

    private static void RemoveRegistryLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegRunKey, writable: true);
            key?.DeleteValue(LegacyTaskName, throwOnMissingValue: false);
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
                CreateNoWindow  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            })!;
            p.WaitForExit(8000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
