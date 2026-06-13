using Microsoft.Win32;

namespace SftpNetDriveNP;

/// <summary>
/// Reads the per-user mount table from HKCU so the WNP DLL can answer
/// NPGetConnection queries without round-tripping to the background EXE.
/// </summary>
internal static class MountRegistry
{
    private const string KeyPath = @"Software\SftpNetDrive\Mounts";

    internal static string? GetRemote(string letter)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(Norm(letter)) as string;
        }
        catch { return null; }
    }

    internal static (string Letter, string UncPath)[] GetAll()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return [];
            var names = key.GetValueNames();
            var result = new (string, string)[names.Length];
            int count = 0;
            foreach (var n in names)
            {
                var val = key.GetValue(n) as string;
                if (!string.IsNullOrEmpty(val))
                    result[count++] = (n + ":", val);
            }
            if (count < result.Length) Array.Resize(ref result, count);
            return result;
        }
        catch { return []; }
    }

    private static string Norm(string letter) =>
        letter.TrimEnd(':').ToUpperInvariant();
}
