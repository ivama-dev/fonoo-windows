using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fonoo.Windows.Telephony;

public sealed record CloudFavoriteContact(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("number")] string Number);
public sealed record CloudFavoriteGroup(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("contacts")] CloudFavoriteContact[] Contacts);
public sealed record CloudFavoriteSnapshot(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("library")] CloudFavoriteLibrary Library)
{
    public CloudFavoriteSnapshot Validate(string owner)
    {
        if (SchemaVersion != 1 || UserId != owner || Revision < 0 || Library is null)
            throw new InvalidDataException("Ungültiger Favoriten-Kontostand.");
        Library.Validate(); return this;
    }
}
public sealed record CloudFavoriteLibrary([property: JsonPropertyName("groups")] CloudFavoriteGroup[] Groups)
{
    // Transport JSON only; never embed this in HTML. Match the Apple/API UTF-8 budget.
    public static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public const string GeneralId = "ungrouped";
    public static CloudFavoriteLibrary Empty => new([new(GeneralId, "Allgemein", [])]);
    [JsonIgnore] public CloudFavoriteContact[] Contacts => Groups.SelectMany(g => g.Contacts).ToArray();
    public CloudFavoriteLibrary Validate()
    {
        static bool Text(string? s, int max, bool empty = false) => s is not null &&
            (empty || !string.IsNullOrWhiteSpace(s)) && s.EnumerateRunes().Count() <= max && !s.Any(char.IsControl);
        if (Groups is null || Groups.Length is < 1 or > 50 || Groups.Any(g => g is null || g.Contacts is null) ||
            Groups.Count(g => g.Id == GeneralId) != 1 || Groups.Select(g => g.Id).Distinct().Count() != Groups.Length ||
            Groups.Any(g => (g.Id != GeneralId && (!Guid.TryParse(g.Id, out var id) || id.ToString() != g.Id)) ||
                !Text(g.Name, 80) || g.Name != g.Name.Trim().Normalize(NormalizationForm.FormC)) ||
            Groups.Select(g => g.Name.ToLowerInvariant()).Distinct().Count() != Groups.Length || Contacts.Length > 500 ||
            Contacts.Any(c => c is null || !Text(c.Id, 256) || !Text(c.Name, 256) || !Text(c.Role, 128, true) || !Text(c.Number, 128)) ||
            Contacts.Select(c => c.Id).Distinct().Count() != Contacts.Length || JsonSerializer.SerializeToUtf8Bytes(this, WireJson).Length > 60_000)
            throw new InvalidDataException("Ungültige Favoriten oder Gruppen.");
        return this;
    }
    public bool Same(CloudFavoriteLibrary? other) => other is not null && Groups.Length == other.Groups.Length &&
        Groups.Zip(other.Groups).All(pair => pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
            pair.First.Contacts.SequenceEqual(pair.Second.Contacts));

