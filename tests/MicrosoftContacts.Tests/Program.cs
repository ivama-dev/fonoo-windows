using Fonoo.Windows.Microsoft365;
using Fonoo.Windows.Telephony;
using System.Net;
using System.Text;
using System.Text.Json;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Rejected(Func<Task> action) { try { await action(); } catch (MicrosoftConnectionException) { return; } throw new Exception("Unsafe data accepted"); }
static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
const string clientId = "34a7c22b-bfe8-4381-ae47-9bc1e20ae912";
var folder = Path.Combine(Path.GetTempPath(), "fonoo-outlook-tests-" + Guid.NewGuid().ToString("N"));
try
{
    Check(!new MicrosoftOptions().Configured && !new MicrosoftOptions { ClientId = Guid.Empty.ToString() }.Configured && new MicrosoftOptions { ClientId = clientId }.Configured, "Registration configuration validation");
    Check(MicrosoftIdentity.Scopes.SequenceEqual(new[] { "https://graph.microsoft.com/Contacts.Read" }), "Only delegated contact scope");
    foreach (var unsafeLink in new[] { "http://graph.microsoft.com/v1.0/me/contacts", "https://evil.example/v1.0/me/contacts", "https://graph.microsoft.com:444/v1.0/me/contacts", "https://graph.microsoft.com/v1.0/users/other/contacts", "https://graph.microsoft.com/v1.0/me/messages", "https://user@graph.microsoft.com/v1.0/me/contacts" })
        await Rejected(() => Task.FromResult(GraphContactsReader.ValidatedUri(unsafeLink)));
    var handler = new Handler();
    handler.Next = request =>
    {
        Check(request.Method == HttpMethod.Get && request.Headers.Authorization?.Parameter == "synthetic-token", "Read-only authorized request");
        var path = request.RequestUri!.AbsolutePath;
        if (request.RequestUri.Query.Contains("skiptoken")) return Task.FromResult(Json("{\"value\":[{\"id\":\"b\",\"displayName\":\"Bea\",\"mobilePhone\":\"102\"}]}"));
        if (path == "/v1.0/me/contacts") return Task.FromResult(Json("{\"value\":[{\"id\":\"a\",\"displayName\":\"Alex\",\"businessPhones\":[\"+43 (1) 234-567\",\"sip:evil@example.invalid\"],\"homePhones\":[\"+431234567\"]}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/me/contacts?$skiptoken=next\"}"));
        if (path == "/v1.0/me/contactFolders") return Task.FromResult(Json("{\"value\":[{\"id\":\"root\"}]}"));
        if (path == "/v1.0/me/contactFolders/root/contacts") return Task.FromResult(Json("{\"value\":[{\"id\":\"c\",\"givenName\":\"Chris\",\"surname\":\"Test\",\"homePhones\":[\"103\"]}]}"));
        if (path == "/v1.0/me/contactFolders/root/childFolders") return Task.FromResult(Json("{\"value\":[{\"id\":\"child\"}]}"));
        if (path.EndsWith("child/contacts")) return Task.FromResult(Json("{\"value\":[{\"id\":\"d\",\"displayName\":\"Dana\",\"mobilePhone\":\"104\"}]}"));
        return Task.FromResult(Json("{\"value\":[]}"));
    };
    using var reader = new GraphContactsReader(handler);
    var contacts = await reader.ReadAsync((_, _) => Task.FromResult("synthetic-token"), default);
    Check(contacts.Length == 4 && contacts.Any(c => c.Name == "Chris Test") && contacts.Any(c => c.Number == "+431234567") && contacts.All(c => !c.Number.Contains('@')), "Paging, nested folders, phone normalization and injection rejection");
    var requests = 0;
    handler.Next = request => { requests++; return Task.FromResult(Json("{\"value\":[],\"@odata.nextLink\":\"https://evil.example/v1.0/me/contacts\"}")); };
    await Rejected(() => reader.ReadAsync((_, _) => Task.FromResult("synthetic-token"), default)); Check(requests == 1, "Token must never follow untrusted pagination");
    var forced = 0; var unauthorized = true;
    handler.Next = _ => { if (unauthorized) { unauthorized = false; return Task.FromResult(Json("{}", HttpStatusCode.Unauthorized)); } return Task.FromResult(Json("{\"value\":[]}")); };
    await reader.ReadAsync((force, _) => { if (force) forced++; return Task.FromResult("synthetic-token"); }, default); Check(forced == 1, "One forced renewal on 401");
    handler.Next = _ => Task.FromResult(Json("{}", HttpStatusCode.Forbidden)); await Rejected(() => reader.ReadAsync((_, _) => Task.FromResult("synthetic-token"), default));
    handler.Next = _ => Task.FromResult(Json("{}", HttpStatusCode.TooManyRequests)); await Rejected(() => reader.ReadAsync((_, _) => Task.FromResult("synthetic-token"), default));

    var store = new Store { Saved = new("fonoo-A", clientId, "microsoft-A", "a@example.invalid", DateTimeOffset.UtcNow, [new("id", "Alex", "101", "Outlook")]) };
    var identity = new Identity();
    using var connection = new MicrosoftContactConnection(identity, reader, store, "fonoo-A", clientId);
    await connection.RestoreAsync(default); Check(connection.Snapshot!.Contacts.Length == 1 && identity.Interactive == 0, "Cached startup never prompts");
    handler.Next = _ => throw new HttpRequestException("Synthetic offline failure");
    try { await connection.RefreshAsync(default); } catch (HttpRequestException) { }
    Check(connection.Snapshot!.Contacts.Length == 1 && store.Saved!.Contacts.Length == 1, "Offline failure preserves complete cache");
    identity.SelectedId = "microsoft-B"; await Rejected(() => connection.RefreshAsync(default));
    Check(connection.SignInRequired && connection.Snapshot!.Contacts.Length == 0, "Changed identity rejected and cached private data cleared");
    identity.SelectedId = "microsoft-A";
    handler.Next = _ => Task.FromResult(Json("{\"value\":[]}")); await connection.ConnectAsync(default);
    Check(identity.Interactive == 1 && connection.Snapshot?.MicrosoftAccountId == "microsoft-A", "Explicit connection records confirmed identity");

    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    handler.Next = async _ => { entered.TrySetResult(); await release.Task; return Json("{\"value\":[]}"); };
    var refresh = connection.RefreshAsync(default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var disconnect = connection.DisconnectAsync(); Check(connection.Snapshot is null && store.Saved is null, "Disconnect clears locally before pending response");
    release.SetResult(); try { await refresh; } catch (OperationCanceledException) { }
    await disconnect; Check(store.Saved is null && identity.Removed == 1, "Late response cannot recreate credentials or contacts");
    Check(ProtectedMicrosoftContactStore.ScopedDirectory(folder, "fonoo-A", clientId) != ProtectedMicrosoftContactStore.ScopedDirectory(folder, "fonoo-B", clientId), "Fonoo account cache isolation");
    var tenantIdentity = new Identity { SelectedTenant = "5759693e-a57b-442e-aee5-843c4aa7610f" };
    var tenantHandler = new Handler { Next = _ => Task.FromResult(Json("{\"value\":[]}")) };
    var tenantStore = new Store();
    using var tenantConnection = new MicrosoftContactConnection(tenantIdentity, new GraphContactsReader(tenantHandler), tenantStore, "fonoo-A", clientId, tenantIdentity.SelectedTenant);
    await tenantConnection.ConnectAsync(default);
    Check(tenantStore.Saved?.TenantId == tenantIdentity.SelectedTenant, "Confirmed company tenant persisted");
    tenantIdentity.SelectedTenant = "11111111-1111-1111-1111-111111111111";
    tenantHandler.Next = _ => throw new Exception("A foreign-tenant token reached Graph");
    await Rejected(() => tenantConnection.RefreshAsync(default));
    Check(tenantConnection.SignInRequired && tenantConnection.Snapshot!.Contacts.Length == 0, "Foreign company rejected before Graph");
    var foreignCache = new Store { Saved = tenantStore.Saved };
    using var isolated = new MicrosoftContactConnection(new Identity(), new GraphContactsReader(), foreignCache, "fonoo-A", clientId, "22222222-2222-2222-2222-222222222222");
    await isolated.RestoreAsync(default);
    Check(isolated.Snapshot is null && foreignCache.Saved is null, "Foreign tenant cached contacts cannot be restored");
    if (!args.Contains("--skip-dpapi"))
    {
        var protectedStore = new ProtectedMicrosoftContactStore(folder, "fonoo-A", clientId);
        var snapshot = new MicrosoftContactSnapshot("fonoo-A", clientId, "microsoft-A", "private@example.invalid", DateTimeOffset.UtcNow, [new("id", "Private Example", "101", "Outlook")]);
        protectedStore.Save(snapshot); Check(protectedStore.Load()!.Contacts.Single().Name == "Private Example", "Protected contact restart");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "contacts.protected"))).Contains("Private Example"), "No plaintext contact cache");
        try { new ProtectedMicrosoftContactStore(folder, "fonoo-B", clientId).Load(); throw new Exception("Foreign cache accepted"); } catch (InvalidDataException) { }
        protectedStore.Clear();
    }
    Console.WriteLine("PASS: least-privilege scope, trusted paging, folders, number validation, 401 renewal, denial/throttling, offline cache, identity isolation and disconnect races." + (args.Contains("--skip-dpapi") ? " DPAPI skipped (requires Windows user context)." : " DPAPI passed."));
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.Message); return 1; }
finally { if (Directory.Exists(folder)) Directory.Delete(folder); }

sealed class Handler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Next = _ => throw new Exception("Unexpected HTTP");
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Next(request);
}
sealed class Store : IMicrosoftContactStore
{
    public MicrosoftContactSnapshot? Saved;
    public MicrosoftContactSnapshot? Load() => Saved;
    public void Save(MicrosoftContactSnapshot value) => Saved = value;
    public void Clear() => Saved = null;
}
sealed class Identity : IMicrosoftIdentity
{
    public string SelectedId = "microsoft-A";
    public string SelectedTenant = "";
    public int Interactive, Removed;
    public Task<MicrosoftSession> ConnectAsync(CancellationToken ct) { Interactive++; return Task.FromResult(new MicrosoftSession(SelectedId, "a@example.invalid", "synthetic-token", SelectedTenant)); }
    public Task<MicrosoftSession> AcquireSilentAsync(string accountId, bool forceRefresh, CancellationToken ct) => Task.FromResult(new MicrosoftSession(SelectedId, "a@example.invalid", "synthetic-token", SelectedTenant));
    public Task RemoveAsync(string accountId) { Removed++; return Task.CompletedTask; }
    public void Dispose() { }
}
