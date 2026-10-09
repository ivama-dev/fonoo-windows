using Fonoo.Windows.Accounts;
using Fonoo.Windows.Telephony;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
static void Reject(Action action, string reason) { try { action(); } catch (AccountException) { return; } throw new Exception(reason); }
var id = Guid.NewGuid().ToString();
var snapshot = new CloudHistorySnapshot { SchemaVersion = 1, TenantId = "company", SelfUserId = "user", Revision = 7,
    Entries = [new() { Id = id, Number = "+437200101010", Incoming = true, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds(), DurationSeconds = 43, Outcome = "completed" }] };
Check(snapshot.Validate("company", "user").Single().DurationSeconds == 43, "Completed incoming call lost");
Reject(() => snapshot.Validate("other-company", "user"), "Cross-company history accepted");
Reject(() => snapshot.Validate("company", "other-user"), "Cross-account history accepted");
snapshot.Entries = [snapshot.Entries[0], snapshot.Entries[0]];
Reject(() => snapshot.Validate("company", "user"), "Duplicate canonical calls accepted");
snapshot.Entries = [snapshot.Entries[0]];
snapshot.Entries[0].Outcome = "missed";
Reject(() => snapshot.Validate("company", "user"), "Missed call with conversation duration accepted");
snapshot.Entries[0].DurationSeconds = 0;
snapshot.Entries[0].Incoming = false;
Reject(() => snapshot.Validate("company", "user"), "Outgoing call accepted as missed");
snapshot.Entries[0].Incoming = true;
snapshot.Validate("company", "user");

var directory = Path.Combine(Path.GetTempPath(), "fonoo-history-" + Guid.NewGuid().ToString("N"));
var protectionKey = RandomNumberGenerator.GetBytes(32);
byte[] ProtectFixture(byte[] value, bool encrypt)
{
    using var aes = new AesGcm(protectionKey, 16);
    if (encrypt)
    {
        var result = new byte[value.Length + 28]; RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        aes.Encrypt(result.AsSpan(0, 12), value, result.AsSpan(28), result.AsSpan(12, 16)); return result;
    }
    if (value.Length < 28) throw new CryptographicException("Invalid test ciphertext.");
    var plain = new byte[value.Length - 28];
    aes.Decrypt(value.AsSpan(0, 12), value.AsSpan(28), value.AsSpan(12, 16), plain); return plain;
}
try
{
    // Portable tests use authenticated encryption; the native WinUI preview checks real Windows DPAPI.
    var cache = new CloudHistoryCache(directory, "user", "company", protection: ProtectFixture);
    cache.Save(snapshot);
    Check(cache.Load()!.Entries.Single().Id == id, "Offline cache round-trip failed");
    Check(new CloudHistoryCache(directory, "other-user", "company").Load() is null, "Account cache isolation failed");
    Check(new CloudHistoryCache(directory, "user", "other-company").Load() is null, "Company cache isolation failed");
    snapshot.NotModified = true;
    Reject(() => snapshot.Validate("company", "user"), "Unchanged reply contained entries");
    snapshot.NotModified = false;
    Check(cache.Load()!.Revision == 7, "Invalid response overwrote offline cache");
    var protectedPath = Directory.GetFiles(directory, "*.protected").Single();
    Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(protectedPath)).Contains(id), "Plain call identifier written to disk");

    var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    var clock = new HistoryClock(now);
    var expiryFolder = Path.Combine(directory, "expiry");
    var expiryCache = new CloudHistoryCache(expiryFolder, "user", "company", clock, ProtectFixture);
    CloudHistoryEntry Row(int ageOffset) => new() { Id = Guid.NewGuid().ToString(), Number = "101", Incoming = true,
        StartedAt = (now - CloudHistoryCache.Retention).ToUnixTimeSeconds() + ageOffset, Outcome = "missed" };
    var retained = new CloudHistorySnapshot { SchemaVersion = 1, TenantId = "company", SelfUserId = "user", Revision = 9,
        Entries = [Row(-1), Row(0), Row(1)] };
    Check(expiryCache.Save(retained).Entries.Length == 2, "90-day retention boundary changed");
    Check(expiryCache.Load()!.Entries.Length == 2, "Expired call restored from offline cache");
    clock.UtcNow = now.AddSeconds(2);
    Check(expiryCache.PruneExpired()!.Entries.Length == 0, "Open offline app kept expired entries");
    Check(new CloudHistoryCache(expiryFolder, "user", "company", clock, ProtectFixture).Load()!.Entries.Length == 0, "Expired records remained in persisted cache");

    var migrationFolder = Path.Combine(directory, "migration"); Directory.CreateDirectory(migrationFolder);
    var scope = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("user\ncompany")));
    var legacyPath = Path.Combine(migrationFolder, "cloud-history-v1-" + scope + ".json");
    File.WriteAllText(legacyPath, JsonSerializer.Serialize(snapshot));
    var migration = new CloudHistoryCache(migrationFolder, "user", "company", protection: ProtectFixture);
    Check(migration.Load()!.Entries.Single().Id == id && !File.Exists(legacyPath), "Plaintext cache migration lost a call or kept the old copy");
    File.WriteAllText(legacyPath, JsonSerializer.Serialize(snapshot));
    File.WriteAllBytes(Directory.GetFiles(migrationFolder, "*.protected").Single(), [1, 2, 3]);
    try { migration.Load(); throw new Exception("Corrupt protected cache fell back to an old deleted list"); }
    catch (CryptographicException) { }
}
finally { CryptographicOperations.ZeroMemory(protectionKey); if (Directory.Exists(directory)) Directory.Delete(directory, true); }

