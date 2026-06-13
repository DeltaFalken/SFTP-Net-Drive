using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace SftpNetDriveNP;

// Simple file logger — writes to %LocalAppData%\SftpNetDrive\NP.log
// Uses LocalApplicationData (not CommonApplicationData) so that Explorer
// (Medium integrity) can write without Mandatory Integrity Control blocking it.
internal static class Log
{
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SftpNetDrive", "NP.log");

    static Log()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); } catch { }
    }

    public static void Write(string msg)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {msg}{Environment.NewLine}";
            File.AppendAllText(_path, line);
        }
        catch { }
    }
}

// ── Windows Network Provider error codes ─────────────────────────────────────

internal static class WN
{
    public const uint SUCCESS          = 0x00000000; // NO_ERROR
    public const uint NOT_SUPPORTED    = 0x00000032; // ERROR_NOT_SUPPORTED      (50)
    public const uint MORE_DATA        = 0x000000EA; // ERROR_MORE_DATA          (234)
    public const uint BAD_VALUE        = 0x00000057; // ERROR_INVALID_PARAMETER  (87)
    public const uint BAD_NETNAME      = 0x00000043; // ERROR_BAD_NET_NAME       (67)
    public const uint ACCESS_DENIED    = 0x00000005; // ERROR_ACCESS_DENIED      (5)
    public const uint NO_NETWORK       = 0x000004C6; // ERROR_NO_NETWORK         (1222)
    public const uint NOT_CONNECTED    = 0x000008CA; // ERROR_NOT_CONNECTED      (2250)
    public const uint NO_MORE_ENTRIES  = 0x00000103; // ERROR_NO_MORE_ITEMS      (259)
    public const uint ALREADY_CONNECTED= 0x00000055; // ERROR_ALREADY_ASSIGNED   (85)
    public const uint BAD_PASSWORD     = 0x00000056; // ERROR_INVALID_PASSWORD   (86)
    public const uint LOGON_FAILURE    = 0x0000052E; // ERROR_LOGON_FAILURE      (1326) → triggers Explorer credential dialog
    public const uint CANCEL           = 0x000004C7; // ERROR_CANCELLED          (1223)
}

// ── NETRESOURCEW structure ───────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NETRESOURCEW
{
    public uint  dwScope;
    public uint  dwType;
    public uint  dwDisplayType;
    public uint  dwUsage;
    public char* lpLocalName;
    public char* lpRemoteName;
    public char* lpComment;
    public char* lpProvider;
}

// ── Enum state (for NPOpenEnum / NPEnumResource) ─────────────────────────────

internal sealed class EnumState
{
    public (string Letter, string UncPath)[] Entries { get; init; } = [];
    public int Index { get; set; }
}

// ── Windows Network Provider exports ─────────────────────────────────────────

public static class NetworkProvider
{
    private const string ServerName = "SftpNetDrive";

    // ── NPGetCaps ─────────────────────────────────────────────────────────────
    // Called by MPR to discover what operations this provider supports.

    [UnmanagedCallersOnly(EntryPoint = "NPGetCaps", CallConvs = [typeof(CallConvStdcall)])]
    public static uint NPGetCaps(uint nIndex)
    {
        // Log every call so we can see what MPR queries.
        Log.Write($"NPGetCaps(nIndex={nIndex}) from PID {Environment.ProcessId}");
        // Empirically confirmed against Dokan2, WinFsp.Np, P9NP on Win10/11:
        // index 1 = WNNC_SPEC_VERSION, index 2 = WNNC_NET_TYPE, index 6 = WNNC_CONNECTION
        return nIndex switch
        {
            1  => 0x00050001u,  // WNNC_SPEC_VERSION: 5.0.1 (all providers return this)
            2  => 0x00490001u,  // WNNC_NET_TYPE: our custom type
            6  => 0x0000000Fu,  // WNNC_CONNECTION: Add + Add3 + Cancel + Get (MPR checks THIS, not index 8!)
            11 => 0x0000000Bu,  // WNNC_ENUMERATION: global + local + shareable (matches Dokan2/P9NP)
            12 => 0x00000001u,  // WNNC_START: available immediately (matches Dokan2/WinFsp)
            _  => 0x00000000u,
        };
    }

