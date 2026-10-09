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
    File.WriteAllText(path, "broken-json");
    var corrupted = new FavoritesStore(directory, "account-A", "tenant-A");
    try { corrupted.Load(); throw new Exception("Corrupt file accepted"); } catch (System.Text.Json.JsonException) { }
    try { corrupted.Save(new[] { one }); throw new Exception("Corrupt file overwritten"); } catch (InvalidOperationException) { }
    Check(File.ReadAllText(path) == "broken-json", "Corrupt data was destroyed");
    Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Temporary files left behind");
    Console.WriteLine("PASS: formatted dial targets, unsafe target rejection, favorites persistence, tenant/account isolation and corrupt-file preservation.");
}
finally
{
    // Only this test's newly created, fixed child directory contains these disposable fixtures.
    foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}