    public CloudFavoriteLibrary Merge(CloudFavoriteLibrary remote, CloudFavoriteLibrary? ancestor = null,
        IEnumerable<string>? deletedContacts = null, IEnumerable<string>? deletedGroups = null)
    {
        Validate(); remote.Validate(); var baseline = (ancestor ?? Empty).Validate();
        var b = baseline.Groups.ToDictionary(g => g.Id); var l = Groups.ToDictionary(g => g.Id); var r = remote.Groups.ToDictionary(g => g.Id);
        var removedGroups = b.Keys.Where(id => id != GeneralId && (!l.ContainsKey(id) || !r.ContainsKey(id)))
            .Concat(deletedGroups ?? []).Where(id => id != GeneralId).ToHashSet();
        var groupIds = l.Keys.Concat(r.Keys).Where(id => !removedGroups.Contains(id)).ToHashSet();
        var order = MergeOrder(baseline.Groups.Select(g => g.Id), Groups.Select(g => g.Id), remote.Groups.Select(g => g.Id), groupIds);
        var groups = new List<CloudFavoriteGroup>(); var aliases = new Dictionary<string, string>(); var named = new Dictionary<string, string>();
        foreach (var id in order)
        {
            var name = l.TryGetValue(id, out var local) ? (!b.TryGetValue(id, out var old) || old.Name != local.Name
                ? local.Name : r.GetValueOrDefault(id)?.Name ?? local.Name) : r[id].Name;
            if (named.TryGetValue(name.ToLowerInvariant(), out var same)) { aliases[id] = same; continue; }
            named[name.ToLowerInvariant()] = id; aliases[id] = id; groups.Add(new(id, name, []));
        }
        static Dictionary<string, (CloudFavoriteContact Contact, string Group)> Entries(CloudFavoriteLibrary x) =>
            x.Groups.SelectMany(g => g.Contacts.Select(c => (c.Id, Value: (c, g.Id)))).ToDictionary(x => x.Id, x => x.Value);
        var bc = Entries(baseline); var lc = Entries(this); var rc = Entries(remote);
        var removed = bc.Keys.Where(id => !lc.ContainsKey(id) || !rc.ContainsKey(id)).Concat(deletedContacts ?? []).ToHashSet();
        var ids = lc.Keys.Concat(rc.Keys).Where(id => !removed.Contains(id)).ToHashSet();
        var merged = new Dictionary<string, CloudFavoriteContact>(); var membership = new Dictionary<string, string>();
        foreach (var id in ids)
        {
            var hasLocal = lc.TryGetValue(id, out var local); var hasOther = rc.TryGetValue(id, out var other); var hasBase = bc.TryGetValue(id, out var old);
            var chosen = hasLocal ? local.Contact : other.Contact;
            static string Field(string local, string? other, string? old) => local != old ? local : other ?? local;
            merged[id] = new(id, Field(chosen.Name, hasOther ? other.Contact.Name : null, hasBase ? old.Contact.Name : null),
                Field(chosen.Role, hasOther ? other.Contact.Role : null, hasBase ? old.Contact.Role : null),
                Field(chosen.Number, hasOther ? other.Contact.Number : null, hasBase ? old.Contact.Number : null));
            var parent = hasLocal ? (!hasBase || old.Group != local.Group ? local.Group : hasOther ? other.Group : local.Group) : other.Group;
            membership[id] = aliases.GetValueOrDefault(parent, GeneralId);
        }
        return new CloudFavoriteLibrary(groups.Select(g => g with { Contacts = MergeOrder(baseline.Contacts.Select(c => c.Id),
            Contacts.Select(c => c.Id), remote.Contacts.Select(c => c.Id), ids.Where(id => membership[id] == g.Id).ToHashSet())
            .Select(id => merged[id]).ToArray() }).ToArray()).Validate();
    }
    private static string[] MergeOrder(IEnumerable<string> baseline, IEnumerable<string> local, IEnumerable<string> remote, HashSet<string> surviving)
    {
        var b = baseline.ToArray(); var l = local.ToArray(); var r = remote.ToArray();
        var common = b.Intersect(l).Where(surviving.Contains).ToHashSet();
        var reordered = !l.Where(common.Contains).SequenceEqual(b.Where(common.Contains));
        var primary = reordered ? l : r; var secondary = reordered ? r : l;
        var result = primary.Where(surviving.Contains).ToList();
        for (var offset = 0; offset < secondary.Length; offset++)
        {
            var id = secondary[offset]; if (!surviving.Contains(id) || result.Contains(id)) continue;
            var previous = secondary.Take(offset).LastOrDefault(result.Contains);
            var next = secondary.Skip(offset + 1).FirstOrDefault(result.Contains);
            if (previous is not null) result.Insert(result.IndexOf(previous) + 1, id);
            else if (next is not null) result.Insert(result.IndexOf(next), id);
            else result.Add(id);
        }
        return result.ToArray();
    }
}

