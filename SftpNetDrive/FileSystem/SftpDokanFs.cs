using System.IO;
using System.Security.AccessControl;
using DokanNet;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using SftpNetDrive.Models;
using FileAccess = DokanNet.FileAccess;
using FileAttributes = System.IO.FileAttributes;
using FileMode = System.IO.FileMode;
using FileOptions = System.IO.FileOptions;
using FileShare = System.IO.FileShare;

namespace SftpNetDrive.FileSystem;

/// <summary>
/// Dokan filesystem that forwards all operations to an SFTP server via SSH.NET.
/// Mounts as a Windows Network Drive; the OS and shell see it identically to
/// a mapped \\server\share drive.
/// </summary>
public sealed class SftpDokanFs : IDokanOperations, IDisposable
{
    private readonly ConnectionProfile _profile;
    private readonly SftpClient _sftp;
    private readonly string _root;   // e.g. "/home/user"
    private readonly object _sftpLock = new();
    private bool _disposed;

    // Attribute + directory listing cache to hide SFTP latency
    private readonly Dictionary<string, CacheEntry> _attrCache = [];
    private readonly Dictionary<string, ListEntry> _listCache = [];
    private readonly object _cacheLock = new();
    private static readonly TimeSpan AttrTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(3);

    public SftpDokanFs(ConnectionProfile profile, string secret)
    {
        _profile = profile;
        _root = profile.RemotePath.TrimEnd('/');
        if (string.IsNullOrEmpty(_root)) _root = "";

        AuthenticationMethod auth = profile.AuthMethod == Models.AuthMethod.PrivateKey
            ? new PrivateKeyAuthenticationMethod(profile.Username,
                string.IsNullOrEmpty(secret)
                    ? new PrivateKeyFile(profile.PrivateKeyPath!)
                    : new PrivateKeyFile(profile.PrivateKeyPath!, secret))
            : new PasswordAuthenticationMethod(profile.Username, secret);

        var connInfo = new ConnectionInfo(profile.Host, profile.Port, profile.Username, auth);
        _sftp = new SftpClient(connInfo);
        _sftp.Connect();
        _sftp.KeepAliveInterval = TimeSpan.FromSeconds(30);
    }

    // ── Path helpers ─────────────────────────────────────────────────────────

