using System.ComponentModel;
using System.Windows;
using SftpNetDrive.Localization;
using SftpNetDrive.Models;
using SftpNetDrive.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SftpNetDrive.Windows;

public partial class MainWindow : Window
{
    private readonly ProfileRepository _repo;
    private readonly MountService _mounts;
    private readonly List<ProfileViewModel> _vms = [];

    public MainWindow(ProfileRepository repo, MountService mounts)
    {
        InitializeComponent();
        _repo = repo;
        _mounts = mounts;
        _mounts.MountChanged += OnMountChanged;
        Refresh();
        // Reflect current startup registration state without triggering the event
        StartupCheckBox.IsChecked = StartupService.IsEnabled();
    }

    private void Refresh()
    {
        _vms.Clear();
        foreach (var p in _repo.Profiles)
            _vms.Add(new ProfileViewModel(p, _mounts.GetStatus(p.Id)));
        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = _vms;
        EmptyState.Visibility = _vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMountChanged(object? sender, MountChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var vm = _vms.FirstOrDefault(v => v.Profile.Id == e.ProfileId);
            if (vm is null) return;
            vm.Status = e.Status;
            vm.ErrorText = e.Error ?? "";
        });
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new EditProfileWindow() { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _repo.Add(dlg.Result!);
        if (!string.IsNullOrEmpty(dlg.Secret))
            CredentialService.Save(dlg.Result!.Id, dlg.Secret);
        Refresh();
        if (dlg.Result!.AutoMount) _ = MountAsync(dlg.Result);
    }

    private void MountToggle_Click(object sender, RoutedEventArgs e)
    {
        if (((System.Windows.Controls.Button)sender).Tag is not ProfileViewModel vm) return;
        if (vm.Status == MountStatus.Mounted) _mounts.Unmount(vm.Profile.Id);
        else _ = MountAsync(vm.Profile);
    }

    private async Task MountAsync(ConnectionProfile profile)
    {
        var vm = _vms.FirstOrDefault(v => v.Profile.Id == profile.Id);
        if (vm is not null) vm.Status = MountStatus.Connecting;

        var (ok, error) = await _mounts.MountAsync(profile);
        if (!ok)
        {
            MessageBox.Show(this,
                string.Format(Strings.MountFailedFmt, profile.Name, error),
                Strings.MountFailedTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (((System.Windows.Controls.Button)sender).Tag is not ProfileViewModel vm) return;
        var dlg = new EditProfileWindow(vm.Profile) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _repo.Update(dlg.Result!);
        if (!string.IsNullOrEmpty(dlg.Secret))
            CredentialService.Save(dlg.Result!.Id, dlg.Secret);
        Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((System.Windows.Controls.Button)sender).Tag is not ProfileViewModel vm) return;
        var r = MessageBox.Show(this,
            string.Format(Strings.ConfirmDeleteFmt, vm.Profile.Name),
            Strings.ConfirmDeleteTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        _mounts.Unmount(vm.Profile.Id);
        _repo.Remove(vm.Profile.Id);
        Refresh();
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        bool enable = StartupCheckBox.IsChecked == true;
        bool ok = enable ? StartupService.Enable() : StartupService.Disable();
        if (!ok)
        {
            MessageBox.Show(this,
                enable ? Strings.StartupEnableFailed : Strings.StartupDisableFailed,
                Strings.StartupTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            // Revert checkbox
            StartupCheckBox.IsChecked = !enable;
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        e.Cancel = true; // minimize to tray instead of closing
        Hide();
    }
}

// ── ViewModel ─────────────────────────────────────────────────────────────────

public sealed class ProfileViewModel(ConnectionProfile profile, MountStatus status) : INotifyPropertyChanged
{
    public ConnectionProfile Profile { get; } = profile;

    public string DriveLetter => Profile.DriveLetter + ":";

    private MountStatus _status = status;
    public MountStatus Status
    {
        get => _status;
        set { _status = value; NotifyAll(); }
    }

    private string _errorText = "";
    public string ErrorText
    {
        get => _errorText;
        set { _errorText = value; OnPropertyChanged(nameof(ErrorText)); OnPropertyChanged(nameof(ErrorVisibility)); }
    }

    public string StatusText => _status switch
    {
        MountStatus.Mounted => Strings.StatusMounted,
        MountStatus.Connecting => Strings.StatusConnecting,
        MountStatus.Error => Strings.StatusError,
        _ => Strings.StatusUnmounted
    };

    public Brush StatusColor => _status switch
    {
        MountStatus.Mounted => new SolidColorBrush(Color.FromRgb(15, 123, 15)),
        MountStatus.Connecting => new SolidColorBrush(Color.FromRgb(232, 160, 0)),
        MountStatus.Error => new SolidColorBrush(Color.FromRgb(196, 43, 28)),
        _ => new SolidColorBrush(Color.FromRgb(118, 118, 118)),
    };

    public string MountButtonText => _status == MountStatus.Mounted ? Strings.BtnUnmount : Strings.BtnMount;
    public bool CanToggleMount => _status is MountStatus.Mounted or MountStatus.Unmounted or MountStatus.Error;
    public Visibility ErrorVisibility => string.IsNullOrEmpty(_errorText) ? Visibility.Collapsed : Visibility.Visible;

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(MountButtonText));
        OnPropertyChanged(nameof(CanToggleMount));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
