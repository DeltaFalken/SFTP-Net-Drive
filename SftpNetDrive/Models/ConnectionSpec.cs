namespace SftpNetDrive.Models;

public enum MountStatus { Unmounted, Connecting, Mounted, Error }

/// <summary>
/// All information needed to establish one SFTP mount.
/// Parsed from the UNC share name: user@server.com!22
/// </summary>
public sealed class ConnectionSpec
{
    /// <summary>Drive letter without colon, e.g. "Z".</summary>
    public string Letter    { get; init; } = "";
    public string Host      { get; init; } = "";
    public int    Port      { get; init; } = 22;
    public string Username  { get; init; } = "";
    /// <summary>Remote directory to mount, defaults to "/".</summary>
    public string RemotePath{ get; init; } = "/";
    /// <summary>Full UNC path, e.g. \\SftpNetDrive\user@server.com!22</summary>
    public string UncPath   { get; init; } = "";

    // ── Parsing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a ConnectionSpec from a drive letter, the full UNC remote path, and
    /// an optional SSH username that overrides the one encoded in the UNC path.
    /// UNC format: \\SftpNetDrive\[user@]host[!port]
    /// </summary>
    public static ConnectionSpec? Parse(string letter, string uncPath, string? usernameOverride = null)
    {
        // Extract the share part: \\SftpNetDrive\SHARE[\subpath]  →  SHARE, /subpath
        var stripped = uncPath.TrimStart('\\');
        var slash    = stripped.IndexOf('\\');
        if (slash < 0) return null;
        var shareAndPath = stripped[(slash + 1)..];
        if (string.IsNullOrWhiteSpace(shareAndPath)) return null;

        // Split SHARE from optional subpath: "user@host!port\home\user" → share="user@host!port", remotePath="/home/user"
        string share;
        string remotePath = "/";
        var subSlash = shareAndPath.IndexOf('\\');
        if (subSlash > 0)
        {
            share      = shareAndPath[..subSlash];
            remotePath = "/" + shareAndPath[(subSlash + 1)..].Replace('\\', '/').Trim('/');
        }
        else
        {
            share = shareAndPath;
        }

        string username, hostPort;
        var atIdx = share.IndexOf('@');
        if (atIdx > 0)
        {
            username = share[..atIdx];
            hostPort = share[(atIdx + 1)..];
        }
        else
        {
            username = "";
            hostPort = share;
        }

        if (!string.IsNullOrEmpty(usernameOverride))
            username = usernameOverride;

        string host;
        int port = 22;
        var bangIdx = hostPort.IndexOf('!');
        if (bangIdx > 0 && int.TryParse(hostPort[(bangIdx + 1)..], out var parsed))
        {
            host = hostPort[..bangIdx];
            port = parsed;
        }
        else
        {
            host = hostPort;
        }

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username))
            return null;

        return new ConnectionSpec
        {
            Letter     = letter.TrimEnd(':').ToUpperInvariant(),
            Host       = host,
            Port       = port,
            Username   = username,
            RemotePath = remotePath,
            UncPath    = uncPath,
        };
    }
}