    private string Remote(string dokanPath)
    {
        var p = dokanPath.Replace('\\', '/');
        return p == "/" ? (_root == "" ? "/" : _root) : _root + p;
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private SftpFileAttributes? GetCached(string remote)
    {
        lock (_cacheLock)
        {
            if (_attrCache.TryGetValue(remote, out var e) && e.Expiry > DateTime.UtcNow)
                return e.Attrs;
            _attrCache.Remove(remote);
            return null;
        }
    }

    private void PutCached(string remote, SftpFileAttributes attrs)
    {
        lock (_cacheLock)
            _attrCache[remote] = new CacheEntry(attrs, DateTime.UtcNow + AttrTtl);
    }

    private void Invalidate(string remote)
    {
        lock (_cacheLock)
        {
            _attrCache.Remove(remote);
            _listCache.Remove(remote);
            var parent = remote.Contains('/') ? remote[..remote.LastIndexOf('/')] : "/";
            if (string.IsNullOrEmpty(parent)) parent = "/";
            _listCache.Remove(parent);
        }
    }

    private SftpFileAttributes? StatRemote(string remote)
    {
        var cached = GetCached(remote);
        if (cached != null) return cached;
        try
        {
            SftpFileAttributes attrs;
            lock (_sftpLock) attrs = _sftp.GetAttributes(remote);
            PutCached(remote, attrs);
            return attrs;
        }
        catch { return null; }
    }

    // ── Attribute → Dokan FileInformation ────────────────────────────────────

    private static FileInformation MakeInfo(string name, SftpFileAttributes a) => new()
    {
        FileName = name,
        Attributes = a.IsDirectory
            ? System.IO.FileAttributes.Directory
            : System.IO.FileAttributes.Normal,
        Length = a.IsDirectory ? 0 : a.Size,
        CreationTime = a.LastWriteTime,
        LastWriteTime = a.LastWriteTime,
        LastAccessTime = a.LastAccessTime,
    };

    // ═══════════════════════════════════════════════════════════════════════════
    // IDokanOperations
    // ═══════════════════════════════════════════════════════════════════════════

    public NtStatus CreateFile(
        string fileName, FileAccess access, FileShare share,
        FileMode mode, FileOptions options, FileAttributes attributes,
        IDokanFileInfo info)
    {
        var remote = Remote(fileName);

        // ── Directories ──────────────────────────────────────────────────────
        if (info.IsDirectory)
        {
            if (mode == FileMode.CreateNew || mode == FileMode.Create)
            {
                try
                {
                    lock (_sftpLock) _sftp.CreateDirectory(remote);
                    Invalidate(remote);
                }
                catch (Exception ex) when (ex.Message.Contains("exist", StringComparison.OrdinalIgnoreCase))
                {
                    if (mode == FileMode.CreateNew) return DokanResult.AlreadyExists;
                }
                catch { return DokanResult.AccessDenied; }
            }
            return DokanResult.Success;
        }

        // ── Files ────────────────────────────────────────────────────────────
        var attrs = StatRemote(remote);
        bool exists = attrs != null;

        if (attrs?.IsDirectory == true)
        {
            info.IsDirectory = true;
            return DokanResult.Success;
        }

        switch (mode)
        {
            case FileMode.Open when !exists: return DokanResult.FileNotFound;
            case FileMode.CreateNew when exists: return DokanResult.AlreadyExists;
            case FileMode.Truncate when !exists: return DokanResult.FileNotFound;
        }

        // If no data-plane access is requested (metadata only) skip opening stream
        const FileAccess dataAccess = FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData
            | FileAccess.GenericRead | FileAccess.GenericWrite | FileAccess.GenericAll;
        if ((access & dataAccess) == 0)
        {
            info.Context = new SftpHandle(remote);
            return DokanResult.Success;
        }

        try
        {
            bool wantWrite = (access & (FileAccess.WriteData | FileAccess.GenericWrite | FileAccess.GenericAll | FileAccess.AppendData)) != 0;
            bool wantRead = (access & (FileAccess.ReadData | FileAccess.GenericRead | FileAccess.GenericAll)) != 0;

            var ioAccess = (wantRead, wantWrite) switch
            {
                (true, true) => System.IO.FileAccess.ReadWrite,
                (false, true) => System.IO.FileAccess.Write,
                _ => System.IO.FileAccess.Read
            };

            var ioMode = mode == FileMode.Append ? FileMode.OpenOrCreate : mode;

            SftpFileStream stream;
            lock (_sftpLock) stream = _sftp.Open(remote, ioMode, ioAccess);

            if (mode == FileMode.Append) { lock (stream) stream.Seek(0, SeekOrigin.End); }

            info.Context = new SftpHandle(remote, stream);
            Invalidate(remote);
            return DokanResult.Success;
        }
        catch (Exception ex) when (ex.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return DokanResult.FileNotFound;
        }
        catch { return DokanResult.AccessDenied; }
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        if (info.Context is SftpHandle h)
        {
            h.Stream?.Close();
            h.Stream?.Dispose();
        }

        if (info.DeleteOnClose)
        {
            var remote = Remote(fileName);
            try
            {
                lock (_sftpLock)
                {
                    if (info.IsDirectory) _sftp.DeleteDirectory(remote);
                    else _sftp.DeleteFile(remote);
                }
                Invalidate(remote);
            }
            catch { }
        }
    }

    public void CloseFile(string fileName, IDokanFileInfo info) =>
        info.Context = null;

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        if (info.Context is SftpHandle { Stream: { } stream })
        {
            lock (stream)
            {
                stream.Seek(offset, SeekOrigin.Begin);
                bytesRead = stream.Read(buffer, 0, buffer.Length);
            }
            return DokanResult.Success;
        }

        // Handle-less read (e.g. memory-mapped access)
        var remote = Remote(fileName);
        try
        {
            lock (_sftpLock)
            using (var s = _sftp.OpenRead(remote))
            {
                s.Seek(offset, SeekOrigin.Begin);
                bytesRead = s.Read(buffer, 0, buffer.Length);
            }
            return DokanResult.Success;
        }
        catch { return DokanResult.AccessDenied; }
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        if (info.Context is not SftpHandle { Stream: { } stream })
            return DokanResult.AccessDenied;

        lock (stream)
        {
            if (info.WriteToEndOfFile)
                stream.Seek(0, SeekOrigin.End);
            else
                stream.Seek(offset, SeekOrigin.Begin);

            stream.Write(buffer, 0, buffer.Length);
            bytesWritten = buffer.Length;
        }
        Invalidate(Remote(fileName));
        return DokanResult.Success;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        if (info.Context is SftpHandle { Stream: { } stream })
            lock (stream) stream.Flush();
        return DokanResult.Success;
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        if (fileName == "\\")
        {
            fileInfo = new FileInformation
            {
                FileName = "\\",
                Attributes = System.IO.FileAttributes.Directory,
                CreationTime = DateTime.Now,
                LastWriteTime = DateTime.Now,
                LastAccessTime = DateTime.Now,
            };
            return DokanResult.Success;
        }

        var attrs = StatRemote(Remote(fileName));
        if (attrs is null) { fileInfo = default; return DokanResult.FileNotFound; }

        fileInfo = MakeInfo(Path.GetFileName(fileName), attrs);
        return DokanResult.Success;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
        => FindFilesWithPattern(fileName, "*", out files, info);

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = [];
        var remote = Remote(fileName);

        List<ISftpFile>? cached = null;
        lock (_cacheLock)
        {
            if (_listCache.TryGetValue(remote, out var le) && le.Expiry > DateTime.UtcNow)
                cached = le.Files;
        }

        if (cached is null)
        {
            try
            {
                IEnumerable<ISftpFile> listing;
                lock (_sftpLock) listing = _sftp.ListDirectory(remote).ToList();
                cached = [.. listing];
                lock (_cacheLock)
                    _listCache[remote] = new ListEntry(cached, DateTime.UtcNow + ListTtl);
            }
            catch { return DokanResult.AccessDenied; }
        }

        foreach (var f in cached)
        {
            if (f.Name is "." or "..") continue;
            if (searchPattern != "*" && !DokanHelper.DokanIsNameInExpression(searchPattern, f.Name, true))
                continue;

            files.Add(MakeInfo(f.Name, f.Attributes));
            PutCached(remote + "/" + f.Name, f.Attributes);
        }

        return DokanResult.Success;
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        => DokanResult.Success; // SFTP doesn't map to Windows file attributes

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
    {
        var remote = Remote(fileName);
        try
        {
            SftpFileAttributes attrs;
            lock (_sftpLock) attrs = _sftp.GetAttributes(remote);
            if (lastAccessTime.HasValue) attrs.LastAccessTime = lastAccessTime.Value;
            if (lastWriteTime.HasValue) attrs.LastWriteTime = lastWriteTime.Value;
            lock (_sftpLock) _sftp.SetAttributes(remote, attrs);
            Invalidate(remote);
        }
        catch { /* best-effort */ }
        return DokanResult.Success;
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        var attrs = StatRemote(Remote(fileName));
        if (attrs is null) return DokanResult.FileNotFound;
        if (attrs.IsDirectory) return DokanResult.AccessDenied;
        return DokanResult.Success; // actual delete happens in Cleanup(DeleteOnClose)
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        var remote = Remote(fileName);
        try
        {
            List<ISftpFile> listing;
            lock (_sftpLock) listing = [.. _sftp.ListDirectory(remote)];
            if (listing.Any(f => f.Name is not "." and not ".."))
                return DokanResult.DirectoryNotEmpty;
            return DokanResult.Success;
        }
        catch { return DokanResult.FileNotFound; }
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        var oldRemote = Remote(oldName);
        var newRemote = Remote(newName);
        try
        {
            if (!replace)
            {
                var existing = StatRemote(newRemote);
                if (existing != null) return DokanResult.AlreadyExists;
            }
            else
            {
                // SFTP rename won't overwrite; delete target first
                var existing = StatRemote(newRemote);
                if (existing != null)
                    lock (_sftpLock)
                    {
                        if (existing.IsDirectory) _sftp.DeleteDirectory(newRemote);
                        else _sftp.DeleteFile(newRemote);
                    }
            }

            lock (_sftpLock) _sftp.RenameFile(oldRemote, newRemote);
            Invalidate(oldRemote);
            Invalidate(newRemote);
            return DokanResult.Success;
        }
        catch { return DokanResult.AccessDenied; }
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        if (info.Context is SftpHandle { Stream: { } stream })
            lock (stream) try { stream.SetLength(length); } catch { /* best-effort */ }
        return DokanResult.Success;
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        => DokanResult.Success;

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        => DokanResult.Success; // SFTP has no advisory lock; pretend success

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        => DokanResult.Success;

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        try
        {
            Renci.SshNet.Sftp.SftpFileSystemInformation di;
            lock (_sftpLock) di = _sftp.GetStatus(_root == "" ? "/" : _root);
            var blockSize = (long)di.FileSystemBlockSize;
            totalNumberOfBytes = (long)di.TotalBlocks * blockSize;
            freeBytesAvailable = (long)di.AvailableBlocks * blockSize;
            totalNumberOfFreeBytes = (long)di.FreeBlocks * blockSize;
        }
        catch
        {
            // Fallback if server doesn't support statvfs
            totalNumberOfBytes = 100L << 30;
            freeBytesAvailable = 50L << 30;
            totalNumberOfFreeBytes = 50L << 30;
        }
        return DokanResult.Success;
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = _profile.Name;
        features = FileSystemFeatures.CaseSensitiveSearch
            | FileSystemFeatures.CasePreservedNames
            | FileSystemFeatures.UnicodeOnDisk;
        fileSystemName = "SFTP";
        maximumComponentLength = 255;
        return DokanResult.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
    {
        security = null!;
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        => DokanResult.Success;

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus Unmounted(IDokanFileInfo info)
    {
        Dispose();
        return DokanResult.Success;
    }

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = [];
        return DokanResult.NotImplemented;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _sftp.Disconnect(); } catch { }
        _sftp.Dispose();
    }

    // ── Cache types ───────────────────────────────────────────────────────────

    private readonly record struct CacheEntry(SftpFileAttributes Attrs, DateTime Expiry);
    private sealed class ListEntry(List<ISftpFile> files, DateTime expiry)
    {
        public List<ISftpFile> Files { get; } = files;
        public DateTime Expiry { get; } = expiry;
    }
}

// ── Per-file handle ───────────────────────────────────────────────────────────

internal sealed class SftpHandle(string remotePath, SftpFileStream? stream = null)
{
    public string RemotePath { get; } = remotePath;
    public SftpFileStream? Stream { get; } = stream;
}
