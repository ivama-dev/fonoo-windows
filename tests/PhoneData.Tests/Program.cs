using Fonoo.Windows.Telephony;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
foreach (var number in new[] { "101", "+43744222322", "*21101#" }) Check(DialNumber.IsValid(number), "Valid number rejected");
foreach (var number in new string?[] { null, "", "+", "#", "sip:101@other.test", "101;transport=udp", "1+01", "++101", "101\r\n", new('1', 65) })
    Check(!DialNumber.IsValid(number), "Invalid dial target accepted");
foreach (var (input, expected) in new[] {
    ("+43 720 0101010", "+437200101010"),
    ("+43\u00a0720\u202f0101010", "+437200101010"),
    ("+43 (720) 010-1010", "+437200101010"),
    ("tel:+43.720.0101010", "+437200101010"),
    (" 101 ", "101"), ("*21 101#", "*21101#") })
    Check(DialNumber.Normalize(input) == expected, "Formatted dial target not normalized correctly");
foreach (var input in new string?[] { null, "", " ", "+", "sip:+437200101010@other.test", "tel:+437200101010;ext=5", "101;transport=udp", "1 + 01", "++101", "101\r\n", "101\t", "+43/720/0101010", "１２３", new('1', 65), new(' ', 257) })
    Check(DialNumber.Normalize(input) is null, "Unsafe formatted dial target accepted");