    // ── NPAddConnection ───────────────────────────────────────────────────────

    [UnmanagedCallersOnly(EntryPoint = "NPAddConnection", CallConvs = [typeof(CallConvStdcall)])]
    public static unsafe uint NPAddConnection(NETRESOURCEW* lpNetResource, char* lpPassword, char* lpUserName)
        => AddConnectionImpl(lpNetResource, lpPassword, lpUserName);

    [UnmanagedCallersOnly(EntryPoint = "NPAddConnection3", CallConvs = [typeof(CallConvStdcall)])]
    public static unsafe uint NPAddConnection3(IntPtr hwndOwner, NETRESOURCEW* lpNetResource, char* lpPassword, char* lpUserName, uint dwFlags)
    {
        Log.Write($"NPAddConnection3 entry: dwFlags=0x{dwFlags:X8} hwnd=0x{hwndOwner:X}");
        return AddConnectionImpl(lpNetResource, lpPassword, lpUserName, hwndOwner, dwFlags);
    }

    private static unsafe uint AddConnectionImpl(NETRESOURCEW* nr, char* lpPassword, char* lpUserName,
        IntPtr hwndOwner = default, uint dwFlags = 0)
    {
        try
        {
            if (nr == null) return WN.BAD_VALUE;

            var remote = nr->lpRemoteName != null ? NormalizePath(new string(nr->lpRemoteName)) : null;
            var local  = nr->lpLocalName  != null ? new string(nr->lpLocalName)  : null;
            var pass   = lpPassword  != null ? new string(lpPassword)  : "";
            var user   = lpUserName  != null ? new string(lpUserName)  : "";
            Log.Write($"NPAddConnection3: remote={remote} local={local} user={user}");

            if (string.IsNullOrEmpty(remote) || !IsOurPath(remote))
                return WN.BAD_NETNAME;

            // If no drive letter specified, find the first free one
            if (string.IsNullOrEmpty(local) || local == "*")
                local = FindFreeLetter();

            // 1. Try GENERIC credentials saved by a previous CredUI prompt.
            if (string.IsNullOrEmpty(pass))
            {
                var (credUser, credPass) = ReadFromCredentialManager(remote!);
                if (!string.IsNullOrEmpty(credPass))
                {
                    pass = credPass;
                    if (string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(credUser))
                        user = credUser;
                }
            }

            // 2. If still no password and we have a window → show our own CredUI dialog.
            //    This avoids the Windows Security dialog which stores DOMAIN_PASSWORD
            //    credentials whose blob is inaccessible to user-mode code.
            const uint CONNECT_INTERACTIVE = 0x00000008;
            if (string.IsNullOrEmpty(pass) && (dwFlags & CONNECT_INTERACTIVE) != 0 && hwndOwner != IntPtr.Zero)
            {
                var (dlgUser, dlgPass) = ShowCredentialDialog(hwndOwner, remote!, user);
                if (dlgUser == null) return WN.CANCEL;  // user cancelled
                if (!string.IsNullOrEmpty(dlgPass)) pass = dlgPass;
                if (!string.IsNullOrEmpty(dlgUser)) user = dlgUser;
            }

            // If an SSH username was entered in the credential box, it overrides
            // the username encoded in the UNC path.
            var msg = $"MOUNT\t{local}\t{remote}\t{pass}\t{user}";
            var response = SendPipeRequest(msg);

            var result = response switch
            {
                null                                      => WN.NO_NETWORK,
                "ALREADY"                                 => WN.SUCCESS,
                var r when r.StartsWith("OK")             => WN.SUCCESS,
                // "Missing parameters" = password not provided; "Auth" = wrong password.
                // LOGON_FAILURE triggers Explorer's credential dialog; ACCESS_DENIED does not.
                var r when r.Contains("Missing")
                        || r.Contains("Auth")
                        || r.Contains("password")         => WN.LOGON_FAILURE,
                _                                         => WN.ACCESS_DENIED,
            };
            Log.Write($"NPAddConnection3: response={response ?? "null"} -> 0x{result:X8}");
            return result;
        }
        catch (Exception ex) { Log.Write($"NPAddConnection3 EXCEPTION: {ex.Message}"); return WN.NO_NETWORK; }
    }

