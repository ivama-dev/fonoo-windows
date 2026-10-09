using Fonoo.Windows.Microsoft365;
using Fonoo.Windows.Accounts;
using Fonoo.Windows.Telephony;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private MicrosoftOptions microsoftOptions = new();
    private MicrosoftContactConnection? microsoftContacts;
    private Task microsoftShutdown = Task.CompletedTask;
    private CancellationTokenSource? microsoftOperation;
    private string microsoftOwner = "";
    private string microsoftCompany = "";
    private MicrosoftCompanyConnection? microsoftPolicy;
    private CancellationTokenSource? microsoftPolicyOperation;
    private string microsoftMessage = "";
    private ContactRow[] outlookContacts = [];
    private void InitializeMicrosoftContacts()
    {
        microsoftOptions = MicrosoftOptions.Load(Path.Combine(AppContext.BaseDirectory, "microsoft365.json"));
        UpdateMicrosoftContactsUi();
        Closed += (_, _) => { microsoftOperation?.Cancel(); microsoftPolicyOperation?.Cancel(); microsoftContacts?.Dispose(); };
    }
    private async Task RestoreMicrosoftContactsAsync(string owner)
    {
        if (previewMode || !microsoftOptions.Configured || string.IsNullOrWhiteSpace(owner)) return;
        var company = activeTenant;
        if (company.Length == 0) return;
        await microsoftShutdown;
        if (closed || accountId != owner || activeTenant != company) return;
        if (microsoftCompany.Length > 0 && microsoftCompany != company)
        { microsoftShutdown = ForgetMicrosoftContactsAsync(); await microsoftShutdown; }
        microsoftPolicyOperation?.Cancel();
        using var policyRequest = new CancellationTokenSource(); microsoftPolicyOperation = policyRequest;
        UpdateMicrosoftContactsUi();
        MicrosoftCompanyConnection policy;
        try
        {
            policy = await account.GetMicrosoftCompanyAsync(company, policyRequest.Token);
            if (closed || accountId != owner || activeTenant != company || policyRequest.IsCancellationRequested) return;
            if (!policy.ValidFor(company, microsoftOptions.ClientId))
            {
                await ForgetMicrosoftContactsAsync();
                microsoftMessage = policy.Configured ? "Bitte lass Microsoft 365 zuerst im Kundenbereich von eurem Firmenadministrator verbinden." : "Die Microsoft-Verbindung deiner Firma wird noch eingerichtet.";
                UpdateMicrosoftContactsUi(); return;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (AccountException ex) when (ex.SessionExpired) { if (!closed && accountId == owner) ResetSession(); return; }
        catch (AccountException ex) when (ex.StatusCode is 403 or 404)
        {
            if (!closed && accountId == owner && activeTenant == company && !policyRequest.IsCancellationRequested)
            {
                await ForgetMicrosoftContactsAsync(); microsoftMessage = "Die Firmenverbindung ist noch nicht verfügbar. Bitte kontaktiere euren Administrator.";
            }
            UpdateMicrosoftContactsUi(); return;
        }
        catch
        {
            if (!closed && accountId == owner && activeTenant == company && !policyRequest.IsCancellationRequested)
                microsoftMessage = "Die Firmenfreigabe konnte nicht geladen werden. Bitte aktualisiere dein Fonoo-Konto.";
            UpdateMicrosoftContactsUi(); return;
        }
        finally { if (ReferenceEquals(microsoftPolicyOperation, policyRequest)) microsoftPolicyOperation = null; UpdateMicrosoftContactsUi(); }
        if (microsoftOwner == owner && microsoftCompany == company && microsoftPolicy?.Revision == policy.Revision && microsoftPolicy?.MicrosoftTenantId == policy.MicrosoftTenantId && microsoftContacts is not null)
        { microsoftMessage = ""; UpdateMicrosoftContactsUi(); return; }
        if (microsoftContacts is not null) { microsoftShutdown = ForgetMicrosoftContactsAsync(); await microsoftShutdown; }
        if (closed || accountId != owner || activeTenant != company) return;
        outlookContacts = []; RenderContacts(); RefreshPeerDisplay();
        microsoftOwner = owner; microsoftCompany = company; microsoftPolicy = policy; microsoftMessage = "";
        var directory = ProtectedMicrosoftContactStore.ScopedDirectory(UserDataDirectory, owner + "\n" + company + "\n" + policy.MicrosoftTenantId + "\n" + policy.Revision, microsoftOptions.ClientId);
        MicrosoftContactConnection current;
        try { current = new MicrosoftContactConnection(new MicrosoftIdentity(new MicrosoftOptions { ClientId = microsoftOptions.ClientId, TenantId = policy.MicrosoftTenantId! }, directory, WinRT.Interop.WindowNative.GetWindowHandle(this)), new GraphContactsReader(),
            new ProtectedMicrosoftContactStore(directory, owner, microsoftOptions.ClientId), owner, microsoftOptions.ClientId, policy.MicrosoftTenantId!); }
        catch { microsoftMessage = "Die Microsoft-Anmeldung ist auf diesem PC gerade nicht verfügbar."; UpdateMicrosoftContactsUi(); return; }
        microsoftContacts = current;
        await MicrosoftOperationAsync(async ct =>
        {
            await current.RestoreAsync(ct);
            if (!ReferenceEquals(current, microsoftContacts) || closed) return;
            ApplyMicrosoftContacts(current);
            if (current.Snapshot is not null) await current.RefreshAsync(ct); // Silent only: no startup authentication pop-up.
        });
    }
    private void ApplyMicrosoftContacts(MicrosoftContactConnection current)
    {
        if (closed || !ReferenceEquals(current, microsoftContacts) || microsoftOwner != accountId || microsoftCompany != activeTenant) return;
        outlookContacts = current.Snapshot?.Contacts ?? [];
        RenderContacts(); UpdateMicrosoftContactsUi(); RefreshPeerDisplay();
    }
    private async Task MicrosoftOperationAsync(Func<CancellationToken, Task> action)
    {
        if (microsoftOperation is not null || closed || previewMode) return;
        using var pending = new CancellationTokenSource(); microsoftOperation = pending;
        var current = microsoftContacts; microsoftMessage = ""; UpdateMicrosoftContactsUi();
        try { await action(pending.Token); }
        catch (OperationCanceledException) { if (!pending.IsCancellationRequested && !closed && ReferenceEquals(current, microsoftContacts)) microsoftMessage = "Microsoft-Anmeldung abgebrochen."; }
        catch (AccountException ex) when (ex.SessionExpired) { if (!closed && ReferenceEquals(current, microsoftContacts)) ResetSession(); }
        catch (Exception ex)
        {
            if (!closed && !pending.IsCancellationRequested && ReferenceEquals(current, microsoftContacts)) microsoftMessage = ex switch
            {
                MicrosoftConnectionException => ex.Message,
                HttpRequestException => "Keine Verbindung zu Microsoft. Bereits geladene Kontakte bleiben verfügbar.",
                _ => "Outlook-Kontakte konnten nicht aktualisiert werden. Bitte erneut versuchen."
            };
        }
        finally
        {
            if (ReferenceEquals(microsoftOperation, pending)) microsoftOperation = null;
            if (!closed && ReferenceEquals(current, microsoftContacts)) { if (current is not null) ApplyMicrosoftContacts(current); UpdateMicrosoftContactsUi(); }
        }
    }
    private void UpdateMicrosoftContactsUi()
    {
        if (OutlookStatus is null) return;
        var saved = microsoftContacts?.Snapshot;
        OutlookAccount.Text = saved?.Username ?? "Microsoft 365 deiner Firma";
        OutlookStatus.Text = microsoftMessage.Length > 0 ? microsoftMessage : microsoftOperation is not null ? "Outlook-Kontakte werden geladen …" :
            !microsoftOptions.Configured ? "Die Microsoft-Verbindung ist noch nicht freigeschaltet." : !previewMode && !account.SignedIn ? "Melde dich zuerst bei Fonoo an." : !previewMode && activeTenant.Length == 0 ? "Wähle zuerst deine Firma in Fonoo aus." : microsoftPolicyOperation is not null ? "Die Microsoft-Verbindung deiner Firma wird geprüft …" : saved is null ? "Verbinde dein Firmenkonto, um deine Outlook-Kontakte in Fonoo zu verwenden." :
            saved.SyncedAt is { } time ? $"{saved.Contacts.Length} Rufnummern · Stand {time.ToLocalTime():dd.MM. HH:mm} · auf diesem PC gespeichert" : "Bitte melde dich erneut bei Microsoft an.";
        var canAct = microsoftOptions.Configured && account.SignedIn && microsoftOwner == accountId && microsoftCompany == activeTenant && microsoftContacts is not null && microsoftOperation is null && microsoftPolicyOperation is null && !previewMode;
        ConnectOutlookButton.IsEnabled = canAct && !snapshot.InCall;
        ConnectOutlookButton.Content = microsoftContacts?.SignInRequired == true ? "Erneut anmelden" : saved is null ? "Outlook verbinden" : "Konto wechseln";
        RefreshOutlookButton.Visibility = DisconnectOutlookButton.Visibility = saved is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshOutlookButton.IsEnabled = canAct && microsoftContacts?.SignInRequired != true;
        DisconnectOutlookButton.IsEnabled = saved is not null;
        MicrosoftPortalButton.IsEnabled = account.SignedIn && !snapshot.InCall && !previewMode;
        OutlookProgress.IsActive = microsoftOperation is not null || microsoftPolicyOperation is not null; OutlookProgress.Visibility = OutlookProgress.IsActive ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void ConnectOutlookClicked(object sender, RoutedEventArgs e)
    {
        if (modalOpen || audioDialogOpen || operation is not null || snapshot.InCall || microsoftContacts is not { } current) return;
        modalOpen = true;
        try { await MicrosoftOperationAsync(async ct => { await ValidateMicrosoftPolicyAsync(ct); await current.ConnectAsync(ct); }); }
        finally { modalOpen = false; }
    }
    private async void RefreshOutlookClicked(object sender, RoutedEventArgs e)
    {
        if (microsoftContacts is { } current) await MicrosoftOperationAsync(async ct => { await ValidateMicrosoftPolicyAsync(ct); await current.RefreshAsync(ct); });
    }
    private async Task ValidateMicrosoftPolicyAsync(CancellationToken ct)
    {
        var company = microsoftCompany; var owner = accountId;
        MicrosoftCompanyConnection next;
        try { next = await account.GetMicrosoftCompanyAsync(company, ct); }
        catch (AccountException ex) when (ex.StatusCode is 403 or 404)
        {
            await ForgetMicrosoftContactsAsync(); microsoftMessage = "Die Firmenverbindung ist nicht mehr verfügbar. Bitte kontaktiere euren Administrator.";
            UpdateMicrosoftContactsUi(); throw new OperationCanceledException(ct);
        }
        ct.ThrowIfCancellationRequested();
        if (owner != accountId || company != activeTenant) throw new OperationCanceledException(ct);
        if (!next.ValidFor(company, microsoftOptions.ClientId) || next.Revision != microsoftPolicy?.Revision || next.MicrosoftTenantId != microsoftPolicy?.MicrosoftTenantId)
        {
            await ForgetMicrosoftContactsAsync();
            microsoftMessage = "Die Firmenverbindung wurde geändert oder getrennt. Bitte aktualisiere dein Fonoo-Konto.";
            UpdateMicrosoftContactsUi(); throw new OperationCanceledException(ct);
        }
    }
    private async void MicrosoftPortalClicked(object sender, RoutedEventArgs e)
    {
        if (!snapshot.InCall) await global::Windows.System.Launcher.LaunchUriAsync(new Uri("https://dev.fonoo.app/kunden/"));
    }
    private async void DisconnectOutlookClicked(object sender, RoutedEventArgs e)
    {
        if (modalOpen || microsoftContacts is not { } current) return;
        microsoftShutdown = ForgetMicrosoftContactsAsync(); await microsoftShutdown;
        if (!closed && account.SignedIn) await RestoreMicrosoftContactsAsync(accountId);
    }
    private async Task ForgetMicrosoftContactsAsync()
    {
        var current = microsoftContacts;
        microsoftOperation?.Cancel(); microsoftPolicyOperation?.Cancel(); microsoftOperation = null; microsoftContacts = null; microsoftOwner = ""; microsoftCompany = ""; microsoftPolicy = null; microsoftMessage = "";
        outlookContacts = []; RenderContacts(); UpdateMicrosoftContactsUi(); RefreshPeerDisplay();
        if (current is null) return;
        try { await current.DisconnectAsync(); }
        catch { if (!closed) ShowNotice("Die Microsoft-Verbindung wurde getrennt. Die lokale Speicherung konnte nicht vollständig entfernt werden; bitte Fonoo neu starten.", InfoBarSeverity.Warning); }
        finally { current.Dispose(); }
    }
    private string ContactName(string number)
    {
        var normalized = ContactRow.NormalizeNumber(number);
        if (normalized is null) return number;
        var local = favorites.Where(f => f.Number == normalized).Select(f => f.Name).Concat(contacts.Concat(outlookContacts).Where(c => c.Number == normalized).Select(c => c.Name))
            .Concat(teamSnapshot?.Members.Where(m => m.Number == normalized).Select(m => m.Name) ?? []).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
        return local.Length == 1 ? local[0] : number;
    }
    private SipSnapshot DisplaySnapshot => snapshot with { Peer = ContactName(snapshot.Peer), OriginalPeer = ContactName(snapshot.OriginalPeer) };
    private void RefreshPeerDisplay()
    {
        CallPeer.Text = ContactName(snapshot.Peer);
        CallPeerNumber.Text = CallPeer.Text == snapshot.Peer ? "" : snapshot.Peer;
        CallPeerNumber.Visibility = CallPeerNumber.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        OriginalCallLabel.Text = "Gehalten: " + ContactName(snapshot.OriginalPeer);
        tray?.Update(DisplaySnapshot, durationText); miniCall?.Update(DisplaySnapshot, callActionPending);
    }
}
