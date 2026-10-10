using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fonoo.Windows.Telephony;

public sealed record Favorite(string Id, string Name, string Number, string? GroupId = null)
{
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => p[0])).ToUpperInvariant();
}

public sealed record FavoriteGroup(string Id, string Name);
public sealed record FavoriteLibrary(Favorite[] Favorites, FavoriteGroup[] Groups)
{
    public static FavoriteLibrary Empty => new([], []);
}

// Array order is the user's order, both for groups and for favorites within a group.
public static class FavoriteGroups
{
    public static FavoriteLibrary Validate(FavoriteLibrary library)
    {
        var values = library.Favorites; var groups = library.Groups;
        if (values is null || groups is null || values.Length > 1000 || groups.Length > 50 ||
            groups.Any(g => g is null || string.IsNullOrWhiteSpace(g.Id) || g.Id.Length > 100 || !ValidName(g.Name)) ||
            groups.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != groups.Length ||
            groups.Select(g => g.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Length ||
            values.Any(f => f is null || string.IsNullOrWhiteSpace(f.Id) || f.Id.Length > 600 || string.IsNullOrWhiteSpace(f.Name) ||
                f.Name.Length > 160 || !DialNumber.IsValid(f.Number) ||
                (f.GroupId is not null && !groups.Any(g => g.Id == f.GroupId))) ||
            values.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidDataException("Invalid favorites file");
        return library;
    }

    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 60 && !name.Any(char.IsControl);

    public static FavoriteLibrary Upsert(FavoriteLibrary library, string id, string name)
    {
        Validate(library);
        if (!ValidName(name)) throw new InvalidDataException("Invalid group name");
        var group = new FavoriteGroup(id, name.Trim());
        return Validate(library with { Groups = library.Groups.Any(g => g.Id == id)
            ? library.Groups.Select(g => g.Id == id ? group : g).ToArray() : [.. library.Groups, group] });
    }

    public static FavoriteLibrary Delete(FavoriteLibrary library, string id)
    {
        Validate(library);
        if (!library.Groups.Any(g => g.Id == id)) throw new InvalidDataException("Unknown group");
        // Removing a group never removes its contacts.
        return library with { Groups = library.Groups.Where(g => g.Id != id).ToArray(),
            Favorites = library.Favorites.Select(f => f.GroupId == id ? f with { GroupId = null } : f).ToArray() };
    }

    public static FavoriteLibrary MoveGroup(FavoriteLibrary library, string id, string targetId, bool after = false)
    {
        Validate(library);
        var moving = library.Groups.SingleOrDefault(g => g.Id == id) ?? throw new InvalidDataException("Unknown group");
        if (!library.Groups.Any(g => g.Id == targetId)) throw new InvalidDataException("Unknown target group");
        if (id == targetId) return library;
        var groups = library.Groups.Where(g => g.Id != id).ToList();
        groups.Insert(groups.FindIndex(g => g.Id == targetId) + (after ? 1 : 0), moving);
        return library with { Groups = groups.ToArray() };
    }

    public static FavoriteLibrary MoveFavorite(FavoriteLibrary library, string id, string? groupId, string? targetId = null, bool after = false)
    {
        Validate(library);
        var moving = library.Favorites.SingleOrDefault(f => f.Id == id) ?? throw new InvalidDataException("Unknown favorite");
        if (groupId is not null && !library.Groups.Any(g => g.Id == groupId)) throw new InvalidDataException("Unknown group");
        if (targetId == id) return library;
        var values = library.Favorites.Where(f => f.Id != id).ToList();
        var index = values.FindIndex(f => f.Id == targetId && f.GroupId == groupId);
        if (targetId is not null && index < 0) throw new InvalidDataException("Unknown target favorite");
        if (targetId is null) { index = values.FindLastIndex(f => f.GroupId == groupId); index = index < 0 ? values.Count : index + 1; }
        else if (after) index++;
        values.Insert(index, moving with { GroupId = groupId });
        return library with { Favorites = values.ToArray() };
    }
}

public sealed class FavoritesStore
{
    private readonly string path;
    private bool writable;
    private FavoriteGroup[] groups = [];
    public FavoritesStore(string directory, string accountId, string tenantId)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId + "\n" + tenantId)));
        path = Path.Combine(directory, $"favorites-{identity}.json");
    }
    public Favorite[] Load() => LoadLibrary().Favorites;
    public FavoriteLibrary LoadLibrary()
    {
        writable = false;
        var library = FavoriteLibrary.Empty;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Favorites file too large");
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                library = new(JsonSerializer.Deserialize<Favorite[]>(json) ?? throw new InvalidDataException(), []);
            else
            {
                var saved = JsonSerializer.Deserialize<SavedLibrary>(json) ?? throw new InvalidDataException();
                if (saved.Version != 1 || saved.Favorites is null || saved.Groups is null) throw new InvalidDataException("Unsupported favorites file");
                library = new(saved.Favorites, saved.Groups);
            }
        }
        FavoriteGroups.Validate(library);
        groups = library.Groups.ToArray();
        writable = true;
        return library;
    }
    public void Save(IEnumerable<Favorite> favorites) => SaveLibrary(new(favorites.ToArray(), groups));
    public void SaveLibrary(FavoriteLibrary library)
    {
        if (!writable) throw new InvalidOperationException("Favorites could not be loaded");
        FavoriteGroups.Validate(library);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedLibrary(1, library.Favorites, library.Groups)));
            File.Move(temporary, path, true);
            groups = library.Groups.ToArray();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed record SavedLibrary(int Version, Favorite[] Favorites, FavoriteGroup[] Groups);
}
