using System.IO;
using DokanNet;
using DokanNet.Logging;
using DokanNet.Native;
using SftpNetDrive.FileSystem;
using SftpNetDrive.Models;

namespace SftpNetDrive.Services;

public class MountService
{
    private readonly Dictionary<string, ActiveMount> _mounts = [];  // key = drive letter ("Z")
    private readonly object _lock = new();

    public bool IsMounted(string letter)
    {
        var key = Norm(letter);
        lock (_lock)
            return _mounts.TryGetValue(key, out var m) && m.Status == MountStatus.Mounted;
    }

    public MountStatus GetStatus(string letter)
    {
        var key = Norm(letter);
        lock (_lock)
            return _mounts.TryGetValue(key, out var m) ? m.Status : MountStatus.Unmounted;
    }

    public async Task<(bool Success, string? Error)> MountAsync(
        ConnectionSpec spec,
        CancellationToken ct = default,
        string? passwordOverride = null)
    {
        var key = Norm(spec.Letter);

        lock (_lock)
        {
            if (_mounts.TryGetValue(key, out var ex) && ex.Status == MountStatus.Mounted)
                return (true, null);
        }

        var secret = passwordOverride ?? CredentialService.Load(spec.Letter) ?? "";

        var mount = new ActiveMount(spec);
        lock (_lock) _mounts[key] = mount;

        mount.Thread = new Thread(() =>
        {
            SftpDokanFs? fs = null;
            try
            {
                fs = new SftpDokanFs(spec, secret);
                mount.FileSystem = fs;

                var dokan    = new Dokan(new NullLogger());
                mount.Dokan  = dokan;

                var instance = new DokanInstanceBuilder(dokan)
                    .ConfigureOptions(opt =>
                    {
                        // NetworkDrive + WNetAddConnection2 makes the drive session-local:
                        // only the current user's logon session sees it in Explorer.
                        // CurrentSession is intentionally omitted — it blocks the Dokan
                        // network-provider callback during mount setup.
                        opt.Options  = DokanOptions.NetworkDrive;
                        opt.MountPoint = $"{spec.Letter}:";
                        // Dokan UNCName must be exactly \Server\Share (2 components).
                        // Strip any subpath — the root offset lives in SftpDokanFs._root.
                        var uncParts = spec.UncPath.TrimStart('\\').Split('\\', 3);
                        opt.UNCName  = uncParts.Length >= 2
                            ? @"\" + uncParts[0] + @"\" + uncParts[1]
                            : spec.UncPath.Replace(@"\\", @"\");
                        opt.AllocationUnitSize = 512;
                        opt.SectorSize         = 512;
                    })
                    .Build(fs);

                mount.Instance = instance;
                mount.Status   = MountStatus.Mounted;
                mount.ReadyTcs.TrySetResult((true, null));
                instance.WaitForFileSystemClosed(uint.MaxValue);
            }
            catch (Exception ex)
            {
                fs?.Dispose();
                var msg = ex.Message;
                lock (_lock) _mounts.Remove(key);
                mount.ReadyTcs.TrySetResult((false, msg));
                return;
            }

            lock (_lock) _mounts.Remove(key);
        });

        mount.Thread.IsBackground = true;
        mount.Thread.Name = $"SFTP-{spec.Letter}:";
        mount.Thread.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { return await mount.ReadyTcs.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { return (false, "Connection timed out."); }
    }

    public void Unmount(string letter)
    {
        var key = Norm(letter);
        ActiveMount? mount;
        lock (_lock) _mounts.TryGetValue(key, out mount);
        if (mount?.Dokan is null) return;

        try { mount.Dokan.RemoveMountPoint($"{mount.Spec.Letter}:"); }
        catch { }
    }

    public void UnmountAll()
    {
        List<string> keys;
        lock (_lock) keys = [.. _mounts.Keys];
        foreach (var k in keys) Unmount(k);
    }

    private static string Norm(string letter) =>
        letter.TrimEnd(':').ToUpperInvariant();

    private sealed class ActiveMount(ConnectionSpec spec)
    {
        public ConnectionSpec Spec    { get; } = spec;
        public MountStatus    Status  { get; set; } = MountStatus.Connecting;
        public Thread?        Thread  { get; set; }
        public SftpDokanFs?   FileSystem { get; set; }
        public Dokan?         Dokan   { get; set; }
        public DokanInstance? Instance{ get; set; }
        public TaskCompletionSource<(bool, string?)> ReadyTcs { get; } = new();
    }
}
