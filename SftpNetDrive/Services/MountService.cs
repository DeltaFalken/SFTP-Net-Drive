using System.IO;
using DokanNet;
using DokanNet.Logging;
using DokanNet.Native;
using SftpNetDrive.FileSystem;
using SftpNetDrive.Models;

namespace SftpNetDrive.Services;

public class MountChangedEventArgs(Guid profileId, MountStatus status, string? error = null) : EventArgs
{
    public Guid ProfileId { get; } = profileId;
    public MountStatus Status { get; } = status;
    public string? Error { get; } = error;
}

public class MountService
{
    private readonly Dictionary<Guid, ActiveMount> _mounts = [];
    private readonly object _lock = new();

    public event EventHandler<MountChangedEventArgs>? MountChanged;

    public MountStatus GetStatus(Guid profileId)
    {
        lock (_lock)
            return _mounts.TryGetValue(profileId, out var m) ? m.Status : MountStatus.Unmounted;
    }

    public bool IsMounted(Guid profileId)
    {
        lock (_lock)
            return _mounts.TryGetValue(profileId, out var m) && m.Status == MountStatus.Mounted;
    }

    public async Task<(bool Success, string? Error)> MountAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_mounts.TryGetValue(profile.Id, out var existing) && existing.Status == MountStatus.Mounted)
                return (true, null);
        }

        var secret = CredentialService.Load(profile.Id) ?? "";

        var mount = new ActiveMount(profile);
        lock (_lock) _mounts[profile.Id] = mount;

        Raise(profile.Id, MountStatus.Connecting);

        mount.Thread = new Thread(() =>
        {
            SftpDokanFs? fs = null;
            try
            {
                // Connect SFTP first — throws on auth failure
                fs = new SftpDokanFs(profile, secret);
                mount.FileSystem = fs;

                var dokan = new Dokan(new NullLogger());
                mount.Dokan = dokan;

                var instance = new DokanInstanceBuilder(dokan)
                    .ConfigureOptions(opt =>
                    {
                        opt.Options = DokanOptions.NetworkDrive;
                        opt.MountPoint = $"{profile.DriveLetter}:";
                        opt.UNCName = $"\\\\SFTP\\{profile.Name.Replace(' ', '_')}";
                        opt.AllocationUnitSize = 512;
                        opt.SectorSize = 512;
                    })
                    .Build(fs);

                mount.Instance = instance;
                mount.Status = MountStatus.Mounted;
                Raise(profile.Id, MountStatus.Mounted);
                mount.ReadyTcs.TrySetResult((true, null));

                instance.WaitForFileSystemClosed(uint.MaxValue);
            }
            catch (Exception ex)
            {
                fs?.Dispose();
                var msg = ex.Message;
                lock (_lock) _mounts.Remove(profile.Id);
                mount.ReadyTcs.TrySetResult((false, msg));
                Raise(profile.Id, MountStatus.Error, msg);
                return;
            }

            lock (_lock) _mounts.Remove(profile.Id);
            Raise(profile.Id, MountStatus.Unmounted);
        });

        mount.Thread.IsBackground = true;
        mount.Thread.Name = $"SFTP-{profile.DriveLetter}:";
        mount.Thread.Start();

        // Wait up to 15 s for connection result
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        try { return await mount.ReadyTcs.Task.WaitAsync(timeoutCts.Token); }
        catch (OperationCanceledException) { return (false, "Connection timed out."); }
    }

    public void Unmount(Guid profileId)
    {
        ActiveMount? mount;
        lock (_lock) _mounts.TryGetValue(profileId, out mount);
        if (mount?.Dokan is null) return;

        try { mount.Dokan.RemoveMountPoint($"{mount.Profile.DriveLetter}:"); }
        catch { }
    }

    public void UnmountAll()
    {
        List<Guid> ids;
        lock (_lock) ids = [.. _mounts.Keys];
        foreach (var id in ids) Unmount(id);
    }

    private void Raise(Guid id, MountStatus status, string? error = null)
    {
        lock (_lock)
            if (_mounts.TryGetValue(id, out var m)) m.Status = status;
        MountChanged?.Invoke(this, new MountChangedEventArgs(id, status, error));
    }

    private sealed class ActiveMount(ConnectionProfile profile)
    {
        public ConnectionProfile Profile { get; } = profile;
        public MountStatus Status { get; set; } = MountStatus.Connecting;
        public Thread? Thread { get; set; }
        public SftpDokanFs? FileSystem { get; set; }
        public Dokan? Dokan { get; set; }
        public DokanInstance? Instance { get; set; }
        public TaskCompletionSource<(bool, string?)> ReadyTcs { get; } = new();
    }
}
