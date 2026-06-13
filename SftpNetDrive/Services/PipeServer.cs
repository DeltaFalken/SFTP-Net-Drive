using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SftpNetDrive.Models;

namespace SftpNetDrive.Services;

public sealed class PipeServer : IDisposable
{
    private readonly MountService _mounts;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SftpNetDrive", "SVC.log");

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath,
                $"{DateTime.Now:HH:mm:ss.fff} [PID {Environment.ProcessId}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public PipeServer(MountService mounts, string sid)
    {
        _mounts   = mounts;
        _pipeName = $"SftpNetDrive_NP_{sid}";
        Log($"PipeServer created, pipe=\\\\.\\pipe\\{_pipeName}");
    }

    public void Start()
    {
        _cts        = new CancellationTokenSource();
        _listenTask = ListenLoop(_cts.Token);
        Log("PipeServer.Start() called — ListenLoop scheduled");
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    // Build a PipeSecurity that grants full control to all authenticated users.
    // This ensures Explorer (medium integrity) can connect to a pipe created
    // by the EXE even if the token integrity levels differ.
    private static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        return security;
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        Log("ListenLoop started");
        int errorCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize:  0,
                    outBufferSize: 0,
                    BuildPipeSecurity());

                if (errorCount > 0) Log($"Pipe created after {errorCount} errors, waiting for client");
                else if (errorCount == 0) Log("Pipe created, waiting for client");
                errorCount = 0;

                await pipe.WaitForConnectionAsync(ct);
                Log("Client connected");
                _ = Task.Run(() => HandleClient(pipe, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                errorCount++;
                if (errorCount <= 3 || errorCount % 60 == 0)
                    Log($"ListenLoop error #{errorCount}: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        Log("ListenLoop stopped");
    }

    private async Task HandleClient(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                var buf  = new byte[8192];
                int len  = await pipe.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                var line = Encoding.Unicode.GetString(buf, 0, len).Trim();
                var parts0 = line.Split('\t');
                Log($"Request: {parts0[0]} (parts={parts0.Length} bytes={len})");

                var response = await ProcessLine(line, ct);
                Log($"Response: {response.Split('\t')[0]}");

                var resp = Encoding.Unicode.GetBytes(response);
                await pipe.WriteAsync(resp.AsMemory(), ct);
                pipe.Flush();
            }
            catch (Exception ex) { Log($"HandleClient error: {ex.Message}"); }
        }
    }

    private async Task<string> ProcessLine(string line, CancellationToken ct)
    {
        var parts = line.Split('\t');
        if (parts.Length < 2) return "ERR\tInvalid command";

        var cmd = parts[0].ToUpperInvariant();

        switch (cmd)
        {
            case "MOUNT":
            {
                // MOUNT  letter  uncPath  password  [usernameOverride]
                if (parts.Length < 4) return "ERR\tMissing parameters";
                var letter          = parts[1].TrimEnd(':').ToUpperInvariant();
                var uncPath         = parts[2];
                var password        = parts[3];
                var usernameOverride = parts.Length > 4 ? parts[4] : null;

                // Already mounted?
                if (_mounts.IsMounted(letter))
                    return "ALREADY";

                var spec = ConnectionSpec.Parse(letter, uncPath, usernameOverride);
                if (spec is null) return "ERR\tCannot parse UNC path";

                // Store password in Credential Manager for persistence
                if (!string.IsNullOrEmpty(password))
                    CredentialService.Save(letter, password);

                // Persist drive mapping in registry
                MountRegistry.Set(letter, uncPath);

                var (ok, error) = await _mounts.MountAsync(spec, ct);
                if (!ok)
                {
                    MountRegistry.Remove(letter);
                    return $"ERR\t{error ?? "Mount failed"}";
                }
                return "OK";
            }

            case "UNMOUNT":
            {
                // UNMOUNT  letter
                var letter = parts[1].TrimEnd(':').ToUpperInvariant();
                _mounts.Unmount(letter);
                MountRegistry.Remove(letter);
                CredentialService.Delete(letter);
                return "OK";
            }

            default:
                return "ERR\tUnknown command";
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
