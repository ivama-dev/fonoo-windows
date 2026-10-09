using Fonoo.Windows.Accounts;
using Fonoo.Windows.Telephony;
using System.Net;
using System.Text.Json;
using System.Text;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static async Task Reject(Func<Task> action) { try { await action(); } catch (AccountException) { return; } throw new Exception("Unsafe response accepted"); }
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
var device = "b1eaf427-ef75-4e62-9730-c8433f07c829";
DeviceSession Session(string token = "a", string refresh = "b") => new() { Token = new(token[0], 43), RefreshToken = new(refresh[0], 43), DeviceId = device, UserId = "user-A", Email = "user@example.invalid", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), DeviceExpiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds() };
var summary = "{\"id\":\"user-A\",\"email\":\"user@example.invalid\"}";
var folder = Path.Combine(Path.GetTempPath(), "fonoo-desktop-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
try
{
    if (!args.Contains("--skip-dpapi"))
    {
    var protectedStore = new ProtectedSessionStore(folder); var session = Session(); protectedStore.Save(session);
    Check(protectedStore.Load()!.RefreshToken == session.RefreshToken, "DPAPI round trip");
    var ciphertext = File.ReadAllBytes(Path.Combine(folder, "session.protected"));
    Check(!Encoding.UTF8.GetString(ciphertext).Contains(session.RefreshToken), "Plaintext refresh token on disk");
    protectedStore.Clear(); Check(protectedStore.Load() is null, "DPAPI logout removal");
    }
    var history = new CallHistoryStore(folder, "user-A", "tenant-A"); Check(history.Load().Length == 0, "History starts empty");
    var entry = new CallHistoryEntry(Guid.NewGuid().ToString(), "101", true, DateTimeOffset.UtcNow, 35, "completed"); history.Save([entry]);
    Check(new CallHistoryStore(folder, "user-A", "tenant-A").Load().Single() == entry, "History survives restart");
    Check(new CallHistoryStore(folder, "user-B", "tenant-A").Load().Length == 0 && new CallHistoryStore(folder, "user-A", "tenant-B").Load().Length == 0, "History scope isolation");
    var historyPath = Directory.GetFiles(folder, "history-*.json").Single(); File.WriteAllText(historyPath, "broken");
    var broken = new CallHistoryStore(folder, "user-A", "tenant-A"); try { broken.Load(); } catch (JsonException) { }
    try { broken.Save([]); throw new Exception("Corrupt history overwritten"); } catch (InvalidOperationException) { }
    Check(File.ReadAllText(historyPath) == "broken", "Corrupt history preserved");
    var vcard = VCardDirectory.Parse("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Alex\\, Beispiel\r\nTEL;TYPE=WORK:+43 (1) 234-\r\n 567\r\nTEL:sip:evil@other.example\r\nEND:VCARD\r\n");
    Check(vcard.Length == 1 && vcard[0].Name == "Alex, Beispiel" && vcard[0].Number == "+431234567", "vCard unfolding and number validation");
    Check(vcard[0].Matches("alex 234") && !vcard[0].Matches("alex missing"), "Contact search all words");

    var store = new MemoryStore { Saved = Session() };
    var handler = new FakeHandler();
    using var client = new AccountClient(handler, store, device); Check(client.Restore(), "Remembered session restore");
    var requestId = "";
    handler.Next = async request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("device/refresh"))
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            requestId = body.RootElement.GetProperty("request_id").GetString()!;
            Check(store.Saved!.PendingRequestId == requestId, "Rotation id must be saved before HTTP");
            throw new HttpRequestException("Test: lost response");
        }
        return FakeHandler.Response("{}", HttpStatusCode.Unauthorized);
    };
    try { await client.GetAccountAsync(default); } catch (HttpRequestException) { }
    Check(client.SignedIn && store.Saved!.PendingRequestId == requestId, "Network failure must preserve recovery credential");
    handler.Next = async request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("device/refresh"))
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); Check(body.RootElement.GetProperty("request_id").GetString() == requestId, "Lost rotation retries same request id");
            return FakeHandler.Response(JsonSerializer.Serialize(Session("c", "d"), json));
        }
        return request.Headers.Authorization?.Parameter == new string('c', 43) ? FakeHandler.Response(summary) : FakeHandler.Response("{}", HttpStatusCode.Unauthorized);
    };
    Check((await client.GetAccountAsync(default)).Id == "user-A" && store.Saved!.PendingRequestId is null, "Recovered session rotation");
    client.SignOut(); Check(!client.SignedIn && store.Saved is null, "Logout clears credential");

    // Both callers receive 401 for the old access token; refresh must be coalesced.
    store.Saved = Session(); client.Restore(); var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var oldRequests = 0; var renewals = 0;
    handler.Next = async request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("device/refresh")) { Interlocked.Increment(ref renewals); return FakeHandler.Response(JsonSerializer.Serialize(Session("e", "f"), json)); }
        if (request.Headers.Authorization?.Parameter == new string('a', 43))
        { if (Interlocked.Increment(ref oldRequests) == 2) both.TrySetResult(); await both.Task; return FakeHandler.Response("{}", HttpStatusCode.Unauthorized); }
        return FakeHandler.Response(summary);
    };
    await Task.WhenAll(client.GetAccountAsync(default), client.GetAccountAsync(default)); Check(renewals == 1, "Concurrent 401s rotated twice");
    handler.Next = _ => Task.FromResult(FakeHandler.Response("{}", HttpStatusCode.Unauthorized));
    await Reject(() => client.GetAccountAsync(default)); Check(!client.SignedIn && store.Saved is null, "Confirmed revocation clears credentials");

    // A delayed authenticated response must not revive a logged-out session.
    store.Saved = Session(); client.Restore(); var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    handler.Next = _ => release.Task; var delayed = client.GetAccountAsync(default); client.SignOut(); release.SetResult(FakeHandler.Response(summary));
    try { await delayed; throw new Exception("Late old response accepted"); } catch (OperationCanceledException) { }

    // Compatibility with services that lack device/verify: only 404 falls back.
    var paths = new List<string>(); handler.Next = request =>
    {
        paths.Add(request.RequestUri!.AbsolutePath);
        return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("device/verify") ? FakeHandler.Response("{}", HttpStatusCode.NotFound) : FakeHandler.Response("{\"token\":\"" + new string('g', 43) + "\"}"));
    };
    await client.VerifyDeviceAsync("synthetic-challenge", "123456", default); client.RememberAccessSession(new() { Id = "user-A", Email = "user@example.invalid" });
    Check(paths.Count == 2 && client.Remembered && !client.CanRenew && store.Saved!.AccessOnly, "Protected access-session fallback");
    client.Dispose(); Check(store.Saved is not null, "Closing app deleted remembered login");
    using var restored = new AccountClient(handler, store, device); Check(restored.Restore(), "Access-only session restart");
    handler.Next = _ => Task.FromResult(FakeHandler.Response("{}", HttpStatusCode.Unauthorized)); await Reject(() => restored.GetAccountAsync(default)); Check(!restored.SignedIn, "Expired legacy token attempts unsupported refresh");

    var team = new TeamSnapshot { SchemaVersion = 1, TenantId = "tenant-A", TenantName = "Firma", SelfUserId = "user-A", Revision = 2,
        Members = [new() { Id = "user-A", Name = "Du", Email = "user@example.invalid", Number = "100" }, new() { Id = "user-B", Name = "Alex", Email = "alex@example.invalid", Number = "101" }] };
    team.Validate("tenant-A", "user-A");
    try { team.Validate("tenant-B", "user-A"); throw new Exception("Foreign team accepted"); } catch (AccountException) { }
    team.Members[1].PersonalDevices = [new() { Id = "private-device", Name = "Private" }]; team.Members[1].PersonalDeviceCount = 1;
    try { team.Validate("tenant-A", "user-A"); throw new Exception("Foreign private devices accepted"); } catch (AccountException) { }
    team.Members[1].PersonalDevices = []; team.Members[1].Number = "sip:101@evil.example";
    try { team.Validate("tenant-A", "user-A"); throw new Exception("SIP injection accepted"); } catch (AccountException) { }
    Console.WriteLine("PASS: " + (args.Contains("--skip-dpapi") ? "DPAPI skipped (requires Windows user context); " : "DPAPI; ") + "restart, rotation recovery/coalescing/revocation, stale-response rejection, legacy compatibility, scoped history, vCards and team isolation.");
}
catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.Message); return 1; }
finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }

return 0;

sealed class MemoryStore : IDeviceSessionStore
{
    public DeviceSession? Saved;
    public DeviceSession? Load() => Saved is null ? null : JsonSerializer.Deserialize<DeviceSession>(JsonSerializer.Serialize(Saved));
    public void Save(DeviceSession session) => Saved = JsonSerializer.Deserialize<DeviceSession>(JsonSerializer.Serialize(session));
    public void Clear() => Saved = null;
}
sealed class FakeHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Next = _ => throw new Exception("Unexpected HTTP request");
    public static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json) };
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Next(request); }
}
