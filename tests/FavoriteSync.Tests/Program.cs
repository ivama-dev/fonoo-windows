using System.Text.Json;
using Fonoo.Windows.Telephony;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var vectors = JsonSerializer.Deserialize<MergeCase[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "merge-cases.json")),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
foreach (var vector in vectors)
    Check(vector.Local.Merge(vector.Remote, vector.Base, vector.DeletedContactIds).Same(vector.Expected), vector.Name);
Console.WriteLine("PASS: identical Swift/Windows merge contract fixtures");
var largeUnicodeLibrary = new CloudFavoriteLibrary([
    new("ungrouped", "Allgemein", []),
    new("00000000-0000-0000-0000-000000000001", "Arbeit", Enumerable.Range(0, 130).Select(i =>
        new CloudFavoriteContact("favorite-" + i, new string('ü', 193), "work", "+431234567")).ToArray()),
    new("00000000-0000-0000-0000-000000000002", "Privat", [])]);
largeUnicodeLibrary.Validate();
Check(JsonSerializer.SerializeToUtf8Bytes(largeUnicodeLibrary, CloudFavoriteLibrary.WireJson).Length < 60_000 &&
    JsonSerializer.SerializeToUtf8Bytes(largeUnicodeLibrary).Length > 60_000, "UTF-8 fixture does not exercise the shared limit");
Console.WriteLine("PASS: compact UTF-8 limit agrees with Apple and the API");
var root = Path.Combine(Path.GetTempPath(), "fonoo-favorite-sync-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    var cache = new CloudFavoriteCache(root, "user-a"); cache.Load();
    var original = new FavoriteLibrary([new("a", "A", "101", "legacy-group", "work")], [new("legacy-group", "Arbeit")]);
    FavoriteCloudBridge.ImportOnce(cache, original, "company-a");
    var first = cache.State.Library;
    Check(first.Groups.Length == 2 && first.Contacts[0].Role == "work", "Migration lost group or role");
    FavoriteCloudBridge.ImportOnce(cache, FavoriteLibrary.Empty, "company-a");
    Check(cache.State.Library.Same(first), "Repeated import changed data");
    var wire = JsonSerializer.Serialize(cache.State.Library);
    Check(!wire.Contains("GroupId") && wire.Contains("\"contacts\""), "Incorrect shared wire format");
    var local = FavoriteCloudBridge.ToLocal(first);
    Check(FavoriteCloudBridge.ToCloud(local, "user-a", "company-b").Same(first), "Cross-company bridge changed canonical identities or role");
    Console.WriteLine("PASS: personal cache, canonical migration, role and ordered wire format");

    var server = new CloudFavoriteSnapshot(1, "user-a", 0, CloudFavoriteLibrary.Empty);
    var race = 0; var writes = 0; var offline = false;
    Task<CloudFavoriteSnapshot> Read(CancellationToken ct) => offline ? Task.FromException<CloudFavoriteSnapshot>(new IOException()) : Task.FromResult(server);
    Task<CloudFavoriteSnapshot> Write(long revision, CloudFavoriteLibrary library, CancellationToken ct)
    {
        if (race > 0)
        {
            race--;
            var extra = new CloudFavoriteGroup(Guid.NewGuid().ToString(), "Remote " + server.Revision, []);
            server = server with { Revision = server.Revision + 1, Library = new([..server.Library.Groups, extra]) };
            return Task.FromException<CloudFavoriteSnapshot>(new FavoriteSyncConflict());
        }
        Check(server.Revision == revision, "CAS mismatch"); writes++;
        return Task.FromResult(server = new(1, "user-a", revision + 1, library));
    }
    var worker = new CloudFavoriteSync(cache, Read, Write);
    await worker.RefreshAsync(CancellationToken.None);
    Check(server.Library.Same(first) && !cache.State.Dirty, "Automatic first sync failed");
    var second = new CloudFavoriteCache(Path.Combine(root, "second"), "user-a"); second.Load();
    var other = new CloudFavoriteSync(second, Read, Write); await other.RefreshAsync(CancellationToken.None);
    Check(second.State.Library.Same(first), "Second device did not automatically load groups");

    var group = first.Groups.Single(g => g.Id != CloudFavoriteLibrary.GeneralId);
    var edited = first with { Groups = first.Groups.Select(g => g.Id == group.Id ? g with { Name = "Büro" } : g).ToArray() };
    cache.Save(cache.State.Edit(edited)); offline = true;
    try { await worker.RefreshAsync(CancellationToken.None); throw new Exception("Offline request succeeded"); } catch (IOException) { }
    var restarted = new CloudFavoriteCache(root, "user-a"); restarted.Load();
    Check(restarted.State.Library.Same(edited) && restarted.State.Dirty, "Offline edits lost on restart");
    offline = false; race = 2; await worker.RefreshAsync(CancellationToken.None); await other.RefreshAsync(CancellationToken.None);
    Check(!cache.State.Dirty && second.State.Library.Same(cache.State.Library) && second.State.Library.Groups.Any(g => g.Name == "Büro"), "Race did not converge");
    Console.WriteLine("PASS: offline restart, automatic cross-device sync and bounded CAS retries preserve independent changes");

    var pending = cache.State.Edit(cache.State.Library with { Groups = cache.State.Library.Groups.Select(g => g.Id == group.Id ? g with { Name = "Office" } : g).ToArray() });
    cache.Save(pending); race = 4; await worker.RefreshAsync(CancellationToken.None);
    Check(cache.State.Dirty && race == 1, "Race retry unbounded or pending edits discarded");
    race = 0; await worker.RefreshAsync(CancellationToken.None); Check(!cache.State.Dirty, "Polling could not recover pending race");
    var before = cache.State;
    try { cache.Save(cache.State.Observe(server with { UserId = "user-b" })); throw new Exception("Wrong owner accepted"); } catch (InvalidDataException) { }
    Check(cache.State == before && new CloudFavoriteCache(root, "user-b").Load().Library.Same(CloudFavoriteLibrary.Empty), "Account libraries mixed");
    Console.WriteLine("PASS: retry bounds and account isolation");

    var late = new TaskCompletionSource<CloudFavoriteSnapshot>(); using var lifetime = new CancellationTokenSource();
    var lateWorker = new CloudFavoriteSync(cache, _ => late.Task, Write);
    var job = lateWorker.RefreshAsync(lifetime.Token); lifetime.Cancel(); late.SetResult(server);
    try { await job; throw new Exception("Old request not cancelled"); } catch (OperationCanceledException) { }
    Check(cache.State == before, "Late signed-out reply changed library");
    Console.WriteLine("PASS: cancelled old-login callbacks do not change cache");
}
finally { Directory.Delete(root, true); }

sealed record MergeCase(string Name, CloudFavoriteLibrary Base, CloudFavoriteLibrary Local, CloudFavoriteLibrary Remote,
    CloudFavoriteLibrary Expected, [property: System.Text.Json.Serialization.JsonPropertyName("deleted_contact_ids")] string[]? DeletedContactIds);
