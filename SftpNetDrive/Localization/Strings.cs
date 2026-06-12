using System.Globalization;

namespace SftpNetDrive.Localization;

/// <summary>
/// All user-visible strings. Call Initialize() once at startup before any
/// window is created. XAML binds via {x:Static loc:Strings.Xxx}.
/// </summary>
public static class Strings
{
    private static bool _de;

    public static void Initialize()
    {
        _de = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
    }

    // ── Main window ───────────────────────────────────────────────────────────
    public static string AppTitle          => _de ? "SFTP Net Drive"                                          : "SFTP Net Drive";
    public static string NewConnection     => _de ? "＋  Neue Verbindung"                                    : "＋  New Connection";
    public static string BtnEdit           => _de ? "Bearbeiten"                                             : "Edit";
    public static string BtnDelete         => _de ? "Löschen"                                                : "Delete";
    public static string NoConnections     => _de ? "Keine Verbindungen konfiguriert"                        : "No connections configured";
    public static string NoConnectionsHint => _de ? "Klicken Sie auf \"Neue Verbindung\", um einen SFTP-Server hinzuzufügen." : "Click \"New Connection\" to add an SFTP server.";
    public static string FooterHint        => _de ? "Laufwerke erscheinen als Netzlaufwerke im Windows Explorer." : "Drives appear as Network Drives in Windows Explorer.";
    public static string StartWithWindows  => _de ? "Mit Windows starten"                                    : "Start with Windows";

    // ── Status labels (used in ProfileViewModel) ──────────────────────────────
    public static string StatusMounted    => _de ? "Verbunden"      : "Mounted";
    public static string StatusConnecting => _de ? "Verbinde…"      : "Connecting…";
    public static string StatusError      => _de ? "Fehler"         : "Error";
    public static string StatusUnmounted  => _de ? "Nicht verbunden": "Not mounted";
    public static string BtnMount         => _de ? "Verbinden"      : "Mount";
    public static string BtnUnmount       => _de ? "Trennen"        : "Unmount";

    // ── Edit-profile window ───────────────────────────────────────────────────
    public static string WinConnectionSettings => _de ? "Verbindungseinstellungen" : "Connection Settings";
    public static string LblDisplayName        => _de ? "Anzeigename"              : "Display Name";
    public static string SectionServer         => _de ? "SERVER"                   : "SERVER";
    public static string LblHost               => _de ? "Host"                     : "Host";
    public static string LblPort               => _de ? "Port"                     : "Port";
    public static string LblRemotePath         => _de ? "Remotepfad"               : "Remote Path";
    public static string SectionAuth           => _de ? "AUTHENTIFIZIERUNG"        : "AUTHENTICATION";
    public static string LblUsername           => _de ? "Benutzername"             : "Username";
    public static string LblAuthMethod         => _de ? "Authentifizierungsmethode": "Authentication Method";
    public static string AuthPassword          => _de ? "Kennwort"                 : "Password";
    public static string AuthPrivateKey        => _de ? "Privatschlüsseldatei"     : "Private Key File";
    public static string LblPassword           => _de ? "Kennwort"                 : "Password";
    public static string PasswordHint          => _de ? "Sicher in der Windows-Anmeldeinformationsverwaltung gespeichert." : "Stored securely in Windows Credential Manager.";
    public static string LblKeyFile            => _de ? "Privatschlüsseldatei (.pem / .ppk / OpenSSH)" : "Private Key File (.pem / .ppk / OpenSSH)";
    public static string LblPassphrase         => _de ? "Passphrase (wenn Schlüssel verschlüsselt — leer lassen, wenn keine)" : "Passphrase (if key is encrypted — leave blank if none)";
    public static string BtnBrowse             => _de ? "Durchsuchen…"             : "Browse…";
    public static string SectionDrive          => _de ? "LAUFWERK"                 : "DRIVE";
    public static string LblDriveLetter        => _de ? "Laufwerksbuchstabe"       : "Drive Letter";
    public static string ChkAutoMount          => _de ? "Beim Start automatisch verbinden" : "Mount automatically on startup";
    public static string BtnCancel             => _de ? "Abbrechen"                : "Cancel";
    public static string BtnSave               => _de ? "Speichern"                : "Save";

