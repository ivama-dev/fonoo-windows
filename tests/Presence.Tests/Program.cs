using Fonoo.Windows.Accounts;
using Fonoo.Windows.Telephony;
using System.Net;
using System.Text.Json;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static void Invalid(Action work, string message)
{
    try { work(); } catch (AccountException) { return; }
    throw new Exception(message);
}
var time = new TestClock();
PresenceSnapshot Snapshot(double offset = 0) => new() {
    SchemaVersion = 1, TenantId = "company", SelfUserId = "self", CollectorAvailable = true,
    ObservedAt = time.GetUtcNow().ToUnixTimeSeconds() + offset, ExpiresAt = time.GetUtcNow().ToUnixTimeSeconds() + offset + 15,
    Members = [new() { UserId = "self", State = "idle" },
        new() { UserId = "alex", State = "busy", CallCount = 1, PeerNumber = "102", PeerUserId = "mara", Direction = "outgoing" },
        new() { UserId = "mara", State = "busy", CallCount = 1, PeerNumber = "101", PeerUserId = "alex", Direction = "incoming" }] };
var good = Snapshot(); good.Validate("company", "self");
Invalid(() => good.Validate("foreign", "self"), "Foreign company accepted");
Invalid(() => good.Validate("company", "other"), "Foreign session identity accepted");
var bad = Snapshot(); bad.SchemaVersion = 2; Invalid(() => bad.Validate("company", "self"), "Unknown schema accepted");
bad = Snapshot(); bad.Members[2].UserId = "alex"; Invalid(() => bad.Validate("company", "self"), "Duplicate identity accepted");
bad = Snapshot(); bad.Members[1].PeerUserId = "foreign"; Invalid(() => bad.Validate("company", "self"), "Foreign peer accepted");
bad = Snapshot(); bad.Members[1].Direction = "sideways"; Invalid(() => bad.Validate("company", "self"), "Invalid direction accepted");
bad = Snapshot(); bad.ExpiresAt = double.NaN; Invalid(() => bad.Validate("company", "self"), "NaN deadline accepted");
bad = Snapshot(); bad.ExpiresAt = good.ObservedAt + 60; Invalid(() => bad.Validate("company", "self"), "Long-lived presence accepted");
bad = Snapshot(); bad.Members[0].CallCount = 1; Invalid(() => bad.Validate("company", "self"), "Calling user marked idle");

var state = new TeamPresenceState("company", "self", time);
Check(state.TryApply(good) && state.Current is not null, "Fresh snapshot missing");
time.Advance(14); Check(state.Current is not null, "Snapshot expired early");
// This read deliberately remains in flight; expiry must not wait for its completion.
var stalled = new TaskCompletionSource<PresenceSnapshot>();
time.Advance(1); Check(state.Expire() && state.Current is null && !stalled.Task.IsCompleted, "Expiry waits for HTTP");
Check(state.TryApply(Snapshot()), "New snapshot rejected");
Check(!state.TryApply(good), "Older observed_at rolls back presence");
state.Clear(); Check(state.Current is null && !state.TryApply(good), "Suspension allows stale presence resurrection");
Check(state.TryApply(Snapshot()), "Fresh presence after suspension rejected");
bad = Snapshot(); bad.CollectorAvailable = false; bad.ObservedAt = bad.ExpiresAt = null;
Check(state.TryApply(bad) && state.Current is null, "Unavailable collector displays status");
state.TryApply(Snapshot()); time.AdvanceMonotonic(15);
Check(state.Current is null, "Backwards/frozen wall clock extends lifetime");
var anotherCompany = new TeamPresenceState("foreign", "self", time);
Invalid(() => anotherCompany.TryApply(good), "Company switch accepts late old response");

var team = new TeamSnapshot { TenantId = "company", SelfUserId = "self", Members = [
    new() { Id = "alex", Name = "Alex Beispiel", Number = "101" }, new() { Id = "mara", Name = "Mara Beispiel", Number = "102" }] };
var internalFavorite = new Favorite("team:company:alex", "Alex", "101");
Check(TeamPresenceResolver.FavoriteMember(internalFavorite, team, "company", "self")?.Id == "alex", "Scoped favorite missing");
Check(TeamPresenceResolver.FavoriteMember(internalFavorite with { Id = "team:foreign:alex" }, team, "company", "self") is null, "Foreign favorite correlates by extension");
Check(TeamPresenceResolver.FavoriteMember(internalFavorite with { Id = "local" }, team, "company", "self")?.Id == "alex", "Legacy exact internal favorite missing");
Check(TeamPresenceResolver.FavoriteMember(internalFavorite with { Id = "local", Number = "+43101" }, team, "company", "self") is null, "Number suffix matched");
team.Members = [..team.Members, new() { Id = "duplicate", Name = "Other", Number = "101" }];
Check(TeamPresenceResolver.FavoriteMember(internalFavorite with { Id = "local" }, team, "company", "self") is null, "Ambiguous extension guessed");
team.Members = team.Members[..2];
PhonePresenceDisplay Display(PresenceSnapshot source, ContactRow[]? contacts = null, Favorite[]? favorites = null) =>
    TeamPresenceResolver.Display("alex", source, team, contacts ?? [], favorites ?? []);
