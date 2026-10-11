using System.Security.Cryptography;
using System.Text;

namespace Fonoo.Windows.Telephony;

public static class FavoriteCloudBridge
{
    public static CloudFavoriteLibrary ToCloud(FavoriteLibrary library, string owner, string source)
    {
        FavoriteGroups.Validate(library);
        var aliases = new Dictionary<string, string>(); var groups = new List<CloudFavoriteGroup>();
        if (!library.Groups.Any(g => g.Id == CloudFavoriteLibrary.GeneralId))
            groups.Add(new(CloudFavoriteLibrary.GeneralId, "Allgemein", []));
        foreach (var group in library.Groups)
        {
            var name = group.Name.Trim().Normalize(NormalizationForm.FormC);
            var id = group.Id == CloudFavoriteLibrary.GeneralId ? group.Id : Guid.TryParse(group.Id, out var parsed)
                ? parsed.ToString() : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(owner + "\n" + source + "\n" + group.Id)).AsSpan(0, 16)).ToString();
            var same = groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (same is not null) { aliases[group.Id] = same.Id; continue; }
            aliases[group.Id] = id; groups.Add(new(id, name, []));
        }
        return new CloudFavoriteLibrary(groups.Select(g => g with { Contacts = library.Favorites
            .Where(f => (f.GroupId is null ? CloudFavoriteLibrary.GeneralId : aliases.GetValueOrDefault(f.GroupId, CloudFavoriteLibrary.GeneralId)) == g.Id)
            .Select(f => new CloudFavoriteContact(f.Id, f.Name, f.Role ?? "", f.Number)).ToArray() }).ToArray()).Validate();
    }
    public static FavoriteLibrary ToLocal(CloudFavoriteLibrary library)
    {
        library.Validate();
        return FavoriteGroups.Validate(new(library.Groups.SelectMany(g => g.Contacts.Select(c =>
            new Favorite(c.Id, c.Name, c.Number, g.Id == CloudFavoriteLibrary.GeneralId ? null : g.Id, c.Role))).ToArray(),
            library.Groups.Select(g => new FavoriteGroup(g.Id, g.Name)).ToArray()));
    }
    public static void ImportOnce(CloudFavoriteCache cache, FavoriteLibrary library, string source)
    {
        if (cache.State.ImportedSources?.Contains(source) == true) return;
        var converted = ToCloud(library, cache.State.Owner, source);
        var combined = cache.State.Library.Merge(converted);
        var state = combined.Same(cache.State.Library) ? cache.State : cache.State.Edit(combined);
        cache.Save(state with { ImportedSources = [.. state.ImportedSources ?? [], source] });
    }
}
