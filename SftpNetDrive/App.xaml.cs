using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using SftpNetDrive.Localization;
using SftpNetDrive.Services;
using SftpNetDrive.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using StartupEventArgs = System.Windows.StartupEventArgs;
using WindowState = System.Windows.WindowState;
using WinForms = System.Windows.Forms;

namespace SftpNetDrive;

public partial class App : Application
{
    private const string ActivationEventName = "SftpNetDrive_ActivateWindow";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activationEvent;
    private static RegisteredWaitHandle? _activationWaitHandle;
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _mainWindow;
    private readonly ProfileRepository _repo = new();
    private readonly MountService _mounts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Strings.Initialize();

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.ToString(), Strings.ErrorTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        bool isAutostart = e.Args.Contains(StartupService.AutostartArg, StringComparer.OrdinalIgnoreCase);

        // Single-instance guard + activation on manual launch
        _mutex = new Mutex(true, "SftpNetDrive_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            if (!isAutostart)
            {
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(ActivationEventName);
                    existing.Set();
                }
                catch { }
            }

            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationWaitHandle = ThreadPool.RegisterWaitForSingleObject(_activationEvent, (_, __) =>
        {
            Dispatcher.Invoke(ShowMainWindow);
        }, null, -1, executeOnlyOnce: false);

        if (!EnsureDokanOrWarn()) return;
        BuildTrayIcon();

        if (StartupService.IsEnabled())
            _ = Task.Run(() => StartupService.Refresh());

        _mainWindow = new MainWindow(_repo, _mounts);
        _mainWindow.Closed += (_, _) => _mainWindow = null;

        if (!isAutostart)
            _mainWindow.Show();

        // Auto-mount profiles flagged for startup.
        // In autostart mode the network may not be ready yet, so retry on failure.
        foreach (var profile in _repo.Profiles.Where(p => p.AutoMount))
            _ = MountWithRetryAsync(profile, retryOnFailure: isAutostart);
    }

    // ── Auto-mount with retry ─────────────────────────────────────────────────

    // Delays between retry attempts (seconds). Only used when retryOnFailure=true.
    private static readonly int[] RetryDelays = [15, 30, 60];

    private async Task MountWithRetryAsync(SftpNetDrive.Models.ConnectionProfile profile, bool retryOnFailure)
    {
        var (ok, _) = await _mounts.MountAsync(profile);
        if (ok || !retryOnFailure) return;

        foreach (var delaySec in RetryDelays)
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySec));
            if (_mounts.IsMounted(profile.Id)) return;  // manually connected in the meantime
            (ok, _) = await _mounts.MountAsync(profile);
            if (ok) return;
        }
    }

    // ── Tray icon (native WinForms — 100% reliable) ───────────────────────────

    private void BuildTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();

        var openItem = new WinForms.ToolStripMenuItem(Strings.TrayOpen)
        {
            Font = new System.Drawing.Font(
                System.Drawing.SystemFonts.MenuFont ?? System.Drawing.SystemFonts.DefaultFont,
                System.Drawing.FontStyle.Bold)
        };
        openItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exitItem = new WinForms.ToolStripMenuItem(Strings.TrayExit);
        exitItem.Click += (_, _) => DoExit();
        menu.Items.Add(exitItem);

        _tray = new WinForms.NotifyIcon
        {
            Icon = GetTrayIcon(),
            Text = "SFTP Net Drive",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    private static System.Drawing.Icon GetTrayIcon()
    {
        // Extract the "network drive" icon out of Windows' own shell32.dll
        try
        {
            var shell32 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");
            var small = new IntPtr[1];
            ExtractIconEx(shell32, 9, null, small, 1);          // index 9 = network drive
            if (small[0] != IntPtr.Zero)
            {
                var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(small[0]).Clone();
                DestroyIcon(small[0]);
                return icon;
            }
        }
        catch { }

        return System.Drawing.SystemIcons.Application;
    }

    // ── Window management ─────────────────────────────────────────────────────

    internal void ShowMainWindow()
    {
        // Must dispatch to the WPF UI thread (DoubleClick arrives on WinForms thread)
        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            if (_mainWindow is null || !_mainWindow.IsLoaded)
                _mainWindow = new MainWindow(_repo, _mounts);

            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mounts.UnmountAll();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _activationWaitHandle?.Unregister(null);
        _activationEvent?.Dispose();
        _mutex?.ReleaseMutex();
        base.OnExit(e);
    }

    internal void RemoveTrayIcon()
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
    }

    private void DoExit()
    {
        Shutdown();
    }

    // ── Dokany check ─────────────────────────────────────────────────────────

    private static bool EnsureDokanOrWarn()
    {
        var dokanDll = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "dokan2.dll");

        if (File.Exists(dokanDll))
        {
            if (IsDokanNativeArch(dokanDll)) return true;

            var arch = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
            MessageBox.Show(string.Format(Strings.DokanWrongArchFmt, arch),
                Strings.DokanMissingTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            Current.Shutdown();
            return false;
        }

        var result = MessageBox.Show(Strings.DokanMissingMsg, Strings.DokanMissingTitle,
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/dokan-dev/dokany/releases",
                UseShellExecute = true
            });

        Current.Shutdown();
        return false;
    }

    private static bool IsDokanNativeArch(string dllPath)
    {
        try
        {
            using var fs = File.OpenRead(dllPath);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) return false;   // no MZ header
            fs.Seek(0x3C, SeekOrigin.Begin);
            fs.Seek(br.ReadInt32(), SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550) return false; // no PE signature
            ushort machine = br.ReadUInt16();
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64   => machine == 0x8664, // IMAGE_FILE_MACHINE_AMD64
                Architecture.Arm64 => machine == 0xAA64, // IMAGE_FILE_MACHINE_ARM64
                _                  => true
            };
        }
        catch { return true; }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint ExtractIconEx(
        string szFileName, int nIconIndex,
        IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

// ── Shared RelayCommand ───────────────────────────────────────────────────────

public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
