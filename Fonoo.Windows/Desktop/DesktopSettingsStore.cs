using System.Text.Json;

namespace Fonoo.Windows.Desktop;

public sealed record DesktopSettings(int Version = 1, bool MinimizeToTray = true);

public sealed class DesktopSettingsStore(string directory)
{
    private readonly string path = Path.Combine(directory, "desktop-settings.json");
    private bool writable;
    public DesktopSettings Load()
    {
        writable = false;
        if (File.Exists(path) && new FileInfo(path).Length > 4096) throw new InvalidDataException("Desktop settings too large");
        var settings = File.Exists(path) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException() : new();
        if (settings.Version != 1) throw new InvalidDataException("Unsupported desktop settings");
        writable = true; return settings;
    }
    public void Save(DesktopSettings settings)
    {
        if (!writable || settings.Version != 1) throw new InvalidOperationException("Desktop settings are read-only");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
