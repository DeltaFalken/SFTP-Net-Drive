using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using SftpNetDrive.Models;
using SftpNetDrive.Services;

namespace SftpNetDrive;

// ── Entry point ───────────────────────────────────────────────────────────────

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Installer hooks — register / remove per-user startup task, then exit.
        if (args.Contains(StartupService.RegisterStartupArg, StringComparer.OrdinalIgnoreCase))
        {
            StartupService.Enable();
            return;
        }
        if (args.Contains(StartupService.UnregisterStartupArg, StringComparer.OrdinalIgnoreCase))
        {
            StartupService.Disable();
            return;
        }

        // Per-user single-instance guard (SID-scoped so Fast User Switch is safe).
        var sid    = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var mutex  = new Mutex(true, $"SftpNetDrive_BG_{sid}", out bool isFirst);
        if (!isFirst)
        {
            mutex.Dispose();
            return;
        }

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new BackgroundContext(sid, args));
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}

// ── ApplicationContext: headless message loop + session watching ───────────────

internal sealed class BackgroundContext : ApplicationContext
{
    private readonly MountService _mounts = new();
    private readonly PipeServer   _pipe;
    private readonly SessionWatcher _watcher;

    public BackgroundContext(string sid, string[] args)
    {
        _pipe    = new PipeServer(_mounts, sid);
        _watcher = new SessionWatcher(_mounts);

        _pipe.Start();

        // Remove the old generic task (no UserId filter) from legacy installs.
        _ = Task.Run(() => StartupService.RemoveLegacyTaskIfExists());

        // Refresh startup task path if it was registered from a different location.
        if (StartupService.IsEnabled())
            _ = Task.Run(() => StartupService.Refresh());

        // Re-mount drives that were active before this logon.
        _ = Task.Run(RestorePersistedMountsAsync);
    }

    private async Task RestorePersistedMountsAsync()
    {
        bool isAutostart = Environment.GetCommandLineArgs()
            .Contains(StartupService.AutostartArg, StringComparer.OrdinalIgnoreCase);

        foreach (var (letter, uncPath) in MountRegistry.GetAll())
        {
            var spec = ConnectionSpec.Parse(letter, uncPath);
            if (spec is null) continue;

            var password = CredentialService.Load(letter) ?? "";
            var (ok, _)  = await _mounts.MountAsync(spec, default, password);
            if (ok || !isAutostart) continue;

            // Retry on failure at startup — network may not be ready yet.
            foreach (var delaySec in new[] { 15, 30, 60 })
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySec));
                if (_mounts.IsMounted(letter)) break;
                (ok, _) = await _mounts.MountAsync(spec, default, password);
                if (ok) break;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pipe.Stop();
            _mounts.UnmountAll();
            _pipe.Dispose();
            _watcher.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ── SessionWatcher: WTS session change notifications ─────────────────────────
//
// Creates a message-only HWND so WPF/WinForms are not required, yet we still
// get WM_WTSSESSION_CHANGE when a Fast User Switch or RDP disconnect happens.

internal sealed class SessionWatcher : IDisposable
{
    private readonly MountService _mounts;
    private Form? _sink;

    // WTS constants
    private const int WM_WTSSESSION_CHANGE   = 0x02B1;
    private const int WTS_CONSOLE_DISCONNECT = 1;
    private const int WTS_CONSOLE_CONNECT    = 2;
    private const int WTS_REMOTE_DISCONNECT  = 4;
    private const int WTS_REMOTE_CONNECT     = 3;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);
    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    public SessionWatcher(MountService mounts)
    {
        _mounts = mounts;

        // WinForms hidden form is the simplest way to get a message pump + HWND.
        _sink = new SinkForm(OnSession) { Visible = false };
        WTSRegisterSessionNotification(_sink.Handle, 0); // NOTIFY_FOR_THIS_SESSION = 0
    }

    private void OnSession(int reason)
    {
        switch (reason)
        {
            case WTS_CONSOLE_DISCONNECT:
            case WTS_REMOTE_DISCONNECT:
                // Synchronous unmount so the new session's Explorer starts clean.
                _mounts.UnmountAll();
                break;

            case WTS_CONSOLE_CONNECT:
            case WTS_REMOTE_CONNECT:
                // Re-mount all drives stored in the registry for this user.
                _ = Task.Run(async () =>
                {
                    foreach (var (letter, uncPath) in MountRegistry.GetAll())
                    {
                        var spec = ConnectionSpec.Parse(letter, uncPath);
                        if (spec is null) continue;
                        var password = CredentialService.Load(letter) ?? "";
                        var (ok, _)  = await _mounts.MountAsync(spec, default, password);
                        if (ok) continue;
                        foreach (var d in new[] { 15, 30, 60 })
                        {
                            await Task.Delay(TimeSpan.FromSeconds(d));
                            if (_mounts.IsMounted(letter)) break;
                            (ok, _) = await _mounts.MountAsync(spec, default, password);
                            if (ok) break;
                        }
                    }
                });
                break;
        }
    }

    public void Dispose()
    {
        if (_sink is not null)
        {
            WTSUnRegisterSessionNotification(_sink.Handle);
            _sink.Dispose();
            _sink = null;
        }
    }

    // Minimal Form that forwards WM_WTSSESSION_CHANGE to the callback.
    private sealed class SinkForm(Action<int> onSession) : Form
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_WTSSESSION_CHANGE)
                onSession(m.WParam.ToInt32());
            base.WndProc(ref m);
        }

        protected override void SetVisibleCore(bool value) =>
            base.SetVisibleCore(false); // never show the window
    }
}