    // ── Tray menu ─────────────────────────────────────────────────────────────
    public static string TrayOpen => _de ? "SFTP Net Drive öffnen" : "Open SFTP Net Drive";
    public static string TrayExit => _de ? "Beenden"               : "Exit";

    // ── Message boxes ─────────────────────────────────────────────────────────
    public static string AlreadyRunningTitle => _de ? "Bereits gestartet"  : "Already Running";
    public static string AlreadyRunningMsg   => _de
        ? "SFTP Net Drive läuft bereits.\nDas Symbol befindet sich im Infobereich der Taskleiste (rechts unten, auf ^ klicken)."
        : "SFTP Net Drive is already running.\nFind its icon in the system tray (bottom-right, click the ^ arrow).";

    public static string DokanMissingTitle => _de ? "Dokany-Treiber nicht gefunden" : "Dokany Driver Not Found";
    public static string DokanMissingMsg   => _de
        ? "SFTP Net Drive benötigt den Dokany-Treiber, um Laufwerke einzubinden.\n\nMöchten Sie die Download-Seite öffnen?\n\nhttps://github.com/dokan-dev/dokany/releases\n\nInstallieren Sie DokanSetup.exe und starten Sie SFTP Net Drive neu."
        : "SFTP Net Drive requires the Dokany driver to mount virtual drives.\n\nWould you like to open the download page?\n\nhttps://github.com/dokan-dev/dokany/releases\n\nDownload and install DokanSetup.exe, then restart SFTP Net Drive.";

    public static string ErrorTitle => _de ? "Unerwarteter Fehler" : "Unexpected Error";

    public static string MountFailedTitle  => _de ? "Verbindung fehlgeschlagen" : "Mount Failed";
    public static string MountFailedFmt    => _de ? "{0} konnte nicht verbunden werden:\n\n{1}" : "Could not mount {0}:\n\n{1}";

    public static string ConfirmDeleteTitle => _de ? "Löschen bestätigen"  : "Confirm Delete";
    public static string ConfirmDeleteFmt   => _de ? "'{0}' löschen? Das Laufwerk wird getrennt." : "Delete '{0}'? The drive will be unmounted.";

    public static string StartupTitle          => _de ? "Autostart"        : "Startup";
    public static string StartupEnableFailed   => _de ? "Autostart konnte nicht registriert werden. Bitte stellen Sie sicher, dass die App als Administrator ausgeführt wird." : "Could not register startup task. Make sure the app is running as Administrator.";
    public static string StartupDisableFailed  => _de ? "Autostart konnte nicht entfernt werden." : "Could not remove startup task.";

    public static string ValidationTitle  => _de ? "Eingabefehler" : "Validation";
    public static string ValDisplayName   => _de ? "Bitte geben Sie einen Anzeigenamen ein."                      : "Please enter a display name.";
    public static string ValHost          => _de ? "Bitte geben Sie einen Host ein."                              : "Please enter a host.";
    public static string ValPort          => _de ? "Der Port muss eine Zahl zwischen 1 und 65535 sein."           : "Port must be a number between 1 and 65535.";
    public static string ValUsername      => _de ? "Bitte geben Sie einen Benutzernamen ein."                     : "Please enter a username.";
    public static string ValKeyFile       => _de ? "Bitte wählen Sie eine Privatschlüsseldatei aus."              : "Please select a private key file.";
    public static string ValDriveLetter   => _de ? "Bitte wählen Sie einen Laufwerksbuchstaben aus."              : "Please select a drive letter.";

    public static string EditTitlePrefix  => _de ? "Bearbeiten — " : "Edit — ";
    public static string KeyFileFilter    => _de
        ? "Schlüsseldateien (*.pem;*.ppk;*.key;*)|*.pem;*.ppk;*.key;*|Alle Dateien (*.*)|*.*"
        : "Key files (*.pem;*.ppk;*.key;*)|*.pem;*.ppk;*.key;*|All files (*.*)|*.*";
}
