using System.Globalization;

namespace Fonoo.Windows.Telephony;

public sealed record ContactRow(string Id, string Name, string Number, string Kind)
{
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => p[0])).ToUpperInvariant();
    public string Detail => $"{Number} · {Kind}";
    public bool Matches(string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
        new[] { Name, Number, Kind }.Any(field => CultureInfo.CurrentCulture.CompareInfo.IndexOf(field, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0));
    public static string? NormalizeNumber(string raw) => DialNumber.Normalize(raw);
}

public static class VCardDirectory
{
    // vCard 3/4: unfold continuation lines; only callable phone numbers are imported.
    public static ContactRow[] Parse(string source)
    {
        if (source.Length > 2_000_000) throw new InvalidDataException("Kontaktdatei zu groß.");
        var lines = source.Replace("\r\n", "\n").Replace("\n ", "").Replace("\n\t", "").Split('\n');
        var result = new List<ContactRow>();
        var active = false; var name = ""; var phones = new List<(string, string)>(); var id = "";
        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)) { active = true; name = ""; phones.Clear(); id = Guid.NewGuid().ToString(); continue; }
            if (!active) continue;
            if (line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (number, kind) in phones.Distinct()) result.Add(new(id + ":" + number, string.IsNullOrWhiteSpace(name) ? number : name, number, kind));
                active = false; continue;
            }
            var colon = line.IndexOf(':'); if (colon < 1) continue;
            var key = line[..colon]; var value = line[(colon + 1)..].Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
            var field = key.Split(';')[0]; if (field.Contains('.')) field = field[(field.LastIndexOf('.') + 1)..];
            if (field.Equals("FN", StringComparison.OrdinalIgnoreCase)) name = value.Trim();
            if (field.Equals("N", StringComparison.OrdinalIgnoreCase) && name.Length == 0) name = string.Join(" ", value.Split(';').Take(2).Reverse()).Trim();
            if (field.Equals("TEL", StringComparison.OrdinalIgnoreCase) && ContactRow.NormalizeNumber(value) is { } phone)
                phones.Add((phone, key.Contains("CELL", StringComparison.OrdinalIgnoreCase) ? "Mobil" : key.Contains("WORK", StringComparison.OrdinalIgnoreCase) ? "Arbeit" : "Telefon"));
            if (result.Count + phones.Count > 10000) throw new InvalidDataException("Zu viele Kontakte.");
        }
        return result.DistinctBy(c => (c.Name, c.Number)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