    // ── NPCancelConnection ────────────────────────────────────────────────────

    [UnmanagedCallersOnly(EntryPoint = "NPCancelConnection", CallConvs = [typeof(CallConvStdcall)])]
    public static unsafe uint NPCancelConnection(char* lpName, int fForce)
    {
        try
        {
            if (lpName == null) return WN.BAD_VALUE;
            var name = new string(lpName); // "Z:" or "\\SftpNetDrive\..."
            SendPipeRequest($"UNMOUNT\t{name}");
            return WN.SUCCESS;
        }
        catch { return WN.NOT_CONNECTED; }
    }

    // ── NPGetConnection ───────────────────────────────────────────────────────

    [UnmanagedCallersOnly(EntryPoint = "NPGetConnection", CallConvs = [typeof(CallConvStdcall)])]
    public static unsafe uint NPGetConnection(char* lpLocalName, char* lpRemoteName, uint* lpnBufferLen)
    {
        try
        {
            if (lpLocalName == null || lpnBufferLen == null) return WN.BAD_VALUE;
            var letter = new string(lpLocalName);
            var remote = MountRegistry.GetRemote(letter);
            if (remote == null) return WN.NOT_CONNECTED;

            // required buffer: chars + null terminator
            uint needed = (uint)((remote.Length + 1) * sizeof(char));
            if (*lpnBufferLen < needed)
            {
                *lpnBufferLen = needed;
                return WN.MORE_DATA;
            }

            var span = new Span<char>(lpRemoteName, (int)*lpnBufferLen / sizeof(char));
            remote.AsSpan().CopyTo(span);
            span[remote.Length] = '\0';
            return WN.SUCCESS;
        }
        catch { return WN.NOT_CONNECTED; }
    }

    // ── NPOpenEnum / NPEnumResource / NPCloseEnum ─────────────────────────────

    [UnmanagedCallersOnly(EntryPoint = "NPOpenEnum", CallConvs = [typeof(CallConvStdcall)])]
    public static unsafe uint NPOpenEnum(uint dwScope, uint dwType, uint dwUsage, NETRESOURCEW* lpNetResource, IntPtr* lphEnum)
    {
        try
        {
            if (lphEnum == null) return WN.BAD_VALUE;
            // dwScope 1 = RESOURCE_CONNECTED
            var entries = dwScope == 1 ? MountRegistry.GetAll() : [];
            var state   = new EnumState { Entries = entries };
            var handle  = GCHandle.Alloc(state, GCHandleType.Normal);
            *lphEnum    = GCHandle.ToIntPtr(handle);
            return WN.SUCCESS;
        }
        catch { return WN.NOT_SUPPORTED; }
    }

    [UnmanagedCallersOnly(EntryPoint = "NPEnumResource", CallConvs = [typeof(CallConvStdcall)])]
    public static unsafe uint NPEnumResource(IntPtr hEnum, uint* lpcCount, void* lpBuffer, uint* lpBufferSize)
    {
        try
        {
            if (hEnum == IntPtr.Zero || lpcCount == null || lpBufferSize == null)
                return WN.BAD_VALUE;

            var state = (EnumState)GCHandle.FromIntPtr(hEnum).Target!;
            if (state.Index >= state.Entries.Length)
                return WN.NO_MORE_ENTRIES;

            // Minimal: tell caller there are no (more) entries to enumerate.
            // Full NETRESOURCEW packing in a flat buffer is complex; skip for now.
            return WN.NO_MORE_ENTRIES;
        }
        catch { return WN.BAD_VALUE; }
    }

