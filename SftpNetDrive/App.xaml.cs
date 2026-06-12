using System.IO;
using System.Runtime.InteropServices;
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
    private static Mutex? _mutex;
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

        // Single-instance guard
        _mutex = new Mutex(true, "SftpNetDrive_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show(Strings.AlreadyRunningMsg, Strings.AlreadyRunningTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        EnsureDokanOrWarn();
        BuildTrayIcon();

        // Create window but do NOT show it — user opens via tray
        _mainWindow = new MainWindow(_repo, _mounts);

        // Auto-mount profiles flagged for startup
        foreach (var profile in _repo.Profiles.Where(p => p.AutoMount))
            _ = _mounts.MountAsync(profile);
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
            if (_mainWindow is null) return;
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        });
    }

    private void DoExit()
    {
        _mounts.UnmountAll();
        _tray?.Dispose();
        _mutex?.ReleaseMutex();
        Dispatcher.Invoke(Shutdown);
    }

    // ── Dokany check ─────────────────────────────────────────────────────────

    private static void EnsureDokanOrWarn()
    {
        var dokanDll = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "dokan2.dll");
        if (File.Exists(dokanDll)) return;

        var result = MessageBox.Show(Strings.DokanMissingMsg, Strings.DokanMissingTitle,
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/dokan-dev/dokany/releases",
                UseShellExecute = true
            });

        Current.Shutdown();
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
