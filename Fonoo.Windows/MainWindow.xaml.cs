using Fonoo.Windows.Accounts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Net.Mail;
using Windows.Graphics;
using Windows.System;

namespace Fonoo.Windows;

public sealed partial class MainWindow : Window
{
    private readonly AccountClient account = new(new ProtectedSessionStore(UserDataDirectory), DeviceId());
    private bool choosingCompany;
    private bool refreshAccountAfterCall;
    private CancellationTokenSource? operation;
    private SipConfiguration? configuration;
    private string challenge = "";
    private bool closed;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonoo.ico"));
        InitializeTelephony();
        InitializeDirectory();
        InitializeNavigation();
        DesktopNavigation.SelectedItem = DialpadTab;
        AppWindow.Resize(new SizeInt32(980, 760));
        Closed += (_, _) => { StopPresence(); StopHistorySync(); StopMicrophoneMeter(); StopSpeakerTest(); closed = true; operation?.Cancel(); _ = StopSipAsync(); configuration?.ClearSecrets(); account.Dispose(); };
#if FONOO_DESIGN_PREVIEW
        Title = "Fonoo · Designvorschau";
        LoginPanel.Visibility = Visibility.Collapsed;
        PhonePanel.Visibility = Visibility.Visible;
        CompanyName.Text = "Fonoo für Windows";
        ExtensionLabel.Text = "Wählpad";
        ReconnectButton.Visibility = Visibility.Collapsed;
        AddFavoriteButton.IsEnabled = false;
        Status.Text = "Designvorschau · nicht verbunden";
        previewMode = true;
        InitializeDesignFixtures();
        RenderDestination();
#endif
        InitializeDesktop();
        InitializeMicrosoftContacts();
        ((FrameworkElement)Content).Loaded += RestoreSessionOnLoaded;
    }

    private bool startupHandled;
    private async void RestoreSessionOnLoaded(object sender, RoutedEventArgs e)
    {
        if (startupHandled || previewMode) return; startupHandled = true;
        await RunAsync(async ct => { if (account.Restore()) await LoadAccountAsync(ct); });
    }
    private Membership? ReadSelectedCompany(Membership[] memberships)
    {
        try
        {
            var selected = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(UserDataDirectory, "selected-company.json")));
            if (selected is { Length: 2 } && selected[0] == accountId) return memberships.FirstOrDefault(m => m.Id == selected[1] && m.CanProvision);
        }
        catch { }
        return null;
    }
    private void SaveSelectedCompany(Membership membership)
    {
        try { Directory.CreateDirectory(UserDataDirectory); File.WriteAllText(Path.Combine(UserDataDirectory, "selected-company.json"), System.Text.Json.JsonSerializer.Serialize(new[] { accountId, membership.Id })); }
        catch { ShowNotice("Die Firmenauswahl gilt für diese Sitzung, konnte aber nicht gespeichert werden.", InfoBarSeverity.Informational); }
    }
    private void ChangeCompanyClicked(object sender, RoutedEventArgs e)
    {
        if (snapshot.InCall) { ShowNotice("Bitte beende zuerst dein laufendes Gespräch.", InfoBarSeverity.Informational); return; }
        choosingCompany = true; DesktopNavigation.SelectedItem = AccountTab; RenderDestination();
    }

    private string ValidEmail()
    {
        var email = Email.Text.Trim();
        if (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
            throw new AccountException("Bitte gib deine vollständige E-Mail-Adresse ein.");
        return email;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (audioDialogOpen) return;
        if (previewMode) { ShowNotice("Die Designvorschau verbindet sich nicht mit deinem Konto.", InfoBarSeverity.Informational); return; }
        if (operation is not null) return;
        using var current = new CancellationTokenSource();
        operation = current;
        Actions.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsActive = true;
        Notice.IsOpen = false;
        try { await action(current.Token); }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (closed || current.IsCancellationRequested) return;
            if (ex is AccountException { SessionExpired: true }) ResetSession();
            if (configuration is null) Status.Text = account.SignedIn ? "Angemeldet · Einrichtung nicht abgeschlossen" : "Nicht angemeldet";
            ShowNotice(ex switch
            {
                AccountException => ex.Message,
                OperationCanceledException => "Die Anfrage dauert zu lange. Bitte versuche es erneut.",
                HttpRequestException => "Keine sichere Verbindung zum Kontodienst. Bitte prüfe deine Internetverbindung und versuche es erneut.",
                _ => "Die Einrichtung konnte nicht abgeschlossen werden. Bitte versuche es erneut."
            });
        }
        finally
        {
            if (!closed && ReferenceEquals(operation, current))
            {
                RenderDestination();
                operation = null;
                RenderProfileButton();
                UpdateTeamActions();
                Actions.IsEnabled = true;
                Progress.IsActive = false;
                Progress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ShowNotice(string message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private async void SendCodeClicked(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var email = ValidEmail();
        challenge = await account.SendCodeAsync(email, ct);
        ct.ThrowIfCancellationRequested();
        Code.Text = "";
        CodePanel.Visibility = Visibility.Visible;
        SendCodeButton.Content = "Neuen Code senden";
        ShowNotice("Wir haben dir einen Code geschickt. Er gilt zehn Minuten.", InfoBarSeverity.Informational);
    });
    private async void VerifyClicked(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        if (challenge.Length == 0 || Code.Text.Length != 6 || !Code.Text.All(c => c is >= '0' and <= '9'))
            throw new AccountException("Bitte gib den sechsstelligen Code aus deiner E-Mail ein.");
        if (RememberDevice.IsChecked == true) await account.VerifyDeviceAsync(challenge, Code.Text, ct);
        else await account.VerifyAsync(challenge, Code.Text, ct);
        await LoadAccountAsync(ct);
    });
    private async void PasswordClicked(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var email = ValidEmail();
        if (Password.Password.Length == 0) throw new AccountException("Bitte gib dein Passwort ein.");
        try { await account.PasswordAsync(email, Password.Password, ct); }
        finally { Password.Password = ""; }
        await LoadAccountAsync(ct);
    });

    private async Task LoadAccountAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        challenge = "";
        Code.Text = "";
        Password.Password = "";
        LoginPanel.Visibility = Visibility.Collapsed;
        if (configuration is null)
        {
            SetupPanel.Visibility = Visibility.Visible;
            PhonePanel.Visibility = Visibility.Collapsed;
            Status.Text = "Angemeldet · Nebenstelle wird geladen …";
        }
        SignOutButton.Visibility = Visibility.Visible;
        var summary = await account.GetAccountAsync(ct);
        if (RememberDevice.IsChecked == true && !account.Remembered) account.RememberAccessSession(summary);
        accountId = summary.Id;
        SetAccountPasswordButton.Visibility = summary.PasswordConfigured is not null && summary.PasswordManaged != true ? Visibility.Visible : Visibility.Collapsed;
        RememberedStatus.Text = account.CanRenew ? "Dieser PC bleibt angemeldet. Beim Abmelden wird die gespeicherte Geräteanmeldung entfernt." : account.Remembered ? "Deine Sitzung ist unter deinem Windows-Benutzer geschützt gespeichert. Wenn der Kontodienst sie beendet, meldest du dich erneut an." : "Sitzung bis zum Beenden der App. Aktiviere bei der nächsten Anmeldung „Auf diesem PC angemeldet bleiben“.";
        AccountEmail.Text = summary.Email;
        var memberships = await account.GetMembershipsAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (configuration is null) Status.Text = $"Angemeldet als {summary.Email}";
        Companies.ItemsSource = memberships;
        Companies.SelectedIndex = memberships.Length == 1 ? 0 : -1;
        SetupDescription.Text = memberships.Length switch
        {
            0 => "Deinem Konto ist noch keine Firma zugewiesen. Bitte wende dich an deinen Administrator und aktualisiere anschließend die Zuweisung.",
            1 => "Deine Nebenstelle wird automatisch aus deinem Fonoo-Konto übernommen.",
            _ => "Wähle die Firma, mit deren Nebenstelle du telefonieren möchtest."
        };
        if (configuration is not null && memberships.Any(m => m.Id == activeTenant && m.CanProvision))
        {
            _ = RestoreMicrosoftContactsAsync(accountId);
            refreshAccountAfterCall = false; choosingCompany = false; Status.Text = snapshot.Status; RenderDestination(); return;
        }
        if (snapshot.InCall) { refreshAccountAfterCall = true; ShowNotice("Die Kontozuweisung wurde aktualisiert. Die Nebenstelle wird nach dem Gespräch erneut eingerichtet.", InfoBarSeverity.Informational); return; }
        refreshAccountAfterCall = false;
        if (configuration is not null)
        {
            await StopSipAsync(); ct.ThrowIfCancellationRequested();
            microsoftShutdown = ForgetMicrosoftContactsAsync(); await microsoftShutdown;
            StopPresence(); StopHistorySync(); configuration.ClearSecrets(); configuration = null; activeTenant = "";
            favorites.Clear(); favoritesStore = null; history.Clear(); historyStore = null; teamSnapshot = null; availability = null;
            RenderFavoriteRows();
            CompanyName.Text = ExtensionLabel.Text = ""; availabilityTimer?.Stop(); RenderHistory(); RenderTeam();
            UpdatePhone(new(false, false, false, false, "Nebenstelle nicht zugewiesen", ""));
        }
        var selected = ReadSelectedCompany(memberships);
        if (selected is not null) await ProvisionAsync(selected, ct);
        else if (memberships.Length == 1 && memberships[0].CanProvision) await ProvisionAsync(memberships[0], ct);
        else if (memberships.Length == 1) SetupDescription.Text = "Für deine Firma fehlt eine Nebenstelle oder ein aktiver Telefoniezugang. Bitte kontaktiere deinen Administrator.";
    }

    private async Task ProvisionAsync(Membership membership, CancellationToken ct)
    {
        if (snapshot.InCall) throw new AccountException("Bitte beende zuerst das laufende Gespräch, bevor du die Firma wechselst.");
        Status.Text = "Deine Nebenstelle wird eingerichtet …";
        var next = await account.ProvisionAsync(membership, DeviceId(), ct);
        if (ct.IsCancellationRequested) { next.ClearSecrets(); ct.ThrowIfCancellationRequested(); }
        try { await StopSipAsync(); ct.ThrowIfCancellationRequested(); }
        catch { next.ClearSecrets(); throw; }
        configuration?.ClearSecrets();
        configuration = next;
        choosingCompany = false;
        SaveSelectedCompany(membership);
        CompanyName.Text = membership.Name;
        ExtensionLabel.Text = $"Deine Nebenstelle: {membership.Extension}";
        SetupPanel.Visibility = Visibility.Collapsed;
        PhonePanel.Visibility = Visibility.Visible;
        LoadFavorites(membership.Id);
        LoadHistory(membership.Id);
        try { await ApplyAvailabilityAsync(await account.GetAvailabilityAsync(activeTenant, accountId, ct)); }
        catch (AccountException ex) when (!ex.SessionExpired) { /* Older services can provision SIP before directory APIs are deployed. */ }
        catch (HttpRequestException) { /* SIP setup can proceed while presence is temporarily unreachable. */ }
        ct.ThrowIfCancellationRequested();
        DesktopNavigation.SelectedItem = DialpadTab;
        RenderDestination();
        await ConnectSipAsync(ct);
    }

    private static string DeviceId()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fonoo", "Windows");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "device-id");
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path), out var saved)) return saved.ToString();
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(path, id);
        return id;
    }

    private async void ConnectClicked(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        if (Companies.SelectedItem is not Membership membership) throw new AccountException("Bitte wähle deine Firma aus.");
        await ProvisionAsync(membership, ct);
    });
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RunAsync(LoadAccountAsync);
    private void CompanyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectButton is not null) ConnectButton.IsEnabled = Companies.SelectedItem is Membership { CanProvision: true };
    }
    private async void SignOutClicked(object sender, RoutedEventArgs e)
    {
        if (snapshot.InCall) return;
        operation?.Cancel();
        Task revoke;
        try { revoke = account.RevokeDeviceAsync(CancellationToken.None); }
        catch { revoke = Task.CompletedTask; }
        ResetSession();
        try { await revoke; } catch { if (!closed) ShowNotice("Du bist auf diesem PC abgemeldet. Die Abmeldung im Kontodienst konnte nicht bestätigt werden.", InfoBarSeverity.Informational); }
    }
    private void ResetSession()
    {
        StopPresence();
        microsoftShutdown = ForgetMicrosoftContactsAsync();
        _ = StopSipAsync();
        snapshot = new(false, false, false, false, "Nicht verbunden", "");
        favorites.Clear();
        RenderFavoriteRows();
        favoritesStore = null;
        StopHistorySync(); activityTimer?.Stop(); availabilityTimer?.Stop(); miniCall?.AppWindow.Hide();
        history.Clear(); historyStore = null; contacts = []; teamSnapshot = null; availability = null;
        windowsContactIds.Clear();
        activeTenant = ""; choosingCompany = false; refreshAccountAfterCall = false; RenderHistory(); RenderContacts();
        profileMenu?.Hide(); RenderProfileButton();
        manualDoNotDisturb = false;
        TeamList.ItemsSource = null; TeamDetailPanel.Visibility = Visibility.Collapsed;
        accountId = "";
        CallPanel.Visibility = Visibility.Collapsed;
        SignOutButton.IsEnabled = true;
        account.SignOut();
        configuration?.ClearSecrets();
        configuration = null;
        challenge = "";
        Password.Password = "";
        Code.Text = "";
        Email.Text = "";
        Number.Text = "";
        Companies.ItemsSource = null;
        CompanyName.Text = ExtensionLabel.Text = "";
        PhonePanel.Visibility = SetupPanel.Visibility = CodePanel.Visibility = SignOutButton.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        SendCodeButton.Content = "Bestätigungscode senden";
        Status.Text = "Nicht angemeldet";
        Notice.IsOpen = false;
        AccountEmail.Text = "";
        ConversationTab.Visibility = Visibility.Collapsed;
        DesktopNavigation.SelectedItem = DialpadTab;
        UpdatePhone(snapshot);
    }
    private void EmailChanged(object sender, TextChangedEventArgs e)
    {
        challenge = "";
        if (CodePanel is null) return;
        Code.Text = "";
        CodePanel.Visibility = Visibility.Collapsed;
        SendCodeButton.Content = "Bestätigungscode senden";
    }
    private void EmailKeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) { e.Handled = true; SendCodeClicked(sender, e); } }
    private void CodeKeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) { e.Handled = true; VerifyClicked(sender, e); } }
    private void PasswordKeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) { e.Handled = true; PasswordClicked(sender, e); } }
}
