using System.Windows;
using SftpNetDrive.Localization;
using SftpNetDrive.Models;
using SftpNetDrive.Services;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace SftpNetDrive.Windows;

public partial class EditProfileWindow : Window
{
    private readonly Guid _existingId;

    public ConnectionProfile? Result { get; private set; }
    public string Secret { get; private set; } = "";

    // New connection
    public EditProfileWindow()
    {
        InitializeComponent();
        _existingId = Guid.NewGuid();
        PopulateDriveLetters();
    }

    // Edit existing connection
    public EditProfileWindow(ConnectionProfile existing) : this()
    {
        _existingId = existing.Id;
        Title = Strings.EditTitlePrefix + existing.Name;

        NameBox.Text = existing.Name;
        HostBox.Text = existing.Host;
        PortBox.Text = existing.Port.ToString();
        RemotePathBox.Text = existing.RemotePath;
        UsernameBox.Text = existing.Username;
        AutoMountBox.IsChecked = existing.AutoMount;

        if (existing.AuthMethod == AuthMethod.PrivateKey)
        {
            AuthMethodBox.SelectedIndex = 1;
            KeyPathBox.Text = existing.PrivateKeyPath ?? "";
            PasswordPanel.Visibility = Visibility.Collapsed;
            KeyPanel.Visibility = Visibility.Visible;
        }

        // Pre-select drive letter
        foreach (var item in DriveLetterBox.Items.Cast<System.Windows.Controls.ComboBoxItem>())
            if (item.Tag is string l && l[0] == existing.DriveLetter)
            { DriveLetterBox.SelectedItem = item; break; }

        // Load existing password/passphrase hint (we don't show the actual secret)
        var stored = CredentialService.Load(existing.Id);
        if (stored is not null)
            PasswordBox.Password = stored.Length > 0 ? "••••••••" : "";
    }

    private void PopulateDriveLetters()
    {
        var usedLetters = System.IO.DriveInfo.GetDrives()
            .Select(d => d.Name[0])
            .ToHashSet();

        // Prefer letters not currently in use
        var letters = Enumerable.Range('D', 23)
            .Select(i => (char)i)
            .OrderBy(c => usedLetters.Contains(c) ? 1 : 0)
            .ThenBy(c => c);

        foreach (var letter in letters)
        {
            var item = new System.Windows.Controls.ComboBoxItem
            {
                Content = $"{letter}:",
                Tag = letter.ToString()
            };
            DriveLetterBox.Items.Add(item);
        }
        DriveLetterBox.SelectedIndex = 0;
    }

    private void AuthMethod_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Guard: this fires during InitializeComponent() before x:Name fields are assigned
        if (PasswordPanel is null || KeyPanel is null) return;
        if (AuthMethodBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        var isKey = item.Tag as string == "PrivateKey";
        PasswordPanel.Visibility = isKey ? Visibility.Collapsed : Visibility.Visible;
        KeyPanel.Visibility = isKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Strings.LblKeyFile,
            Filter = Strings.KeyFileFilter
        };
        if (dlg.ShowDialog(this) == true)
            KeyPathBox.Text = dlg.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;

        var isKey = AuthMethodBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: "PrivateKey" };
        var driveLetter = ((System.Windows.Controls.ComboBoxItem)DriveLetterBox.SelectedItem).Tag.ToString()![0];

        Result = new ConnectionProfile
        {
            Id = _existingId,
            Name = NameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Port = int.TryParse(PortBox.Text, out var port) ? port : 22,
            Username = UsernameBox.Text.Trim(),
            RemotePath = RemotePathBox.Text.Trim().Replace('\\', '/'),
            DriveLetter = driveLetter,
            AutoMount = AutoMountBox.IsChecked == true,
            AuthMethod = isKey ? AuthMethod.PrivateKey : AuthMethod.Password,
            PrivateKeyPath = isKey ? KeyPathBox.Text.Trim() : null,
        };

        // Only update stored secret if user typed something new
        if (isKey)
        {
            var pass = PassphraseBox.Password;
            if (!string.IsNullOrEmpty(pass) && !pass.All(c => c == '•'))
                Secret = pass;
        }
        else
        {
            var pass = PasswordBox.Password;
            if (!string.IsNullOrEmpty(pass) && !pass.All(c => c == '•'))
                Secret = pass;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private bool Validate()
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        { Warn(Strings.ValDisplayName); NameBox.Focus(); return false; }
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        { Warn(Strings.ValHost); HostBox.Focus(); return false; }
        if (!int.TryParse(PortBox.Text, out var p) || p < 1 || p > 65535)
        { Warn(Strings.ValPort); PortBox.Focus(); return false; }
        if (string.IsNullOrWhiteSpace(UsernameBox.Text))
        { Warn(Strings.ValUsername); UsernameBox.Focus(); return false; }

        var isKey = AuthMethodBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: "PrivateKey" };
        if (isKey && string.IsNullOrWhiteSpace(KeyPathBox.Text))
        { Warn(Strings.ValKeyFile); return false; }

        if (DriveLetterBox.SelectedItem is null)
        { Warn(Strings.ValDriveLetter); return false; }

        return true;
    }

    private void Warn(string msg) =>
        MessageBox.Show(this, msg, Strings.ValidationTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
}