var directory = Path.Combine(Path.GetTempPath(), "fonoo-favorites-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var store = new FavoritesStore(directory, "account-A", "tenant-A");
    Check(store.Load().Length == 0, "New store not empty");
    var one = new Favorite("one", "Empfang", "101");
    store.Save(new[] { one });
    Check(new FavoritesStore(directory, "account-A", "tenant-A").Load().Single() == one, "Favorite did not survive restart");
    Check(new FavoritesStore(directory, "account-B", "tenant-A").Load().Length == 0, "Favorites leaked to another account");
    Check(new FavoritesStore(directory, "account-A", "tenant-B").Load().Length == 0, "Favorites leaked to another tenant");
    var path = Directory.GetFiles(directory, "*.json").Single();
    // The old array format is read without modifying the user's original file.
    var legacy = "[{\"Id\":\"one\",\"Name\":\"Empfang\",\"Number\":\"101\"},{\"Id\":\"team:tenant-A:two\",\"Name\":\"Alex\",\"Number\":\"102\"}]";
    File.WriteAllText(path, legacy);
    var migratedStore = new FavoritesStore(directory, "account-A", "tenant-A");
    var library = migratedStore.LoadLibrary();
    Check(library.Favorites.Length == 2 && library.Groups.Length == 0 && File.ReadAllText(path) == legacy, "Legacy favorites lost or eagerly rewritten");
    library = FavoriteGroups.Upsert(library, "team", "  Mein Team  ");
    library = FavoriteGroups.Upsert(library, "clients", "Kunden");
    library = FavoriteGroups.Upsert(library, "private", "Privat");
    library = FavoriteGroups.MoveFavorite(library, "one", "team");
    library = FavoriteGroups.MoveFavorite(library, "team:tenant-A:two", "team", "one");
    Check(library.Favorites.Select(f => f.Id).SequenceEqual(["team:tenant-A:two", "one"]), "Favorite order wrong");
    Check(library.Favorites[0].Id == "team:tenant-A:two" && library.Favorites[0].Number == "102", "Team identity changed by grouping");
    library = FavoriteGroups.MoveGroup(library, "private", "team");
    library = FavoriteGroups.MoveGroup(library, "team", "clients", after: true);
    Check(library.Groups.Select(g => g.Id).SequenceEqual(["private", "clients", "team"]), "Group upward/downward movement wrong");
    library = FavoriteGroups.Upsert(library, "team", "Kollegen");
    Check(library.Groups.Last().Name == "Kollegen" && library.Favorites.All(f => f.GroupId == "team"), "Rename lost group order or members");
    library = FavoriteGroups.MoveFavorite(library, "one", "clients");
    library = FavoriteGroups.MoveFavorite(library, "one", "team", "team:tenant-A:two", after: true);
    Check(library.Favorites.Where(f => f.GroupId == "team").Select(f => f.Id).SequenceEqual(["team:tenant-A:two", "one"]), "Cross-group move or downward placement wrong");
    migratedStore.SaveLibrary(library);
    var restarted = new FavoritesStore(directory, "account-A", "tenant-A").LoadLibrary();
    Check(restarted.Favorites.SequenceEqual(library.Favorites) && restarted.Groups.SequenceEqual(library.Groups), "Group membership or order lost after restart");
    // Old call sites that save favorites only must retain the groups as well.
    migratedStore.Save(library.Favorites);
    Check(new FavoritesStore(directory, "account-A", "tenant-A").LoadLibrary().Groups.SequenceEqual(library.Groups), "Favorite-only save removed groups");
    Check(new FavoritesStore(directory, "account-B", "tenant-A").LoadLibrary().Groups.Length == 0 &&
        new FavoritesStore(directory, "account-A", "tenant-B").LoadLibrary().Groups.Length == 0, "Groups leaked across account or company");
    var removed = FavoriteGroups.Delete(library, "team");
    Check(removed.Favorites.Length == 2 && removed.Favorites.All(f => f.GroupId is null) && removed.Groups.Length == 2, "Deleting group deleted favorites");
    var ungrouped = FavoriteGroups.MoveFavorite(library, "one", null);
    Check(ungrouped.Favorites.Single(f => f.Id == "one").GroupId is null && ungrouped.Favorites.Length == 2, "Ungrouped move lost favorite");
    Check(ReferenceEquals(FavoriteGroups.MoveGroup(library, "team", "team"), library) &&
        ReferenceEquals(FavoriteGroups.MoveFavorite(library, "one", "team", "one"), library), "Self-drop changed order");
    void Reject(Action action)
    {
        try { action(); throw new Exception("Invalid group edit accepted"); } catch (InvalidDataException) { }
    }
    Reject(() => FavoriteGroups.Upsert(library, "duplicate", " kollegen "));
    Reject(() => FavoriteGroups.Upsert(library, "bad", "\r\n"));
    Reject(() => FavoriteGroups.Upsert(library, "bad", new('x', 61)));
    Reject(() => FavoriteGroups.MoveGroup(library, "missing", "team"));
    Reject(() => FavoriteGroups.MoveFavorite(library, "one", "missing"));
    Reject(() => FavoriteGroups.MoveFavorite(library, "missing", "team"));
    Reject(() => FavoriteGroups.MoveFavorite(library, "one", "clients", "team:tenant-A:two"));
    Reject(() => FavoriteGroups.Validate(library with { Favorites = [one with { GroupId = "missing" }] }));
    Reject(() => FavoriteGroups.Validate(library with { Groups = [new("team", "A"), new("team", "B")] }));
    Reject(() => FavoriteGroups.Validate(library with { Groups = Enumerable.Range(0, 51).Select(i => new FavoriteGroup(i.ToString(), "Gruppe " + i)).ToArray() }));
    var validJson = File.ReadAllText(path);
    Reject(() => migratedStore.SaveLibrary(library with { Favorites = [one with { GroupId = "missing" }] }));
    Check(File.ReadAllText(path) == validJson, "Rejected save overwrote valid data");
    foreach (var bad in new[] { "{\"Version\":2,\"Favorites\":[],\"Groups\":[]}", "{\"Version\":1,\"Favorites\":[],\"Groups\":null}",
        "{\"Version\":1,\"Favorites\":[{\"Id\":\"one\",\"Name\":\"A\",\"Number\":\"101\",\"GroupId\":\"missing\"}],\"Groups\":[]}" })
    {
        File.WriteAllText(path, bad);
        var badStore = new FavoritesStore(directory, "account-A", "tenant-A");
        Reject(() => badStore.LoadLibrary());
        try { badStore.SaveLibrary(library); throw new Exception("Invalid file overwritten"); } catch (InvalidOperationException) { }
        Check(File.ReadAllText(path) == bad, "Unsupported or invalid document destroyed");
    }
    File.WriteAllText(path, "broken-json");
    var corrupted = new FavoritesStore(directory, "account-A", "tenant-A");
    try { corrupted.Load(); throw new Exception("Corrupt file accepted"); } catch (System.Text.Json.JsonException) { }
    try { corrupted.Save(new[] { one }); throw new Exception("Corrupt file overwritten"); } catch (InvalidOperationException) { }
    Check(File.ReadAllText(path) == "broken-json", "Corrupt data was destroyed");
    Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Temporary files left behind");
    Console.WriteLine("PASS: dial targets, favorite groups, reorder and cross-group moves, non-destructive removal, migration/restart, account/company isolation and corrupt-file preservation.");
}
finally
{
    // Only this test's newly created, fixed child directory contains these disposable fixtures.
    foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}