public sealed record CloudFavoriteState(int Version, string Owner, CloudFavoriteLibrary Library, long? Revision = null,
    CloudFavoriteLibrary? Base = null, CloudFavoriteLibrary? Attempt = null, bool Dirty = false,
    string[]? DeletedContactIds = null, string[]? DeletedGroupIds = null, string[]? ImportedSources = null)
{
    public CloudFavoriteState Observe(CloudFavoriteSnapshot snapshot)
    {
        snapshot.Validate(Owner);
        if (Revision is { } revision && snapshot.Revision < revision) throw new InvalidDataException("Veraltete Favoritenantwort.");
        var library = Library; var dirty = Dirty;
        if (!dirty || library.Same(snapshot.Library)) { library = snapshot.Library; dirty = false; }
        else if (!snapshot.Library.Same(Base) && !snapshot.Library.Same(Attempt) && !(Revision is null && snapshot.Library.Same(CloudFavoriteLibrary.Empty)))
        {
            library = Library.Merge(snapshot.Library, Base, DeletedContactIds, DeletedGroupIds); dirty = !library.Same(snapshot.Library);
        }
        return this with { Library = library, Dirty = dirty, Revision = snapshot.Revision, Base = snapshot.Library, Attempt = null,
            DeletedContactIds = dirty ? DeletedContactIds : null, DeletedGroupIds = dirty ? DeletedGroupIds : null };
    }
    public CloudFavoriteState Edit(CloudFavoriteLibrary next)
    {
        next.Validate();
        var ids = next.Contacts.Select(c => c.Id).ToHashSet(); var groups = next.Groups.Select(g => g.Id).ToHashSet();
        return this with { Library = next, Dirty = true,
            DeletedContactIds = (DeletedContactIds ?? []).Concat(Library.Contacts.Select(c => c.Id).Where(id => !ids.Contains(id))).Where(id => !ids.Contains(id)).Distinct().ToArray(),
            DeletedGroupIds = (DeletedGroupIds ?? []).Concat(Library.Groups.Select(g => g.Id).Where(id => !groups.Contains(id))).Where(id => !groups.Contains(id)).Distinct().ToArray() };
    }
    public CloudFavoriteState Acknowledge(CloudFavoriteSnapshot snapshot, CloudFavoriteLibrary sent)
    {
        snapshot.Validate(Owner); if (!sent.Same(snapshot.Library)) throw new InvalidDataException("Ungültige Bestätigung.");
        var dirty = !Library.Same(snapshot.Library);
        return this with { Revision = snapshot.Revision, Base = snapshot.Library, Attempt = null, Dirty = dirty,
            DeletedContactIds = dirty ? DeletedContactIds : null, DeletedGroupIds = dirty ? DeletedGroupIds : null };
    }
}

public sealed class CloudFavoriteCache
{
    private readonly string owner, path;
    private bool loaded;
    public CloudFavoriteState State { get; private set; }
    public CloudFavoriteCache(string directory, string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new InvalidDataException("Konto fehlt.");
        this.owner = owner;
        path = Path.Combine(directory, "personal-favorites-v1", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner))) + ".json");
        State = new(1, owner, CloudFavoriteLibrary.Empty);
    }
    public CloudFavoriteState Load()
    {
        loaded = false;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 1_000_000) throw new InvalidDataException("Favoritendatei zu groß.");
            State = JsonSerializer.Deserialize<CloudFavoriteState>(File.ReadAllText(path)) ?? throw new InvalidDataException();
        }
        Validate(State); loaded = true; return State;
    }
    private void Validate(CloudFavoriteState state)
    {
        if (state.Version != 1 || state.Owner != owner || state.Revision < 0 || (state.Revision is null) != (state.Base is null))
            throw new InvalidDataException("Ungültige Kontozuordnung der Favoriten.");
        state.Library.Validate(); state.Base?.Validate(); state.Attempt?.Validate();
    }
    public void Save(CloudFavoriteState state)
    {
        if (!loaded) throw new InvalidOperationException("Favoriten konnten nicht geladen werden.");
        Validate(state); var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (bytes.Length > 1_000_000) throw new InvalidDataException("Favoritendatei zu groß.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); State = state; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class FavoriteSyncConflict : Exception { }
public sealed class CloudFavoriteSync(CloudFavoriteCache cache,
    Func<CancellationToken, Task<CloudFavoriteSnapshot>> read,
    Func<long, CloudFavoriteLibrary, CancellationToken, Task<CloudFavoriteSnapshot>> write)
{
    private bool working;
    public event Action<string>? Updated;
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (working) return; working = true;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var remote = await read(ct); ct.ThrowIfCancellationRequested(); cache.Save(cache.State.Observe(remote));
                if (!cache.State.Dirty) { Updated?.Invoke("Mit deinem fonoo-Konto synchronisiert"); return; }
                var sent = cache.State.Library; cache.Save(cache.State with { Attempt = sent });
                try
                {
                    var saved = await write(cache.State.Revision!.Value, sent, ct); ct.ThrowIfCancellationRequested();
                    cache.Save(cache.State.Acknowledge(saved, sent));
                    Updated?.Invoke(cache.State.Dirty ? "Lokal gespeichert · automatischer Abgleich ausstehend" : "Mit deinem fonoo-Konto synchronisiert");
                    if (!cache.State.Dirty) return;
                }
                catch (FavoriteSyncConflict) { }
            }
            Updated?.Invoke("Lokal gespeichert · automatischer Abgleich ausstehend");
        }
        finally { working = false; }
    }
}
