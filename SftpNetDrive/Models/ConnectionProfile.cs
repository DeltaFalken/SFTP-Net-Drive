namespace SftpNetDrive.Models;

public enum AuthMethod { Password, PrivateKey }

public enum MountStatus { Unmounted, Connecting, Mounted, Error }

public class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string RemotePath { get; set; } = "/";
    public char DriveLetter { get; set; } = 'Z';
    public bool AutoMount { get; set; }
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;
    public string? PrivateKeyPath { get; set; }
}
