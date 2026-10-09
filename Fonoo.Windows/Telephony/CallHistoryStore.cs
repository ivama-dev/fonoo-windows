using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fonoo.Windows.Telephony;

public sealed record CallHistoryEntry(string Id, string Number, bool Incoming, DateTimeOffset StartedAt, int DurationSeconds, string Outcome)
{
    public string Title => Number == "anonymous" ? "Unbekannte Rufnummer" : Number;
    public string Detail => $"{(Incoming ? "Eingehend" : "Ausgehend")} · {Outcome switch { "missed" => "Verpasst", "declined" => "Abgelehnt", "failed" => "Fehlgeschlagen", _ => "Gespräch" }} · {StartedAt.LocalDateTime:g}";
    public string Duration => DurationSeconds > 0 ? TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "";
}

public sealed class CallHistoryStore
{
    private readonly string path;
    private bool writable;
    public CallHistoryStore(string directory, string user, string tenant) => path = Path.Combine(directory,
        "history-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + "\n" + tenant))) + ".json");
    public CallHistoryEntry[] Load()
    {
        writable = false;
        var result = File.Exists(path) ? JsonSerializer.Deserialize<CallHistoryEntry[]>(File.ReadAllText(path)) ?? throw new InvalidDataException() : [];
        Validate(result); writable = true;
        return result.OrderByDescending(c => c.StartedAt).ToArray();
    }
    private static void Validate(CallHistoryEntry[] entries)
    {
        if (entries.Length > 500 || entries.Any(e => e is null || !Guid.TryParse(e.Id, out _) || string.IsNullOrEmpty(e.Number) || e.Number.Length > 128 || e.DurationSeconds < 0 ||
            e.Outcome is not ("completed" or "missed" or "declined" or "failed")) || entries.Select(e => e.Id).Distinct().Count() != entries.Length)
            throw new InvalidDataException("Invalid call history");
    }
    public void Save(IEnumerable<CallHistoryEntry> entries)
    {
        if (!writable) throw new InvalidOperationException();
        var values = entries.OrderByDescending(e => e.StartedAt).Take(500).ToArray(); Validate(values);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(values)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
