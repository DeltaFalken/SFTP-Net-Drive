using System.IO;
using System.Text.Json;
using SftpNetDrive.Models;

namespace SftpNetDrive.Services;

public class ProfileRepository
{
    private static readonly string DataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SftpNetDrive", "profiles.json");

    private List<ConnectionProfile> _profiles = [];

    public IReadOnlyList<ConnectionProfile> Profiles => _profiles.AsReadOnly();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ProfileRepository() => Load();

    public void Add(ConnectionProfile profile)
    {
        _profiles.Add(profile);
        Save();
    }

    public void Update(ConnectionProfile updated)
    {
        var idx = _profiles.FindIndex(p => p.Id == updated.Id);
        if (idx >= 0) _profiles[idx] = updated;
        Save();
    }

    public void Remove(Guid id)
    {
        _profiles.RemoveAll(p => p.Id == id);
        CredentialService.Delete(id);
        Save();
    }

    private void Load()
    {
        if (!File.Exists(DataPath)) return;
        try
        {
            var json = File.ReadAllText(DataPath);
            _profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(json) ?? [];
        }
        catch { _profiles = []; }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        File.WriteAllText(DataPath, JsonSerializer.Serialize(_profiles, JsonOpts));
    }
}
