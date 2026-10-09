using Fonoo.Windows.Accounts;
using System.Net;
using System.Text.Json;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Reject(Func<Task> work, string message)
{
    try { await work(); } catch (AccountException) { return; }
    throw new Exception(message);
}
var configuration = """
{"status":"ready","tenant_id":"company-1","device_id":"windows-device","schema_version":1,"backend":"asterisk","revision":2,"configuration":{"server":"sip.example.test","domain":"sip.example.test","username":"endpoint-1","authentication_name":"endpoint-1","password":"test-only","transport":"TLS","port":5061,"media_encryption":"SRTP","media_encryption_mandatory":true}}
""";
var handler = new QueueHandler();
using var client = new AccountClient(handler);
handler.Add("account/code", "{\"challenge\":\"email-challenge\"}", request =>
{
    Check(request.Headers.Authorization is null, "Code request must not send a bearer token");
    Check(request.Method == HttpMethod.Post, "Code request must be POST");
});
Check(await client.SendCodeAsync("user@example.test", default) == "email-challenge", "Challenge missing");
handler.Add("account/verify", "{\"token\":\"test-session\"}");
await client.VerifyAsync("email-challenge", "123456", default);
Check(client.SignedIn, "Verify must establish session");
handler.Add("account/me", "{\"id\":\"user-1\",\"email\":\"user@example.test\"}", request =>
    Check(request.Headers.Authorization?.ToString() == "Bearer test-session", "Authenticated request missing token"));
Check((await client.GetAccountAsync(default)).Id == "user-1", "Account failed");
handler.Add("cloud/me", "{\"tenants\":[{\"id\":\"company-1\",\"name\":\"Firma\",\"extension\":\"101\",\"trial\":{\"expired\":false}}]}");
var membership = (await client.GetMembershipsAsync(default)).Single();
Check(membership.CanProvision && membership.Extension == "101", "Membership decode failed");
handler.Add("cloud/device-configuration", configuration, request =>
{
    var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
    using var data = JsonDocument.Parse(body);
    Check(data.RootElement.GetProperty("device_id").GetString() == "windows-device", "Wrong device");
    Check(data.RootElement.GetProperty("sip_engine").GetString() == "liblinphone", "Windows must explicitly request its fixed SDK");
    Check(data.RootElement.GetProperty("capabilities").GetArrayLength() == 2, "Capabilities missing");
    Check(!body.Contains("push_token"), "Windows must not enroll iOS push");
});
var sip = await client.ProvisionAsync(membership, "windows-device", default);
Check(sip.Password == "test-only", "Provisioned credentials missing");
sip.ClearSecrets();
Check(sip.Password.Length == 0, "Secret reference not cleared");
foreach (var bad in new[] {
    configuration.Replace("company-1", "other-company"),
    configuration.Replace("windows-device", "other-device"),
    configuration.Replace("\"TLS\"", "\"UDP\""),
    configuration.Replace("\"schema_version\":1", "\"schema_version\":2"),
    configuration.Replace("\"configuration\":", "\"sip_engine\":{\"engine\":\"unsupported\"},\"configuration\":"),
    configuration.Replace("\"media_encryption_mandatory\":true", "\"media_encryption_mandatory\":false") })
{
    handler.Add("cloud/device-configuration", bad);
    await Reject(() => client.ProvisionAsync(membership, "windows-device", default), "Unsafe configuration accepted");
}
await Reject(() => client.ProvisionAsync(new Membership { Id = "none" }, "windows-device", default), "Unassigned membership accepted");
handler.Add("account/me", "{}", status: HttpStatusCode.Unauthorized);
await Reject(() => client.GetAccountAsync(default), "401 accepted");
Check(!client.SignedIn, "401 must clear session");
handler.Add("account/password", "{\"token\":\"password-session\"}");
await client.PasswordAsync("user@example.test", "test-only", default);
Check(client.SignedIn, "Password login failed");
client.SignOut();
Check(!client.SignedIn, "Sign out failed");
handler.Add("account/verify", "{\"token\":\"\"}");
await Reject(() => client.VerifyAsync("c", "123456", default), "Empty token accepted");
Check(!client.SignedIn, "Invalid token established session");
handler.Add("account/code", "{}", status: HttpStatusCode.Forbidden);
try
{
    await client.SendCodeAsync("user@example.test", default);
    throw new Exception("Password-only account accepted code request");
}
catch (AccountException ex)
{
    Check(ex.Message.Contains("Mit Passwort anmelden"), "Password-only account needs actionable guidance");
}
Check(handler.Remaining == 0, "Not all responses consumed");
Console.WriteLine("PASS: email/password authentication, explicit liblinphone provisioning, device/tenant isolation, TLS/SRTP validation, session expiry and logout.");

sealed class QueueHandler : HttpMessageHandler
{
    private readonly Queue<(string Path, string Json, Action<HttpRequestMessage>? Check, HttpStatusCode Status)> queue = new();
    public int Remaining => queue.Count;
    public void Add(string path, string json, Action<HttpRequestMessage>? check = null, HttpStatusCode status = HttpStatusCode.OK)
        => queue.Enqueue((path, json, check, status));
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method == HttpMethod.Post)
        {
            // The Python account service rejects POST bodies without Content-Length.
            var length = request.Content?.Headers.ContentLength;
            if (length is null or <= 0) throw new Exception("POST must declare Content-Length before serialization");
            var bytes = request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            if (length != bytes.Length) throw new Exception("Content-Length must match UTF-8 byte length");
            if (request.Content.Headers.ContentType?.MediaType != "application/json") throw new Exception("JSON content type required");
        }
        var next = queue.Dequeue();
        if (request.RequestUri!.AbsolutePath != "/v1/" + next.Path) throw new Exception("Wrong endpoint");
        next.Check?.Invoke(request);
        return Task.FromResult(new HttpResponseMessage(next.Status) { Content = new StringContent(next.Json) });
    }
}
