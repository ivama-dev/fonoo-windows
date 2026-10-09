using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fonoo.Windows.Accounts;

public sealed class AccountException(string message, bool sessionExpired = false, int? statusCode = null) : Exception(message)
{
    public bool SessionExpired { get; } = sessionExpired;
    public int? StatusCode { get; } = statusCode;
}

public sealed class Membership
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonPropertyName("extension")] public string? Extension { get; set; }
    public Trial? Trial { get; set; }
    [JsonIgnore] public string DisplayName => $"{Name} · Nebenstelle {Extension ?? "nicht zugewiesen"}";
    [JsonIgnore] public bool CanProvision => !string.IsNullOrWhiteSpace(Extension) && Trial is { Expired: false };
}
public sealed class Trial { public bool Expired { get; set; } }
public sealed class MicrosoftCompanyConnection
{
    public bool Configured { get; set; }
    public bool Connected { get; set; }
    public string CompanyId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? MicrosoftTenantId { get; set; }
    public int Revision { get; set; }
    public bool CanManage { get; set; }
    public bool ValidFor(string company, string client) => CompanyId == company && ClientId == client && Configured && Connected && Revision > 0 &&
        Guid.TryParse(MicrosoftTenantId, out var tenant) && tenant != Guid.Empty && tenant.ToString() != "9188040d-6c67-4c5b-b112-36a304b66dad";
}
public sealed class AccountSummary
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public bool? PasswordConfigured { get; set; }
    public bool? PasswordManaged { get; set; }
}
public sealed class SipConfiguration
{
    public string Server { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Username { get; set; } = "";
    public string AuthenticationName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Transport { get; set; } = "";
    public int Port { get; set; }
    public string MediaEncryption { get; set; } = "";
    public bool MediaEncryptionMandatory { get; set; }
    public string TurnPassword { get; set; } = "";
    public bool IceEnabled { get; set; }
    public string StunServer { get; set; } = "";
    public int StunPort { get; set; }
    public bool TurnEnabled { get; set; }
    public string TurnServer { get; set; } = "";
    public int TurnPort { get; set; }
    public string TurnUsername { get; set; } = "";
    public string TurnTransport { get; set; } = "";
    public bool ForceTurn { get; set; }
    public void ClearSecrets() { Password = ""; TurnPassword = ""; }
}
public sealed class DeviceConfiguration
{
    public string Status { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string Backend { get; set; } = "";
    public int Revision { get; set; }
    public SipConfiguration? Configuration { get; set; }
    public DeviceSipEngine? SipEngine { get; set; }

    public SipConfiguration? Validate(string tenantId, string deviceId)
    {
        if (TenantId != tenantId || DeviceId != deviceId)
            throw new AccountException("Die Konfiguration gehört nicht zu deiner Firma oder diesem Windows-Gerät.");
        if (Status == "provisioning") return null;
        var c = Configuration;
        if (Status != "ready" || SchemaVersion != 1 || Backend != "asterisk" || Revision <= 0 || c is null ||
            (SipEngine is not null && SipEngine.Engine != "liblinphone") ||
            c.Transport != "TLS" || c.Port != 5061 || c.Server != c.Domain ||
            Uri.CheckHostName(c.Server) == UriHostNameType.Unknown ||
            c.MediaEncryption != "SRTP" || !c.MediaEncryptionMandatory ||
            string.IsNullOrWhiteSpace(c.Username) || string.IsNullOrWhiteSpace(c.AuthenticationName) || string.IsNullOrEmpty(c.Password))
            throw new AccountException("Die Telefonie-Konfiguration konnte nicht sicher übernommen werden.");
        if ((c.ForceTurn && !c.TurnEnabled) ||
            (c.TurnEnabled && (!c.IceEnabled || Uri.CheckHostName(c.TurnServer) == UriHostNameType.Unknown ||
                c.TurnPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(c.TurnUsername) || string.IsNullOrEmpty(c.TurnPassword) ||
                c.TurnTransport is not ("UDP" or "TCP" or "TLS"))) ||
            (c.IceEnabled && !c.TurnEnabled && c.StunServer.Length > 0 &&
                (Uri.CheckHostName(c.StunServer) == UriHostNameType.Unknown || c.StunPort is < 1 or > 65535)))
            throw new AccountException("Die Audio-Netzwerkkonfiguration ist ungültig.");
        return c;
    }
}
public sealed class DeviceSipEngine { public string Engine { get; set; } = ""; }

public sealed partial class AccountClient : IDisposable
{
    private static readonly Uri Endpoint = new("https://push.dev.fonoo.app/v1/");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private readonly HttpClient http;
    private string token = "";
    public bool SignedIn => token.Length > 0;

    public AccountClient() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }
    public AccountClient(HttpMessageHandler handler) => http = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
    public void SignOut() { loginGeneration++; token = ""; remembered = null; sessionStore?.Clear(); }
    private sealed class TokenResponse { public string Token { get; set; } = ""; }
    private sealed class ChallengeResponse { public string Challenge { get; set; } = ""; }
    private sealed class CloudSummary { public Membership[] Tenants { get; set; } = []; }