    [UnmanagedCallersOnly(EntryPoint = "NPCloseEnum", CallConvs = [typeof(CallConvStdcall)])]
    public static uint NPCloseEnum(IntPtr hEnum)
    {
        try
        {
            if (hEnum != IntPtr.Zero)
                GCHandle.FromIntPtr(hEnum).Free();
        }
        catch { }
        return WN.SUCCESS;
    }

    // ── NPGetResourceInformation ──────────────────────────────────────────────
    // Called by MPR to decide which provider owns a UNC path.

    [UnmanagedCallersOnly(EntryPoint = "NPGetResourceInformation", CallConvs = [typeof(CallConvStdcall)])]
    public static unsafe uint NPGetResourceInformation(NETRESOURCEW* lpNetResource, void* lpBuffer, uint* lpcbBuffer, char** lplpSystem)
    {
        try
        {
            if (lpNetResource == null || lpcbBuffer == null) return WN.BAD_VALUE;

            var remote = lpNetResource->lpRemoteName != null
                ? new string(lpNetResource->lpRemoteName) : null;

            Log.Write($"NPGetResourceInformation: remote={remote} isOurs={IsOurPath(remote)}");
            if (!IsOurPath(remote)) return WN.BAD_NETNAME;

            // Buffer layout: NETRESOURCEW | remote-name\0 | provider-name\0
            // lpProvider must point into the buffer so MPR knows which provider owns this
            // path and can call NPAddConnection3 on us directly.
            const string ProviderName = "SFTP Net Drive";
            var remoteBytes   = (remote!.Length + 1) * sizeof(char);
            var providerBytes = (ProviderName.Length + 1) * sizeof(char);
            uint needed = (uint)(sizeof(NETRESOURCEW) + remoteBytes + providerBytes);

            if (*lpcbBuffer < needed)
            {
                *lpcbBuffer = needed;
                return WN.MORE_DATA;
            }

            // Write strings after the struct
            var remoteDest   = (char*)((byte*)lpBuffer + sizeof(NETRESOURCEW));
            var providerDest = (char*)((byte*)lpBuffer + sizeof(NETRESOURCEW) + remoteBytes);

            remote.AsSpan().CopyTo(new Span<char>(remoteDest, remote.Length + 1));
            remoteDest[remote.Length] = '\0';

            ProviderName.AsSpan().CopyTo(new Span<char>(providerDest, ProviderName.Length + 1));
            providerDest[ProviderName.Length] = '\0';

            var nr = (NETRESOURCEW*)lpBuffer;
            nr->dwScope       = 0;
            nr->dwType        = 1;      // RESOURCETYPE_DISK
            nr->dwDisplayType = 3;      // RESOURCEDISPLAYTYPE_SHARE
            nr->dwUsage       = 1;      // RESOURCEUSAGE_CONNECTABLE
            nr->lpLocalName   = null;
            nr->lpComment     = null;
            nr->lpRemoteName  = remoteDest;
            nr->lpProvider    = providerDest; // tells MPR: "SFTP Net Drive" owns this path

            if (lplpSystem != null) *lplpSystem = null;
            Log.Write($"NPGetResourceInformation: SUCCESS provider={ProviderName}");
            return WN.SUCCESS;
        }
        catch (Exception ex) { Log.Write($"NPGetResourceInformation EXCEPTION: {ex.Message}"); return WN.BAD_NETNAME; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // MPR sometimes delivers paths with a single leading backslash; normalize to two.
    private static string NormalizePath(string path) =>
        path.Length > 0 && path[0] == '\\' && (path.Length < 2 || path[1] != '\\')
            ? '\\' + path : path;

    private static bool IsOurPath(string? path) =>
        path != null &&
        (path.StartsWith($@"\\{ServerName}\", StringComparison.OrdinalIgnoreCase)
         || path.Equals($@"\\{ServerName}", StringComparison.OrdinalIgnoreCase));

    private static string FindFreeLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();
        foreach (var c in "ZYXWVUTSRQPONMLKJIHGFEDCB")
            if (!used.Contains(c)) return c + ":";
        return "Z:";
    }

    // ── Windows Credential Manager ────────────────────────────────────────────

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, uint type, uint reserved, out IntPtr ppCredential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDUI_INFO
    {
        public int    cbSize;
        public IntPtr hwndParent;
        public IntPtr pszMessageText;   // LPCWSTR — must stay pinned for the call
        public IntPtr pszCaptionText;   // LPCWSTR
        public IntPtr hbmBanner;
    }

    // Modern Windows-style credential dialog (same look as "Windows Security" prompt).
    // Returns plaintext credentials via CredUnPackAuthenticationBufferW — unlike the
    // DOMAIN_PASSWORD path which stores credentials in LSASS and is inaccessible.
    [DllImport("credui.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern unsafe uint CredUIPromptForWindowsCredentialsW(
        CREDUI_INFO* pUiInfo,
        uint dwAuthError,
        ref uint pulAuthPackage,
        IntPtr pvInAuthBuffer,
        uint ulInAuthBufferSize,
        out IntPtr ppvOutAuthBuffer,
        out uint pulOutAuthBufferSize,
        ref bool pfSave,
        uint dwFlags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredUnPackAuthenticationBufferW(
        uint dwFlags,
        IntPtr pAuthBuffer,
        uint cbAuthBuffer,
        char[] pszUserName,
        ref int pcchMaxUserName,
        char[] pszDomainName,
        ref int pcchMaxDomainName,
        char[] pszPassword,
        ref int pcchMaxPassword);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredPackAuthenticationBufferW(
        uint dwFlags,
        string pszUserName,
        string pszPassword,
        IntPtr pPackedCredentials,
        ref uint pcbPackedCredentials);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWriteW(IntPtr Credential, uint Flags);

    private const uint CREDUIWIN_GENERIC  = 0x00000001; // return plaintext via CredUnPack
    private const uint CREDUIWIN_CHECKBOX = 0x00000002; // show "Save" checkbox

    // Try to read a CRED_TYPE_GENERIC credential (written by previous CredUIPrompt with
    // CREDUI_FLAGS_GENERIC_CREDENTIALS). DOMAIN_PASSWORD blobs are inaccessible.
    private static (string? user, string? pass) ReadFromCredentialManager(string remote)
    {
        foreach (var t in new[] { remote, $@"\\{ServerName}", ServerName })
        {
            if (!CredReadW(t, 1u /*GENERIC*/, 0, out var pCred)) continue;
            try
            {
                // CREDENTIAL offsets (x64/ARM64): BlobSize@32, Blob*@40, UserName*@72
                var blobSize = Marshal.ReadInt32(pCred, 32);
                var blobPtr  = Marshal.ReadIntPtr(pCred, 40);
                var userPtr  = Marshal.ReadIntPtr(pCred, 72);
                var credUser = userPtr != IntPtr.Zero ? Marshal.PtrToStringUni(userPtr) : null;
                string? credPass = blobSize > 0 && blobPtr != IntPtr.Zero
                    ? Marshal.PtrToStringUni(blobPtr, blobSize / sizeof(char)) : null;
                Log.Write($"CredManager: target={t} user={credUser} passLen={credPass?.Length ?? 0}");
                if (credPass != null) return (credUser, credPass);
            }
            finally { CredFree(pCred); }
        }
        Log.Write($"CredManager: no GENERIC credential found");
        return (null, null);
    }

    // Show the Windows Security-style credential dialog (CredUIPromptForWindowsCredentialsW).
    // Returns (user, pass) on OK, or (null, null) if cancelled.
    // Credentials are extracted as plaintext via CredUnPackAuthenticationBufferW and
    // optionally saved as CRED_TYPE_GENERIC so ReadFromCredentialManager can reuse them.
    private static unsafe (string? user, string? pass) ShowCredentialDialog(
        IntPtr hwndOwner, string remote, string? prefillUser)
    {
        var msg = remote;
        var cap = "SFTP Net Drive";
        fixed (char* pMsg = msg, pCap = cap)
        {
            var uiInfo = new CREDUI_INFO
            {
                cbSize         = Marshal.SizeOf<CREDUI_INFO>(),
                hwndParent     = hwndOwner,
                pszMessageText = (IntPtr)pMsg,
                pszCaptionText = (IntPtr)pCap,
            };

            // Pre-fill username by packing an input auth buffer (empty password).
            uint inBufSize = 0;
            IntPtr inBuf = IntPtr.Zero;
            if (!string.IsNullOrEmpty(prefillUser))
            {
                CredPackAuthenticationBufferW(0, prefillUser, "", IntPtr.Zero, ref inBufSize);
                if (inBufSize > 0)
                {
                    inBuf = Marshal.AllocCoTaskMem((int)inBufSize);
                    if (!CredPackAuthenticationBufferW(0, prefillUser, "", inBuf, ref inBufSize))
                    {
                        Marshal.FreeCoTaskMem(inBuf);
                        inBuf = IntPtr.Zero;
                        inBufSize = 0;
                    }
                }
            }

            uint authPackage = 0;
            bool save = true;
            IntPtr outBuf = IntPtr.Zero;
            uint outBufSize = 0;

            try
            {
                uint r = CredUIPromptForWindowsCredentialsW(&uiInfo, 0, ref authPackage,
                    inBuf, inBufSize, out outBuf, out outBufSize, ref save,
                    CREDUIWIN_GENERIC | CREDUIWIN_CHECKBOX);

                if (r != 0) { Log.Write($"CredUI cancelled/failed: {r}"); return (null, null); }

                var userBuf   = new char[513];
                var domainBuf = new char[256];
                var passBuf   = new char[513];
                int userLen = 513, domainLen = 256, passLen = 513;

                if (!CredUnPackAuthenticationBufferW(0, outBuf, outBufSize,
                        userBuf, ref userLen, domainBuf, ref domainLen, passBuf, ref passLen))
                {
                    Log.Write($"CredUnPack failed: {Marshal.GetLastWin32Error()}");
                    return (null, null);
                }

                // pcchMax* on output includes the NUL terminator
                var user = new string(userBuf, 0, Math.Max(0, userLen - 1));
                var pass = new string(passBuf, 0, Math.Max(0, passLen - 1));
                Array.Clear(passBuf, 0, passBuf.Length);

                Log.Write($"CredUI OK: user={user} passLen={pass.Length} save={save}");

                // Persist as GENERIC so ReadFromCredentialManager avoids re-prompting.
                if (save && !string.IsNullOrEmpty(pass))
                    SaveGenericCredential(remote, user, pass);

                return (user, pass);
            }
            finally
            {
                if (inBuf != IntPtr.Zero) Marshal.FreeCoTaskMem(inBuf);
                if (outBuf != IntPtr.Zero)
                {
                    // Secure-zero the sensitive output buffer before returning memory.
                    new Span<byte>((void*)outBuf, (int)outBufSize).Clear();
                    Marshal.FreeCoTaskMem(outBuf);
                }
            }
        }
    }

    // Write a CRED_TYPE_GENERIC credential to Windows Credential Manager manually.
    // CredUIPromptForWindowsCredentialsW does NOT auto-save — we must call CredWriteW.
    private static void SaveGenericCredential(string target, string user, string pass)
    {
        try
        {
            var passBytes = Encoding.Unicode.GetBytes(pass);
            int blobSize  = passBytes.Length;
            var hBlob     = Marshal.AllocCoTaskMem(blobSize > 0 ? blobSize : 1);
            var hTarget   = Marshal.StringToCoTaskMemUni(target);
            var hUser     = Marshal.StringToCoTaskMemUni(user);
            try
            {
                if (blobSize > 0) Marshal.Copy(passBytes, 0, hBlob, blobSize);
                Array.Clear(passBytes, 0, blobSize);

                // CREDENTIAL struct — x64 layout (80 bytes total):
                //  0: DWORD Flags
                //  4: DWORD Type
                //  8: LPWSTR TargetName
                // 16: LPWSTR Comment
                // 24: FILETIME LastWritten (8 bytes)
                // 32: DWORD CredentialBlobSize
                // 36: (4-byte pad to align ptr)
                // 40: LPBYTE CredentialBlob
                // 48: DWORD Persist
                // 52: DWORD AttributeCount
                // 56: ptr Attributes
                // 64: LPWSTR TargetAlias
                // 72: LPWSTR UserName
                const int sz = 80;
                var pCred = Marshal.AllocCoTaskMem(sz);
                try
                {
                    for (int i = 0; i < sz; i++) Marshal.WriteByte(pCred, i, 0);
                    Marshal.WriteInt32(pCred,  0, 0);         // Flags
                    Marshal.WriteInt32(pCred,  4, 1);         // Type = CRED_TYPE_GENERIC
                    Marshal.WriteIntPtr(pCred, 8,  hTarget);  // TargetName
                    Marshal.WriteInt32(pCred, 32, blobSize);  // CredentialBlobSize
                    Marshal.WriteIntPtr(pCred, 40, hBlob);    // CredentialBlob
                    Marshal.WriteInt32(pCred, 48, 3);         // Persist = CRED_PERSIST_ENTERPRISE
                    Marshal.WriteIntPtr(pCred, 72, hUser);    // UserName

                    bool ok = CredWriteW(pCred, 0);
                    Log.Write($"SaveGenericCredential: target={target} ok={ok} err={Marshal.GetLastWin32Error()}");
                }
                finally { Marshal.FreeCoTaskMem(pCred); }
            }
            finally
            {
                for (int i = 0; i < blobSize; i++) Marshal.WriteByte(hBlob, i, 0);
                Marshal.FreeCoTaskMem(hBlob);
                Marshal.FreeCoTaskMem(hTarget);
                Marshal.FreeCoTaskMem(hUser);
            }
        }
        catch (Exception ex) { Log.Write($"SaveGenericCredential ex: {ex.Message}"); }
    }

    // ── Named-pipe IPC with background service ────────────────────────────────

    private static string PipeName
    {
        get
        {
            try
            {
                var sid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
                return $"SftpNetDrive_NP_{sid}";
            }
            catch { return "SftpNetDrive_NP_unknown"; }
        }
    }

    private static string? SendPipeRequest(string message)
    {
        Log.Write($"SendPipeRequest: {message.Split('\t')[0]} pipe={PipeName}");
        EnsureServiceRunning();

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName,
                    PipeDirection.InOut, PipeOptions.None,
                    TokenImpersonationLevel.Identification);
                pipe.Connect(8_000);

                var bytes = Encoding.Unicode.GetBytes(message + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();

                var buf = new byte[4096];
                int len = pipe.Read(buf, 0, buf.Length);
                return Encoding.Unicode.GetString(buf, 0, len).Trim();
            }
            catch (TimeoutException) { Log.Write("SendPipeRequest: timeout"); break; }
            catch (Exception ex)
            {
                Log.Write($"SendPipeRequest attempt {attempt} failed: {ex.Message}");
                if (attempt == 0)
                {
                    EnsureServiceRunning();
                    Thread.Sleep(1000);
                }
            }
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WaitNamedPipeW(string lpNamedPipeName, uint nTimeOut);

    private static bool IsPipeAvailable(int timeoutMs = 100) =>
        WaitNamedPipeW($@"\\.\pipe\{PipeName}", (uint)timeoutMs);

    private static void EnsureServiceRunning()
    {
        // Use WaitNamedPipe to check availability — no ghost connection consumed.
        if (IsPipeAvailable()) return;

        try
        {
            var exePath = GetExePath();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
            });

            // Wait up to 6 s for the pipe to appear (no connecting, just polling)
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(200);
                if (IsPipeAvailable()) return;
            }
        }
        catch { }
    }

    private static string? GetExePath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\SftpNetDrive.exe");
            return key?.GetValue("") as string;
        }
        catch { return null; }
    }
}
