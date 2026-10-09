using System.Net;
using System.Text;
using System.Text.Json;
using Fonoo.Windows.Accounts;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
const string fixture = """
{"schema_version":1,"device_profiles_version":1,"profiles_available":true,"tenant_id":"alpha","user_id":"owner","self_user_id":"owner","revision":7,"settings":{"presence":null,"work_mode":"custom","mode_devices":{},"schedule_id":null,"device_profiles":[{"id":"standard","name":"Standard","device_ids":null},{"id":"work","name":"Büro","device_ids":["mac"]}],"active_profile_id":"work"},"effective":{"eligible":true,"reason_text":"Verfügbar","effective_devices":[]},"call_teams":[],"personal_devices":[{"id":"mac","name":"Mac","user_id":"owner","enabled":true},{"id":"phone","name":"iPhone","user_id":"owner","enabled":false},{"id":"foreign","name":"Fremd","user_id":"other","enabled":true}],"schedules":[],"company":{"routing_enabled":false}}
""";
AvailabilitySnapshot Decode(string value) => JsonSerializer.Deserialize<AvailabilitySnapshot>(value, json)!.Validate("alpha", "owner");
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch (AccountException) { return; } throw new Exception("Invalid profile accepted"); }
var current = Decode(fixture);
Check(current.OwnDevices.Length == 2 && !current.Company.RoutingEnabled && current.Settings.ActiveProfile.Name == "Büro", "Profiles depend only on own devices, independently of advanced routing");
var standardJson = JsonSerializer.Serialize(DeviceProfile.Standard(), new JsonSerializerOptions(json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
Check(JsonDocument.Parse(standardJson).RootElement.GetProperty("device_ids").ValueKind == JsonValueKind.Null, "Standard must explicitly send null");
foreach (var bad in new[] { fixture.Replace("[\"mac\"]", "[\"foreign\"]"), fixture.Replace("[\"mac\"]", "[\"mac\",\"mac\"]"),
    fixture.Replace("\"active_profile_id\":\"work\"", "\"active_profile_id\":\"absent\""), fixture.Replace("\"device_profiles_version\":1", "\"device_profiles_version\":2"),
    fixture.Replace("\"id\":\"work\"", "\"id\":\"standard\""), fixture.Replace("\"device_ids\":null", "\"device_ids\":[]"),
    fixture.Replace("\"name\":\"Standard\"", "\"name\":\"Edited\""), fixture.Replace("\"device_ids\":[\"mac\"]", "\"device_ids\":null") }) Reject(() => Decode(bad));
var legacy = JsonSerializer.Deserialize<AvailabilitySnapshot>(fixture, json)!;
legacy.Settings.DeviceProfiles = null; legacy.Settings.ActiveProfileId = null; legacy.DeviceProfilesVersion = null;
Check(legacy.Validate("alpha","owner").Settings.ActiveProfile.IsStandard, "Legacy read defaults to Standard");
var empty = DeviceProfileChanges.Upsert(current, new() { Id = "quiet", Name = " Ruhe ", DeviceIds = [] }, true);
var emptySnapshot = empty.Apply(current).Validate("alpha","owner");
Check(emptySnapshot.Settings.Profiles.Single(p => p.Id == "quiet").DeviceIds!.Length == 0 && emptySnapshot.Settings.ActiveProfile.Id == "work", "Empty profiles are permitted and creation preserves the active profile");
Check(DeviceProfileChanges.Upsert(current, new() { Id = "work", Name = "Mobil", DeviceIds = ["phone"] }, false).Apply(current).Settings.ActiveProfile.Name == "Mobil", "Rename preserves identity and allows disabled own devices");
Reject(() => DeviceProfileChanges.Upsert(current, new() { Id = "x", Name = "Foreign", DeviceIds = ["foreign"] }, true));
Reject(() => DeviceProfileChanges.Upsert(current, new() { Id = "work", Name = "Duplicate", DeviceIds = [] }, true));
Reject(() => DeviceProfileChanges.Upsert(current, new() { Id = "missing", Name = "Gone", DeviceIds = [] }, false));
Reject(() => DeviceProfileChanges.Upsert(current, DeviceProfile.Standard(), false));
Reject(() => DeviceProfileChanges.Delete(current, "standard"));
Reject(() => DeviceProfileChanges.Select(current, "missing"));
Reject(() => DeviceProfileChanges.Upsert(current, new() { Id = "long", Name = new string('a',51), DeviceIds = [] }, true));
var deleted = DeviceProfileChanges.Delete(current, "work").Apply(current).Validate("alpha","owner");
Check(deleted.Settings.ActiveProfile.IsStandard && current.Settings.Profiles.Length == 2, "Deleting active profile falls back to Standard without mutating source");
var full = empty.Apply(current);
full.Settings.DeviceProfiles = Enumerable.Range(1,19).Select(i => new DeviceProfile { Id = "p"+i, Name = "P"+i, DeviceIds = [] }).Prepend(DeviceProfile.Standard()).ToArray();
full.Settings.ActiveProfileId = "standard";
Reject(() => DeviceProfileChanges.Upsert(full, new() { Id = "last", Name = "last", DeviceIds = [] }, true));

using var handler = new ProfilesHandler(current, json);
using var account = new AccountClient(handler);
await account.PasswordAsync("owner@example.invalid", "test-only", CancellationToken.None);
var next = await account.SaveDeviceProfilesAsync(current, empty, CancellationToken.None);
Check(next.Revision == 8 && next.Settings.Profiles.Length == 3, "Save must reread confirmed server state");
using (var request = JsonDocument.Parse(handler.LastWrite!))
{
    var root = request.RootElement; var changes = root.GetProperty("changes");
    Check(root.GetProperty("tenant_id").GetString() == "alpha" && root.GetProperty("expected_revision").GetInt32() == 7, "Scoped optimistic revision required");
    Check(!changes.TryGetProperty("work_mode", out _) && !changes.TryGetProperty("presence", out _), "Profile writes must preserve status and legacy work modes");
    Check(changes.GetProperty("device_profiles")[0].GetProperty("device_ids").ValueKind == JsonValueKind.Null, "Write preserves dynamic Standard");
}
handler.Conflict = true; var writes = handler.Writes;
try { await account.SaveDeviceProfilesAsync(next, DeviceProfileChanges.Select(next,"standard"), CancellationToken.None); throw new Exception("Conflict accepted"); }
catch (AccountException ex) when (ex.StatusCode == 409) { }
Check(handler.Writes == writes + 1, "Conflicts must never retry with a new revision");
handler.Conflict = false; handler.Unconfirmed = true;
try { await account.SaveDeviceProfilesAsync(next, DeviceProfileChanges.Select(next,"standard"), CancellationToken.None); throw new Exception("Unconfirmed save accepted"); }
catch (AccountException) { }
handler.Unconfirmed = false; handler.PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
var pending = account.GetAvailabilityAsync("alpha","owner",CancellationToken.None);
account.SignOut(); handler.PendingRead.SetResult(handler.Json(handler.State));
try { await pending; throw new Exception("Old account read accepted"); } catch (OperationCanceledException) { }
Console.WriteLine("PASS: device profile contract, dynamic Standard, own-device scope, CRUD, capacity, confirmed writes, revision conflicts and sign-out race");

sealed class ProfilesHandler(AvailabilitySnapshot initial, JsonSerializerOptions json) : HttpMessageHandler
{
    public AvailabilitySnapshot State = initial;
    public string? LastWrite;
    public int Writes;
    public bool Conflict, Unconfirmed;
    public TaskCompletionSource<HttpResponseMessage>? PendingRead;
    public HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value,json), Encoding.UTF8,"application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("account/password")) return Json(new { token = new string('a',43) });
        if (request.Headers.Authorization?.Scheme != "Bearer") throw new Exception("Authentication missing");
        if (request.RequestUri.AbsolutePath.EndsWith("availability/read")) return PendingRead is null ? Json(State) : await PendingRead.Task;
        if (!request.RequestUri.AbsolutePath.EndsWith("availability/user")) throw new Exception("Unexpected endpoint");
        Writes++; LastWrite = await request.Content!.ReadAsStringAsync(ct);
        if (Conflict) return new(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(LastWrite);
        var changes = body.RootElement.GetProperty("changes").Deserialize<DeviceProfileChanges>(json)!;
        var revision = State.Revision + 1;
        if (!Unconfirmed) { State = changes.Apply(State); State.Revision = revision; }
        return Json(new { revision });
    }
}
