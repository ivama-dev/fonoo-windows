using Fonoo.Windows.Accounts;
using Fonoo.Windows.Desktop;
using Fonoo.Windows.Telephony;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.Contacts;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private static string UserDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fonoo", "Windows");
    private string activeTenant = "";
    private CallHistoryStore? historyStore;
    private readonly List<CallHistoryEntry> history = [];
    private ContactRow[] contacts = [];
    private readonly HashSet<string> windowsContactIds = new(StringComparer.Ordinal);
    private AppCapability? windowsContactCapability;
    private TeamSnapshot? teamSnapshot;
    private AvailabilitySnapshot? availability;
    private DispatcherQueueTimer? availabilityTimer;
    private bool availabilityPolling;
    private bool modalOpen;

    private void InitializeDirectory()
    {
        InitializePresence();
        try { windowsContactCapability = AppCapability.Create("contacts"); windowsContactCapability.AccessChanged += WindowsContactAccessChanged; }
        catch { /* Unsupported capability queries never request additional permissions. */ }
        Closed += (_, _) => { if (windowsContactCapability is not null) windowsContactCapability.AccessChanged -= WindowsContactAccessChanged; };
        historyTimer = DispatcherQueue.CreateTimer(); historyTimer.Interval = TimeSpan.FromSeconds(15);
        historyTimer.Tick += async (_, _) => await SyncHistoryAsync();
        availabilityTimer = DispatcherQueue.CreateTimer(); availabilityTimer.Interval = TimeSpan.FromSeconds(15);
        availabilityTimer.Tick += async (_, _) =>
        {
            if (availabilityPolling || profileBusy || modalOpen || operation is not null || closed || closingWithEngine || activeTenant.Length == 0 || !account.SignedIn) return;
            availabilityPolling = true;
            var tenant = activeTenant; var user = accountId; var lifetime = historyLifetime;
            try
            {
                var next = await account.GetAvailabilityAsync(tenant, user, lifetime?.Token ?? CancellationToken.None);
                if (closed || tenant != activeTenant || user != accountId || !ReferenceEquals(lifetime, historyLifetime)) return;
                await ApplyAvailabilityAsync(next);
            }
            catch (AccountException ex) when (ex.SessionExpired) { if (!closed && tenant == activeTenant && user == accountId) ResetSession(); }
            catch { /* Keep the last confirmed local preference through a network interruption. */ }
            finally { availabilityPolling = false; }
        };
    }
    private void LoadHistory(string tenant)
    {
        activeTenant = tenant; history.Clear(); historyStore = null; teamSnapshot = null; availability = null;
        profileMenu?.Hide(); RenderProfileButton();
        _ = RestoreMicrosoftContactsAsync(accountId);
        TeamList.ItemsSource = null; TeamDetailPanel.Visibility = Visibility.Collapsed; TeamCompany.Text = CompanyName.Text;
        TeamMessage.Text = "Lade dein Firmenverzeichnis.";
        try { var store = new CallHistoryStore(UserDataDirectory, accountId, tenant); history.AddRange(store.Load()); historyStore = store; }
        catch { ShowNotice("Die Anrufliste konnte nicht geladen werden. Die gespeicherte Datei bleibt erhalten."); }
        RenderHistory(); availabilityTimer?.Start(); StartHistorySync(tenant);
        StartPresenceScope();
    }
    private void ArchiveCall(CallHistoryEntry entry, string tenant, string user)
    {
        if (closed || tenant != activeTenant || user != accountId || historyStore is null || history.Any(c => c.Id == entry.Id)) return;
        if (historyRevision is not null) { _ = RefreshHistoryAfterCallAsync(); return; }
        try
        {
            var next = history.Append(entry).OrderByDescending(c => c.StartedAt).Take(500).ToArray(); historyStore.Save(next);
            history.Clear(); history.AddRange(next); RenderHistory();
        }
        catch { ShowNotice("Das Gespräch konnte nicht in der Anrufliste gespeichert werden."); }
        _ = RefreshHistoryAfterCallAsync();
    }
    private void RenderHistory()
    {
        var query = HistorySearch.Text.Trim();
        var missedOnly = HistoryFilter.SelectedItem == MissedHistoryFilter;
        var rows = history.Where(c => (!missedOnly || (c.Incoming && c.Outcome == "missed")) &&
            c.Number.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        HistoryList.ItemsSource = rows; HistoryEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Text = missedOnly
            ? query.Length == 0 ? "Keine verpassten Anrufe." : "Keine passenden verpassten Anrufe."
            : history.Count == 0 ? historyRevision is not null
                ? "Noch keine Anrufe in den letzten 90 Tagen. Deine Gespräche auf allen Fonoo-Geräten erscheinen hier."
                : "Noch keine Anrufe. Deine Gespräche erscheinen hier nach dem Abgleich mit deinem Fonoo-Konto."
                : "Keine passenden Anrufe.";
        ClearHistoryButton.IsEnabled = (historyStore is not null || historyRevision is not null) && history.Count > 0;
        HistoryRetentionStatus.Text = historyRevision is not null
            ? "Letzte 90 Tage · bis zu 500 Anrufe · Offline-Kopie auf diesem PC geschützt"
            : "Der gemeinsame Verlauf enthält die letzten 90 Tage und bis zu 500 Anrufe.";
    }
    private void HistoryFilterChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (HistoryList is not null) RenderHistory();
    }
    private void HistorySearchChanged(object sender, TextChangedEventArgs e) { if (HistoryList is not null) RenderHistory(); }
    private async void ClearHistoryClicked(object sender, RoutedEventArgs e) => await ConfirmDeleteHistoryAsync(null);
    private async void DeleteHistoryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: CallHistoryEntry entry }) await ConfirmDeleteHistoryAsync(entry);
    }
    private async Task ConfirmDeleteHistoryAsync(CallHistoryEntry? entry)
    {
        if (modalOpen || audioDialogOpen || (historyStore is null && historyRevision is null)) return;
        if (entry is not null && !history.Any(e => e.Id == entry.Id)) return;
        modalOpen = true;
        try
        {
            var tenant = activeTenant; var user = accountId; var lifetime = historyLifetime;
            var synchronized = historyRevision is not null;
            var dialog = NewDialog(entry is null ? "Anrufliste löschen?" : "Anruf löschen?", synchronized
                ? entry is null
                    ? "Deine Anrufliste für diese Firma wird auf allen Geräten gelöscht. Die Listen anderer Mitarbeiter bleiben erhalten."
                    : $"Der Anruf mit {entry.Title} wird aus deiner Anrufliste auf allen Geräten gelöscht. Die Listen anderer Mitarbeiter bleiben erhalten."
                : entry is null ? "Alle Einträge dieser Firma auf diesem PC werden gelöscht." : "Dieser Eintrag wird aus der bisherigen Liste auf diesem PC gelöscht.", "Löschen");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (tenant != activeTenant || user != accountId) return;
            if (synchronized)
            {
                var response = entry is null
                    ? await account.ClearHistoryAsync(tenant, lifetime?.Token ?? CancellationToken.None)
                    : await account.DeleteHistoryAsync(tenant, entry.Id, lifetime?.Token ?? CancellationToken.None);
                if (closed || tenant != activeTenant || user != accountId || !ReferenceEquals(lifetime, historyLifetime)) return;
                if (response.NotModified) throw new AccountException("Die Löschung wurde nicht bestätigt.");
                ApplyHistoryResponse(response, cloudHistoryCache!, tenant, user);
            }
            else
            {
                var remaining = entry is null ? [] : history.Where(e => e.Id != entry.Id).ToArray();
                historyStore!.Save(remaining); history.Clear(); history.AddRange(remaining);
            }
            RenderHistory();
            ShowNotice(synchronized ? "Aus deiner Anrufliste auf allen Geräten gelöscht." : "Aus der bisherigen Liste auf diesem PC gelöscht.", InfoBarSeverity.Success);
        }
        catch { ShowNotice("Die Anrufliste konnte nicht gelöscht werden."); }
        finally { modalOpen = false; }
    }
    private async Task CallNumberAsync(string number)
    {
        if (snapshot.InCall) { ShowNotice("Bitte beende zuerst dein laufendes Gespräch.", InfoBarSeverity.Informational); return; }
        if (DialNumber.Normalize(number) is not { } normalized) { ShowNotice("Diese Rufnummer kann nicht gewählt werden."); return; }
        Number.Text = normalized; DesktopNavigation.SelectedItem = DialpadTab;
        if (snapshot.Registered) await PhoneActionAsync(e => e.DialAsync(normalized));
        else ShowNotice("Die Rufnummer ist ausgewählt. Telefonie ist noch nicht verbunden.", InfoBarSeverity.Informational);
    }
    private async void HistoryClicked(object sender, ItemClickEventArgs e) { if (e.ClickedItem is CallHistoryEntry call) await CallNumberAsync(call.Number); }
    private async void ContactClicked(object sender, ItemClickEventArgs e) { if (e.ClickedItem is ContactListItem row) await CallNumberAsync(row.Contact.Number); }
    private void ContactFavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ContactRow contact } || favoritesStore is null ||
            (!contacts.Contains(contact) && !outlookContacts.Contains(contact)) || DialNumber.Normalize(contact.Number) is not { } number) return;
        if (favorites.Any(f => f.Number == number)) return;
        if (SaveFavorites(favorites.Append(new Favorite(Guid.NewGuid().ToString(), contact.Name.Trim(), number))))
            ShowNotice("Kontakt in deinen Favoriten gespeichert.", InfoBarSeverity.Success);
    }
    private void ContactsSearchChanged(object sender, TextChangedEventArgs e) { if (ContactsList is not null) RenderContacts(); }
    private void ContactsNavigationChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ContactsListPanel is null || ContactSourcesPanel is null || ContactsViewDescription is null) return;
        var sources = sender.SelectedItem == ContactSourcesTab;
        ContactsListPanel.Visibility = sources ? Visibility.Collapsed : Visibility.Visible;
        ContactSourcesPanel.Visibility = sources ? Visibility.Visible : Visibility.Collapsed;
        ContactsViewDescription.Text = sources
            ? "Verbinde Outlook oder lade Kontakte aus Windows und vCard-Dateien. Alle Kontakte erscheinen gemeinsam in deiner Kontaktliste."
            : "Wähle einen Kontakt zum Anrufen oder speichere eine Rufnummer über den Stern in deinen Favoriten.";
    }
    private void RenderContacts()
    {
        var all = contacts.Concat(outlookContacts).DistinctBy(c => (c.Name, c.Number)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var rows = all.Where(c => c.Matches(ContactsSearch.Text)).Select(c => new ContactListItem(c)).ToArray(); ContactsList.ItemsSource = rows;
        UpdateContactFavorites();
        ContactsEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ContactsEmpty.Text = all.Length == 0 ? "Noch keine Kontakte mit wählbarer Rufnummer. Im Reiter „Kontaktquellen“ kannst du Outlook verbinden, Windows-Kontakte laden oder eine vCard-Datei öffnen." : "Keine passenden Kontakte.";
        RenderPresence();
    }
    private void UpdateContactFavorites()
    {
        if (ContactsList?.ItemsSource is not ContactListItem[] rows) return;
        var numbers = favorites.Select(f => f.Number).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows) row.UpdateFavoriteState(numbers.Contains(row.Contact.Number), favoritesStore is not null);
    }
    private async void LoadContactsClicked(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var tenant = activeTenant; var user = accountId;
        try
        {
            var store = await ContactManager.RequestStoreAsync(ContactStoreAccessType.AllContactsReadOnly).AsTask(ct);
            if (store is null) throw new UnauthorizedAccessException();
            var values = await store.FindContactsAsync().AsTask(ct);
            var rows = new List<ContactRow>();
            foreach (var contact in values)
                foreach (var phone in contact.Phones)
                    if (ContactRow.NormalizeNumber(phone.Number) is { } number)
                        rows.Add(new(contact.Id + ":" + number, string.IsNullOrWhiteSpace(contact.DisplayName) ? number : contact.DisplayName, number, phone.Kind switch { ContactPhoneKind.Mobile => "Mobil", ContactPhoneKind.Work => "Arbeit", ContactPhoneKind.Home => "Privat", _ => "Telefon" }));
            ct.ThrowIfCancellationRequested();
            if (tenant != activeTenant || user != accountId) throw new OperationCanceledException(ct);
            contacts = rows.DistinctBy(c => (c.Name, c.Number)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Take(10000).ToArray(); RenderContacts();
            windowsContactIds.Clear(); windowsContactIds.UnionWith(contacts.Select(c => c.Id)); CheckWindowsContactAccess();
        }
        catch (OperationCanceledException) { throw; }
        catch { RemoveWindowsContacts(); throw new AccountException("Windows hat keine Kontakte freigegeben. Prüfe den Kontaktezugriff in den Windows-Einstellungen oder öffne eine vCard-Datei."); }
    });
    private void WindowsContactAccessChanged(AppCapability sender, AppCapabilityAccessChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => { if (!closed) CheckWindowsContactAccess(); });
    private void CheckWindowsContactAccess()
    {
        if (windowsContactIds.Count == 0) return;
        try { if (windowsContactCapability?.CheckAccess() == AppCapabilityAccessStatus.Allowed) return; }
        catch { }
        RemoveWindowsContacts();
    }
    private void RemoveWindowsContacts()
    {
        if (windowsContactIds.Count == 0) return;
        contacts = contacts.Where(c => !windowsContactIds.Contains(c.Id)).ToArray(); windowsContactIds.Clear();
        RenderContacts(); RefreshPeerDisplay();
    }
    private async void ImportContactsClicked(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var tenant = activeTenant; var user = accountId;
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".vcf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync(); if (file is null) return;
        ct.ThrowIfCancellationRequested();
        var properties = await file.GetBasicPropertiesAsync();
        if (properties.Size > 2_000_000) throw new AccountException("Bitte wähle eine Kontaktdatei mit maximal 2 MB.");
        var text = await FileIO.ReadTextAsync(file); ct.ThrowIfCancellationRequested();
        var next = VCardDirectory.Parse(text);
        if (tenant != activeTenant || user != accountId) throw new OperationCanceledException(ct);
        contacts = contacts.Concat(next).DistinctBy(c => (c.Name, c.Number)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Take(10000).ToArray(); RenderContacts();
        ShowNotice($"{next.Length} Rufnummern aus der Kontaktdatei geladen.", InfoBarSeverity.Success);
    });
    private async Task RefreshTeamAsync(CancellationToken ct)
    {
        if (activeTenant.Length == 0 || accountId.Length == 0) throw new AccountException("Bitte richte zuerst deine Nebenstelle ein.");
        var tenant = activeTenant; var user = accountId;
        teamSnapshot = null; TeamList.ItemsSource = null; TeamDetailPanel.Visibility = Visibility.Collapsed; TeamMessage.Text = "Team wird geladen …";
        try
        {
            var next = await account.GetTeamAsync(tenant, user, ct); ct.ThrowIfCancellationRequested();
            if (tenant != activeTenant || user != accountId) return;
            teamSnapshot = next; TeamCompany.Text = next.TenantName; RenderTeam();
        }
        catch { if (tenant == activeTenant && user == accountId) TeamMessage.Text = "Team konnte nicht geladen werden. Bitte erneut aktualisieren."; throw; }
        try
        {
            var next = await account.GetAvailabilityAsync(tenant, user, ct); ct.ThrowIfCancellationRequested();
            if (tenant != activeTenant || user != accountId) return;
            await ApplyAvailabilityAsync(next);
        }
        catch (AccountException ex) when (!ex.SessionExpired) { ShowNotice("Das Team ist geladen; die Verfügbarkeit konnte noch nicht aktualisiert werden.", InfoBarSeverity.Informational); }
    }
    private async Task ApplyAvailabilityAsync(AvailabilitySnapshot next)
    {
        if (next.TenantId != activeTenant || next.UserId != accountId || next.SelfUserId != accountId ||
            availability is { } previous && previous.Revision > next.Revision) return;
        availability = next;
        RenderProfileButton();
        if (next.TenantId == activeTenant && next.UserId == accountId && engine is { } current)
        {
            var dnd = manualDoNotDisturb || (next.Company.RoutingEnabled && next.Effective.Presence?.State == "do_not_disturb");
            if (snapshot.DoNotDisturb != dnd) await current.SetDoNotDisturbAsync(dnd);
        }
    }
    private async void RefreshTeamClicked(object sender, RoutedEventArgs e) => await RunAsync(RefreshTeamAsync);
    private void TeamSearchChanged(object sender, TextChangedEventArgs e) { if (TeamList is not null) RenderTeam(); }
    private void RenderTeam()
    {
        var rows = teamSnapshot?.Members.Where(m => m.Matches(TeamSearch.Text)).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Select(m => new TeamListItem(m)).ToArray() ?? [];
        TeamList.ItemsSource = rows; TeamMessage.Text = rows.Length == 0 ? "Keine passenden Teammitglieder." : $"{rows.Length} Personen · wähle eine Person für Details";
        TeamDetailPanel.Visibility = Visibility.Collapsed;
        RenderPresence();
    }
    private void TeamSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TeamList.SelectedItem is not TeamListItem { Member: var member } selected) { TeamDetailPanel.Visibility = Visibility.Collapsed; return; }
        TeamDetailPanel.Visibility = Visibility.Visible; TeamDetailName.Text = member.Name;
        TeamDetailText.Text = member.Detail + (member.AvailabilityLabel.Length > 0 ? "\n" + member.AvailabilityLabel : "") +
            (member.Id == accountId ? "\nDeine Geräte: " + string.Join(", ", member.PersonalDevices.Select(d => d.Name)) : "");
        UpdateTeamActions();
        TeamDetailPresence.Text = selected.Phone.Text + (selected.Phone.Detail.Length == 0 ? "" : " · " + selected.Phone.Detail);
    }
    private bool CanCallTeam(TeamMember member) => teamSnapshot is { } team && team.TenantId == activeTenant && team.SelfUserId == accountId &&
        team.Members.Contains(member) && member.Id != accountId && member.Callable && snapshot.Registered && !snapshot.InCall && operation is null;
    private void UpdateTeamActions()
    {
        if (TeamList.SelectedItem is not TeamListItem { Member: var member }) return;
        TeamCallButton.IsEnabled = CanCallTeam(member);
        TeamFavoriteButton.IsEnabled = member.Callable && member.Id != accountId && favoritesStore is not null;
        RenameSelfButton.Visibility = member.Id == accountId ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void CallTeamClicked(object sender, RoutedEventArgs e) { if (TeamList.SelectedItem is TeamListItem { Member: var member } && CanCallTeam(member)) await CallNumberAsync(member.Number!); }
    private void FavoriteTeamClicked(object sender, RoutedEventArgs e)
    {
        if (TeamList.SelectedItem is not TeamListItem { Member: { Callable: true } member } || member.Id == accountId || favoritesStore is null ||
            teamSnapshot?.TenantId != activeTenant || teamSnapshot.SelfUserId != accountId || !teamSnapshot.Members.Contains(member)) return;
        var id = TeamPresenceResolver.FavoriteId(activeTenant, member.Id);
        if (favorites.Any(f => f.Id == id)) { ShowNotice("Diese Person ist bereits in deinen Favoriten.", InfoBarSeverity.Informational); return; }
        var existing = favorites.FirstOrDefault(f => !f.Id.StartsWith("team:", StringComparison.Ordinal) && f.Number == member.Number);
        var favorite = new Favorite(id, existing?.Name ?? member.Name, member.Number!, existing?.GroupId);
        if (SaveFavorites(existing is null ? favorites.Append(favorite) : favorites.Select(f => f.Id == existing.Id ? favorite : f)))
            ShowNotice("Im Favoritenverzeichnis gespeichert.", InfoBarSeverity.Success);
    }
    private ContentDialog NewDialog(string title, object content, string primary = "Speichern") => new()
    { XamlRoot = ((FrameworkElement)Content).XamlRoot, RequestedTheme = ElementTheme.Light, Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = "Abbrechen" };
    private async void RenameSelfClicked(object sender, RoutedEventArgs e)
    {
        if (modalOpen || teamSnapshot is not { } current || TeamList.SelectedItem is not TeamListItem { Member: var member } || member.Id != accountId) return;
        modalOpen = true;
        try
        {
            var input = new TextBox { Header = "Dein Anzeigename", Text = member.Name, MaxLength = 80 };
            var dialog = NewDialog("Deinen Namen ändern", input);
            dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(input.Text)) args.Cancel = true; };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await RunAsync(async ct => { var next = await account.RenameSelfAsync(current, accountId, input.Text.Trim(), ct); teamSnapshot = next; RenderTeam(); });
        }
        finally { modalOpen = false; }
    }
}
