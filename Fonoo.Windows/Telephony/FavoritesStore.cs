using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fonoo.Windows.Telephony;

public sealed record Favorite(string Id, string Name, string Number)
{
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => p[0])).ToUpperInvariant();
}

public sealed class FavoritesStore
{
    private readonly string path;
    private bool writable;
    public FavoritesStore(string directory, string accountId, string tenantId)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId + "\n" + tenantId)));
        path = Path.Combine(directory, $"favorites-{identity}.json");
    }
    public Favorite[] Load()
    {
        writable = false;
        var values = File.Exists(path) ? JsonSerializer.Deserialize<Favorite[]>(File.ReadAllText(path)) ?? throw new InvalidDataException() : [];
        if (values.Length > 1000 || values.Any(f => f is null || string.IsNullOrWhiteSpace(f.Id) || f.Id.Length > 600 || string.IsNullOrWhiteSpace(f.Name) || f.Name.Length > 160 || !DialNumber.IsValid(f.Number)) ||
            values.Select(f => f.Id).Distinct().Count() != values.Length) throw new InvalidDataException("Invalid favorites file");
        writable = true;
        return values;
    }
    public void Save(IEnumerable<Favorite> favorites)
    {
        if (!writable) throw new InvalidOperationException("Favorites could not be loaded");
        var values = favorites.ToArray();
        if (values.Length > 1000 || values.Any(f => f is null || string.IsNullOrWhiteSpace(f.Id) || f.Id.Length > 600 || string.IsNullOrWhiteSpace(f.Name) || f.Name.Length > 160 || !DialNumber.IsValid(f.Number)) ||
            values.Select(f => f.Id).Distinct().Count() != values.Length)
            throw new InvalidDataException();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(values));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