    public async Task<string> SendCodeAsync(string email, CancellationToken ct)
    {
        var result = await RequestAsync<ChallengeResponse>("account/code", new { email }, ct, false);
        if (string.IsNullOrWhiteSpace(result.Challenge)) throw new AccountException("Der Kontodienst hat keinen Bestätigungsvorgang zurückgegeben.");
        return result.Challenge;
    }
    public async Task VerifyAsync(string challenge, string code, CancellationToken ct)
        => SetTemporaryToken((await RequestAsync<TokenResponse>("account/verify", new { challenge, code }, ct, false)).Token);
    public async Task PasswordAsync(string email, string password, CancellationToken ct)
        => SetTemporaryToken((await RequestAsync<TokenResponse>("account/password", new { email, password }, ct, false)).Token);
    private void SetTemporaryToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new AccountException("Der Kontodienst hat keine gültige Sitzung zurückgegeben.");
        sessionStore?.Clear(); remembered = null; SetToken(value);
    }
    private void SetToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new AccountException("Der Kontodienst hat keine gültige Sitzung zurückgegeben.");
        token = value;
        loginGeneration++;
    }
    public async Task<AccountSummary> GetAccountAsync(CancellationToken ct)
    {
        var summary = await RequestAsync<AccountSummary>("account/me", null, ct);
        if (string.IsNullOrWhiteSpace(summary.Id) || string.IsNullOrWhiteSpace(summary.Email) || (remembered is not null && summary.Id != remembered.UserId))
            throw new AccountException("Die Kontoantwort konnte nicht sicher übernommen werden.");
        return summary;
    }
    public async Task<Membership[]> GetMembershipsAsync(CancellationToken ct)
        => (await RequestAsync<CloudSummary>("cloud/me", null, ct)).Tenants ?? [];
    public Task<MicrosoftCompanyConnection> GetMicrosoftCompanyAsync(string company, CancellationToken ct)
        => RequestAsync<MicrosoftCompanyConnection>("cloud/microsoft365/status", new { tenant_id = company }, ct);
    public async Task<SipConfiguration> ProvisionAsync(Membership membership, string deviceId, CancellationToken ct)
    {
        if (!membership.CanProvision) throw new AccountException("Für diese Firma werden eine zugewiesene Nebenstelle und ein aktiver Telefoniezugang benötigt.");
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var result = await RequestAsync<DeviceConfiguration>("cloud/device-configuration", new
            {
                tenant_id = membership.Id, device_id = deviceId, sip_engine = "liblinphone",
                capabilities = new[] { "fonoo.cloud.v1", "srtp.required" }
            }, ct);
            if (result.Validate(membership.Id, deviceId) is { } configuration) return configuration;
            if (attempt < 9) await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        throw new AccountException("Deine Nebenstelle wird noch eingerichtet. Bitte versuche es gleich erneut.");
    }
    private async Task<T> RequestAsync<T>(string path, object? body, CancellationToken ct, bool authenticated = true)
    {
        var generation = loginGeneration;
        var requestToken = token;
        try { return await SendRequestAsync<T>(path, body, ct, authenticated, requestToken); }
        catch (AccountException ex) when (ex.SessionExpired && authenticated && generation == loginGeneration)
        {
            if (!CanRenew) { SignOut(); throw; }
            await RenewAsync(ct, requestToken);
            try { return await SendRequestAsync<T>(path, body, ct, authenticated); }
            catch (AccountException retry) when (retry.SessionExpired && generation == loginGeneration) { SignOut(); throw; }
        }
    }
    private async Task<T> SendRequestAsync<T>(string path, object? body, CancellationToken ct, bool authenticated, string? bearer = null)
    {
        var generation = loginGeneration;
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(Endpoint, path));
        if (authenticated)
        {
            if (!SignedIn) throw new AccountException("Bitte melde dich erneut an.", true);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? token);
        }
        if (body is not null)
        {
            // The account service requires Content-Length and does not accept chunked JSON.
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        using var response = await http.SendAsync(request, ct);
        if (generation != loginGeneration) throw new OperationCanceledException();
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized && (authenticated || path == "account/device/refresh"))
            {
                throw new AccountException("Deine Sitzung ist abgelaufen. Bitte melde dich erneut an.", true);
            }
            throw new AccountException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "E-Mail, Passwort oder Bestätigungscode stimmen nicht. Bitte prüfe deine Eingabe.",
                HttpStatusCode.Forbidden when path == "account/code" => "Dieses Konto benötigt ein Passwort. Bitte verwende „Mit Passwort anmelden“. Dein Administrator hilft dir bei fehlenden Zugangsdaten.",
                HttpStatusCode.Forbidden => "Für diesen Zugang fehlt die Berechtigung. Bitte kontaktiere deinen Administrator.",
                HttpStatusCode.TooManyRequests => "Zu viele Versuche. Bitte warte einige Minuten.",
                HttpStatusCode.Conflict when path.StartsWith("cloud/availability/") || path == "cloud/team/name" => "Die Angaben wurden inzwischen geändert. Bitte aktualisieren und erneut speichern.",
                HttpStatusCode.Conflict => "Deine Nebenstelle ist noch nicht bereit. Bitte versuche es später erneut.",
                HttpStatusCode.BadRequest => "Bitte prüfe deine Eingabe. Fordere gegebenenfalls einen neuen Code an.",
                HttpStatusCode.NotFound => "Diese Funktion ist im Kontodienst noch nicht verfügbar.",
                _ => "Der Kontodienst ist momentan nicht erreichbar. Bitte versuche es erneut."
            }, statusCode: (int)response.StatusCode);
        }
        try
        {
            var result = await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new JsonException();
            ct.ThrowIfCancellationRequested();
            if (generation != loginGeneration) throw new OperationCanceledException();
            return result;
        }
        catch (JsonException) { throw new AccountException("Die Antwort des Kontodienstes konnte nicht gelesen werden."); }
    }
    public void Dispose() { loginGeneration++; token = ""; remembered = null; http.Dispose(); }
}