Check(Display(Snapshot()).Detail == "Mit Mara Beispiel", "Internal peer identity not resolved");
bad = Snapshot(); bad.Members[1].PeerNumber = null;
Check(Display(bad).Detail == "", "Suppressed number leaks identity from peer_user_id");
bad.Members[1].CallCount = 2; Check(Display(bad).Detail == "2 Gespräche", "Multiple calls show peer identity");
foreach (var phase in new[] { "ringing", "dialing", "unknown", "idle" })
{
    bad = Snapshot(); bad.Members[1].State = phase; bad.Members[1].CallCount = 0;
    Check(Display(bad).Detail == "", "Unanswered/unknown/idle call shows peer identity");
}
bad = Snapshot(); bad.Members[1].PeerUserId = null; bad.Members[1].PeerNumber = "+437200101010";
var permitted = new ContactRow("outlook", "Kunde", "0720 0101010", "Arbeit");
Check(Display(bad, [permitted]).Detail == "Mit Kunde", "AT national contact not resolved");
bad.Members[1].PeerNumber = "00437200101010";
Check(Display(bad, [permitted]).Detail == "Mit Kunde", "0043 contact not resolved");
Check(Display(bad).Detail == "Mit 00437200101010", "Revoked/absent contact permission retains name");
Check(Display(bad, [permitted, permitted with { Name = "Andere Person" }]).Detail == "Mit 00437200101010", "Ambiguous contact guessed");
Check(Display(bad, [], [new("team:foreign:user", "Private name", "00437200101010")]).Detail == "Mit 00437200101010", "Foreign favorite name leaked");
Check(TeamPresenceResolver.NumberKey("0101") != TeamPresenceResolver.NumberKey("101"), "Short extension normalized incorrectly");
Check(TeamPresenceResolver.Display("missing", good, team, [], []).Text == "Telefonstatus unbekannt", "Missing member defaults to idle");

var handler = new Handler(); using var client = new AccountClient(handler);
await client.PasswordAsync("synthetic@example.invalid", "test", default);
var session = client.SessionGeneration;
handler.Next = JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
var read = await client.GetPresenceAsync("company", "self", default);
Check(read.TenantId == "company" && client.SessionGeneration == session, "Presence changes login/session");
handler.Next = handler.Next.Replace("\"tenant_id\":\"company\"", "\"tenant_id\":\"foreign\"");
try { await client.GetPresenceAsync("company", "self", default); throw new Exception("Foreign API response accepted"); } catch (AccountException) { }
handler.Next = JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
handler.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
var oldRequest = client.GetPresenceAsync("company", "self", default);
client.SignOut(); handler.Wait.SetResult();
try { await oldRequest; throw new Exception("Late logout response applied"); } catch (OperationCanceledException) { }
handler.Wait = null; await client.PasswordAsync("synthetic@example.invalid", "test", default);
handler.Status = HttpStatusCode.Forbidden;
try { await client.GetPresenceAsync("company", "self", default); throw new Exception("403 accepted"); } catch (AccountException ex) { Check(ex.StatusCode == 403, "403 missing"); }
handler.Status = HttpStatusCode.Unauthorized;
try { await client.GetPresenceAsync("company", "self", default); throw new Exception("401 accepted"); } catch (AccountException ex) { Check(ex.SessionExpired && !client.SignedIn, "Expired session retained"); }
Console.WriteLine("PASS: authenticated presence API, schema/scope/session isolation, independent 15-second expiry, stale reply rejection, internal favorites, private/ambiguous peers and AT contact matching.");

sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1791467696);
    private long ticks;
    public override DateTimeOffset GetUtcNow() => now;
    public override long GetTimestamp() => ticks;
    public override long TimestampFrequency => 1000;
    public void Advance(int seconds) { now = now.AddSeconds(seconds); AdvanceMonotonic(seconds); }
    public void AdvanceMonotonic(int seconds) => ticks += seconds * 1000;
}
sealed class Handler : HttpMessageHandler
{
    public string Next = "";
    public HttpStatusCode Status = HttpStatusCode.OK;
    public TaskCompletionSource? Wait;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
        Check(request.Content.Headers.ContentLength == bytes.Length, "Missing Content-Length");
        if (request.RequestUri!.AbsolutePath == "/v1/account/password") return new(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"synthetic-session\"}") };
        Check(request.RequestUri.AbsolutePath == "/v1/cloud/team/presence" && request.Method == HttpMethod.Post, "Wrong presence endpoint/method");
        Check(request.Headers.Authorization?.ToString() == "Bearer synthetic-session", "Missing bearer");
        using var data = JsonDocument.Parse(bytes);
        Check(data.RootElement.EnumerateObject().Count() == 1 && data.RootElement.GetProperty("tenant_id").GetString() == "company", "Request uploads more than tenant ID");
        if (Wait is not null) await Wait.Task.WaitAsync(ct);
        return new(Status) { Content = new StringContent(Next) };
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
