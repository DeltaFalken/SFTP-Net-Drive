using System.Runtime.InteropServices;
using System.Text;

namespace SftpNetDrive.Services;

/// <summary>
/// Stores SSH passwords in the Windows Credential Manager.
/// Key format: "SftpNetDrive_Z" (drive letter, no colon, upper-case).
/// </summary>
public static class CredentialService
{
    private const uint CredTypeGeneric   = 1;
    private const uint PersistLocalMachine = 2;
    private const string Prefix          = "SftpNetDrive_";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string  TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL userCredential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credential);

    /// <param name="letter">Drive letter — "Z", "Z:", or any case.</param>
    public static void Save(string letter, string secret)
    {
        var blob    = Encoding.Unicode.GetBytes(secret);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type               = CredTypeGeneric,
                TargetName         = Key(letter),
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob     = blobPtr,
                Persist            = PersistLocalMachine,
                UserName           = "SftpNetDrive",
            };
            CredWrite(ref cred, 0);
        }
        finally { Marshal.FreeHGlobal(blobPtr); }
    }

    public static string? Load(string letter)
    {
        if (!CredRead(Key(letter), CredTypeGeneric, 0, out var ptr))
            return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0) return "";
            var blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
            return Encoding.Unicode.GetString(blob);
        }
        finally { CredFree(ptr); }
    }

    public static void Delete(string letter) =>
        CredDelete(Key(letter), CredTypeGeneric, 0);

    private static string Key(string letter) =>
        Prefix + letter.TrimEnd(':').ToUpperInvariant();
}