var handler = new HistoryHandler();
using var client = new AccountClient(handler);
await client.PasswordAsync("synthetic@example.invalid", "synthetic", default);
var read = await client.ReadHistoryAsync("company", 7, default);
Check(read.Validate("company", "user").Length == 0 && read.NotModified, "Revision request failed");
await client.ClearHistoryAsync("company", default);
await client.DeleteHistoryAsync("company", id, default);
Check(handler.Read && handler.Delete && handler.SingleDelete, "Missing account API operations");
try { await client.DeleteHistoryAsync("company", "invalid-id", default); throw new Exception("Invalid deletion reached API"); }
catch (AccountException) { }
Console.WriteLine("PASS: scope isolation, canonical IDs, outcomes, 90-day offline expiry, protected-cache migration and authenticated whole-list/single-call deletion.");

sealed class HistoryHandler : HttpMessageHandler
{
    public bool Read, Delete, SingleDelete;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/account/password")) return Reply("{\"token\":\"synthetic-token\"}");
        if (request.Method != HttpMethod.Post || request.Headers.Authorization?.ToString() != "Bearer synthetic-token") throw new Exception("History missing authentication");
        if (request.Content!.Headers.ContentLength is null) throw new Exception("History needs Content-Length");
        using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
        if (json.RootElement.GetProperty("tenant_id").GetString() != "company") throw new Exception("History company missing");
        if (path.EndsWith("/call-history/read"))
        {
            if (json.RootElement.GetProperty("revision").GetInt64() != 7) throw new Exception("History revision missing");
            Read = true;
            return Reply("{\"schema_version\":1,\"tenant_id\":\"company\",\"self_user_id\":\"user\",\"revision\":7,\"not_modified\":true,\"entries\":[]}");
        }
        if (!path.EndsWith("/call-history/delete")) throw new Exception("Wrong deletion route");
        if (json.RootElement.TryGetProperty("clear_all", out var all))
        {
            if (!all.GetBoolean() || json.RootElement.TryGetProperty("ids", out _)) throw new Exception("Wrong whole-list deletion scope");
            Delete = true;
        }
        else
        {
            var ids = json.RootElement.GetProperty("ids");
            if (ids.GetArrayLength() != 1 || !Guid.TryParse(ids[0].GetString(), out _)) throw new Exception("Wrong single-call deletion scope");
            SingleDelete = true;
        }
        return Reply("{\"schema_version\":1,\"tenant_id\":\"company\",\"self_user_id\":\"user\",\"revision\":8,\"not_modified\":false,\"entries\":[]}");
    }
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

sealed class HistoryClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}
