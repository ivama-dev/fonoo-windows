using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using System.Text.Json;

namespace Fonoo.Windows.Microsoft365;

public sealed class MicrosoftConnectionException(string message, bool signInRequired = false) : Exception(message)
{
    public bool SignInRequired { get; } = signInRequired;
}

public sealed record MicrosoftSession(string AccountId, string Username, string AccessToken, string TenantId = "")
{
    public override string ToString() => "Microsoft session (protected)";
}

public sealed class MicrosoftOptions
{
    public string ClientId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public bool Configured => Guid.TryParse(ClientId, out var id) && id != Guid.Empty;
    public static MicrosoftOptions Load(string path)
    {
        try { return JsonSerializer.Deserialize<MicrosoftOptions>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
        catch { return new(); }
    }
}

public interface IMicrosoftIdentity : IDisposable
{
    Task<MicrosoftSession> ConnectAsync(CancellationToken ct);
    Task<MicrosoftSession> AcquireSilentAsync(string accountId, bool forceRefresh, CancellationToken ct);
    Task RemoveAsync(string accountId);
}

// MSAL validates OAuth/OIDC responses and owns PKCE, token renewal and WAM.
// The only Graph permission is read-only access to the user's mailbox contacts.
public sealed class MicrosoftIdentity : IMicrosoftIdentity
{
    public static readonly string[] Scopes = ["https://graph.microsoft.com/Contacts.Read"];
    private readonly IPublicClientApplication app;
    private readonly string directory;
    private readonly SemaphoreSlim cacheGate = new(1, 1);
    private readonly object cacheLifecycle = new();
    private bool disposed;
    private MsalCacheHelper? cache;
    public MicrosoftIdentity(MicrosoftOptions options, string directory, IntPtr parentWindow)
    {
        if (!options.Configured) throw new MicrosoftConnectionException("Die Microsoft-Verbindung ist noch nicht freigeschaltet.");
        this.directory = directory;
        var builder = PublicClientApplicationBuilder.Create(options.ClientId);
        if (Guid.TryParse(options.TenantId, out var tenant) && tenant != Guid.Empty)
            builder = builder.WithAuthority(AzureCloudInstance.AzurePublic, tenant.ToString());
        else builder = builder.WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount);
        app = builder
            .WithRedirectUri("http://localhost")
            .WithParentActivityOrWindow(() => parentWindow)
            .WithLegacyCacheCompatibility(false)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Fonoo · Outlook-Kontakte" })
            .Build();
    }
    private async Task InitializeAsync(CancellationToken ct)
    {
        await cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (cache is not null) return;
            Directory.CreateDirectory(directory);
            var helper = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("tokens.protected", directory).Build()).ConfigureAwait(false);
            helper.VerifyPersistence(); // Windows DPAPI; no unprotected-file fallback.
            lock (cacheLifecycle)
            {
                ct.ThrowIfCancellationRequested();
                if (disposed) throw new OperationCanceledException(ct);
                helper.RegisterCache(app.UserTokenCache); cache = helper;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new MicrosoftConnectionException("Windows konnte die Microsoft-Anmeldung nicht geschützt speichern. Bitte versuche es erneut."); }
        finally { cacheGate.Release(); }
    }
    public async Task<MicrosoftSession> ConnectAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        try
        {
            // Account selection and consent are always initiated by a user click.
            var result = await app.AcquireTokenInteractive(Scopes).WithPrompt(Prompt.SelectAccount).WithUseEmbeddedWebView(false).ExecuteAsync(ct);
            return Session(result);
        }
        catch (MsalClientException ex) when (ex.ErrorCode == "authentication_canceled") { throw new OperationCanceledException(ct); }
        catch (MsalException) { throw new MicrosoftConnectionException("Die Microsoft-Anmeldung konnte nicht abgeschlossen werden. Prüfe gegebenenfalls die Freigabe durch deine Firma.", true); }
    }
    public async Task<MicrosoftSession> AcquireSilentAsync(string accountId, bool forceRefresh, CancellationToken ct)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        try
        {
            var selected = await app.GetAccountAsync(accountId).ConfigureAwait(false);
            if (selected is null) throw new MicrosoftConnectionException("Bitte verbinde dein Microsoft-Konto erneut.", true);
            return Session(await app.AcquireTokenSilent(Scopes, selected).WithForceRefresh(forceRefresh).ExecuteAsync(ct).ConfigureAwait(false));
        }
        catch (MsalUiRequiredException) { throw new MicrosoftConnectionException("Microsoft benötigt eine erneute Anmeldung oder Freigabe. Wähle „Erneut anmelden“.", true); }
        catch (MsalException) { throw new MicrosoftConnectionException("Microsoft ist gerade nicht erreichbar. Bereits geladene Kontakte bleiben verfügbar."); }
    }
    private static MicrosoftSession Session(AuthenticationResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Account?.HomeAccountId?.Identifier) || string.IsNullOrWhiteSpace(result.AccessToken))
            throw new MicrosoftConnectionException("Microsoft hat keine gültige Anmeldung bestätigt.", true);
        return new(result.Account.HomeAccountId.Identifier, result.Account.Username ?? "Microsoft-Konto", result.AccessToken, result.TenantId ?? "");
    }
    public async Task RemoveAsync(string accountId)
    {
        // Remove this app's account/cache, never the Windows account itself.
        if (cache is null && !File.Exists(Path.Combine(directory, "tokens.protected"))) return;
        await InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var selected in await app.GetAccountsAsync().ConfigureAwait(false)) await app.RemoveAsync(selected).ConfigureAwait(false);
        var path = Path.Combine(directory, "tokens.protected");
        if (File.Exists(path)) File.Delete(path);
    }
    public void Dispose() { lock (cacheLifecycle) { disposed = true; cache?.UnregisterCache(app.UserTokenCache); } }
}
