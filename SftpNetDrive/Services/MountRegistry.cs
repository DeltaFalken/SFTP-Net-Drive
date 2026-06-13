using Microsoft.Win32;

namespace SftpNetDrive.Services;

/// <summary>
/// Persists the mapping of drive letter → UNC path in
/// HKCU\Software\SftpNetDrive\Mounts so that the background EXE can
/// re-mount drives after a logon and the WNP DLL can answer NPGetConnection
/// queries without IPC.
/// </summary>
public static class MountRegistry
{
    private const string KeyPath = @"Software\SftpNetDrive\Mounts";

    public static string? GetRemote(string letter)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(Norm(letter)) as string;
        }
        catch { return null; }
    }

    public static IReadOnlyList<(string Letter, string UncPath)> GetAll()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return [];
            return key.GetValueNames()
                .Select(n => (n + ":", key.GetValue(n) as string ?? ""))
                .Where(t => !string.IsNullOrEmpty(t.Item2))
                .ToList();
        }
        catch { return []; }
    }

    public static void Set(string letter, string uncPath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key.SetValue(Norm(letter), uncPath);
        }
        catch { }
    }

    public static void Remove(string letter)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(Norm(letter), throwOnMissingValue: false);
        }
        catch { }
    }

    private static string Norm(string letter) =>
        letter.TrimEnd(':').ToUpperInvariant();
}
