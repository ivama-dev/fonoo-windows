using Fonoo.Windows.Telephony;
using Fonoo.Windows.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using System.Collections.ObjectModel;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private SipEngine? engine;
    private Task engineShutdown = Task.CompletedTask;
    private SipSnapshot snapshot = new(false, false, false, false, "Nicht verbunden", "");
    private readonly ObservableCollection<Favorite> favorites = [];
    private FavoritesStore? favoritesStore;
    private string accountId = "";
    private bool callActionPending;
    private bool closingWithEngine;

    private void InitializeTelephony()
    {
        RenderFavoriteRows();
        var keys = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "0", "#" };
        for (var i = 0; i < keys.Length; i++)
        {
            var button = new Button { Content = keys[i], Tag = keys[i], FontSize = 22, Style = (Style)Application.Current.Resources["FonooDialKeyStyle"] };
            AutomationProperties.SetName(button, keys[i] == "*" ? "Stern" : keys[i] == "#" ? "Raute" : keys[i]);
            button.Click += KeyClicked;
            Grid.SetRow(button, i / 3); Grid.SetColumn(button, i % 3);
            Keypad.Children.Add(button);
        }

    }

    private async Task ConnectSipAsync(CancellationToken ct)
    {
        if (configuration is null) throw new InvalidOperationException("Konfiguration fehlt.");
        await StopSipAsync();
        ct.ThrowIfCancellationRequested();
        snapshot = new(false, false, false, false, "Telefonie wird verbunden …", "");
        UpdatePhone(snapshot);
        var current = new SipEngine(configuration);
        engine = current;
        var historyTenant = activeTenant; var historyUser = accountId;
        current.CallEnded += entry => DispatcherQueue.TryEnqueue(() => ArchiveCall(entry, historyTenant, historyUser));
        current.Changed += state => DispatcherQueue.TryEnqueue(() =>
        {
            if (!closed && ReferenceEquals(engine, current)) UpdatePhone(state);
        });
        try
        {
            await current.Ready.WaitAsync(ct);
            if (availability is { } initialAvailability) await ApplyAvailabilityAsync(initialAvailability);
            if (manualDoNotDisturb) await current.SetDoNotDisturbAsync(true);
        }
        catch
        {
            await StopSipAsync();
            throw;
        }
    }
    private Task StopSipAsync()
    {
        if (engine is { } old)
        {
            engine = null;
            engineShutdown = old.StopAsync();
        }
        return engineShutdown;
    }
    private void UpdatePhone(SipSnapshot state)
    {
        var wasInCall = snapshot.InCall;
        snapshot = state;
        if (state.Incoming && modalOpen) microsoftOperation?.Cancel();
        if (state.InCall) { StopMicrophoneMeter(); StopSpeakerTest(); }
        Status.Text = state.Registered && !state.InCall && !state.DoNotDisturb ? "Verbunden" : state.Status;
        ConversationTab.Visibility = state.InCall ? Visibility.Visible : Visibility.Collapsed;
        if (state.InCall && !wasInCall) { destinationBeforeCall = DesktopNavigation.SelectedItem; DesktopNavigation.SelectedItem = ConversationTab; }
        else if (!state.InCall && wasInCall && ReferenceEquals(DesktopNavigation.SelectedItem, ConversationTab)) DesktopNavigation.SelectedItem = destinationBeforeCall ?? DialpadTab;
        RenderDestination();
        RefreshPeerDisplay();
        UpdateMicrosoftContactsUi();
        CallStatus.Text = state.Status;
        AnswerButton.Visibility = state.Incoming ? Visibility.Visible : Visibility.Collapsed;
        MuteButton.IsEnabled = state.Active && !callActionPending;
        MuteButton.Content = state.Muted ? "Mikrofon aktivieren" : "Stummschalten";
        HoldButton.Content = state.Held ? "Fortsetzen" : "Halten";
        HoldButton.IsEnabled = (state.Active || state.Held) && !state.Consulting && !state.HoldPending && !state.TransferPending && !callActionPending;
        TransferButton.IsEnabled = state.CanTransfer && !callActionPending;
        TransferButton.Visibility = state.Consulting ? Visibility.Collapsed : Visibility.Visible;
        CompleteTransferButton.Visibility = ReturnOriginalButton.Visibility = state.Consulting ? Visibility.Visible : Visibility.Collapsed;
        CompleteTransferButton.IsEnabled = state.ConsultationActive && state.Held && !state.TransferPending && !callActionPending;
        ReturnOriginalButton.IsEnabled = !state.TransferPending && !callActionPending;
        OriginalCallLabel.Visibility = state.Consulting ? Visibility.Visible : Visibility.Collapsed;
        OriginalCallLabel.Text = "Gehalten: " + ContactName(state.OriginalPeer);
        HangUpButton.Content = state.Incoming ? "Ablehnen" : state.Consulting ? "Beide Gespräche beenden" : "Auflegen";
        HangUpButton.IsEnabled = AnswerButton.IsEnabled = !callActionPending;
        CallKeypadButton.IsEnabled = state.Active;
        SignOutButton.IsEnabled = !state.InCall;
        UpdateTeamActions(); UpdateDesktop(wasInCall);
        ReconnectButton.IsEnabled = !state.InCall && !state.Registered;
        ReconnectButton.Visibility = state.Registered || state.InCall ? Visibility.Collapsed : Visibility.Visible;
        Number.IsReadOnly = state.InCall;
        PlusButton.IsEnabled = DeleteButton.IsEnabled = !state.InCall;
        UpdateDialButton();
    }
    private void UpdateDialButton()
    {
        if (CallButton is not null) CallButton.IsEnabled = snapshot.Registered && !snapshot.InCall && !callActionPending && DialNumber.Normalize(Number.Text) is not null;
    }
    private async Task PhoneActionAsync(Func<SipEngine, Task> action)
    {
        if (callActionPending) return;
        var current = engine;
        if (current is null) { ShowNotice("Telefonie ist noch nicht verbunden."); return; }
        callActionPending = true;
        UpdatePhone(snapshot);
        try { StopMicrophoneMeter(); StopSpeakerTest(); await action(current); }
        catch { if (!closed && ReferenceEquals(current, engine)) ShowNotice("Die Telefonie-Aktion konnte nicht ausgeführt werden. Bitte prüfe den Verbindungsstatus."); }
        finally { callActionPending = false; if (!closed) UpdatePhone(snapshot); }
    }
    private async void CallClicked(object sender, RoutedEventArgs e)
    {
        var number = DialNumber.Normalize(Number.Text);
        if (number is null) { ShowNotice("Bitte gib eine gültige Rufnummer ein."); return; }
        Number.Text = number;
        await PhoneActionAsync(e => e.DialAsync(number));
    }
    private async void KeyClicked(object sender, RoutedEventArgs e)
    {
        var digit = (string)((Button)sender).Tag;
        if (snapshot.InCall) await PhoneActionAsync(e => e.DtmfAsync(digit[0]));
        else if (Number.Text.Length < 64)
        {
            var position = Number.SelectionStart;
            Number.Text = Number.Text.Remove(position, Number.SelectionLength).Insert(position, digit);
            Number.SelectionStart = position + 1;
        }
    }
    private void PlusClicked(object sender, RoutedEventArgs e) { if (Number.Text.Length == 0) Number.Text = "+"; }
    private void DeleteClicked(object sender, RoutedEventArgs e)
    {
        var start = Number.SelectionStart;
        if (Number.SelectionLength > 0) Number.Text = Number.Text.Remove(start, Number.SelectionLength);
        else if (start > 0) { Number.Text = Number.Text.Remove(start - 1, 1); Number.SelectionStart = start - 1; }
    }
    private void NumberChanged(object sender, TextChangedEventArgs e) => UpdateDialButton();
    private void NumberKeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) { e.Handled = true; CallClicked(sender, e); } }
    private async void HangUpClicked(object sender, RoutedEventArgs e) => await PhoneActionAsync(e => e.HangUpAsync());
    private async void AnswerClicked(object sender, RoutedEventArgs e) => await PhoneActionAsync(e => e.AnswerAsync());
    private async void MuteClicked(object sender, RoutedEventArgs e) => await PhoneActionAsync(e => e.MuteAsync(!snapshot.Muted));
    private async void ReconnectClicked(object sender, RoutedEventArgs e) => await RunAsync(ConnectSipAsync);
    private void ShowDialpadClicked(object sender, RoutedEventArgs e) => DesktopNavigation.SelectedItem = DialpadTab;

    private void LoadFavorites(string tenantId)
    {
        favorites.Clear();
        favoritesStore = null;
        try
        {
            var store = new FavoritesStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fonoo", "Windows"), accountId, tenantId);
            foreach (var favorite in store.Load()) favorites.Add(favorite);
            favoritesStore = store;
        }
        catch { ShowNotice("Favoriten konnten nicht geladen werden. Die gespeicherte Datei bleibt unverändert."); }
        AddFavoriteButton.IsEnabled = favoritesStore is not null;
        NoFavorites.Visibility = favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderFavoriteRows();
        UpdateContactFavorites();
    }
    private bool SaveFavorites(IEnumerable<Favorite> updated)
    {
        try
        {
            if (favoritesStore is null) throw new InvalidOperationException();
            var values = updated.ToArray();
            favoritesStore.Save(values);
            favorites.Clear();
            foreach (var favorite in values) favorites.Add(favorite);
            NoFavorites.Visibility = favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderFavoriteRows();
            UpdateContactFavorites(); RefreshPeerDisplay();
            return true;
        }
        catch { ShowNotice("Favoriten konnten nicht gespeichert werden. Deine bisherige Liste bleibt erhalten."); return false; }
    }
    private async Task EditFavoriteAsync(Favorite? existing)
    {
        if (modalOpen || audioDialogOpen) return;
        modalOpen = true;
        try
        {
        var name = new TextBox { Header = "Name", MaxLength = 160, Text = existing?.Name ?? "" };
        var number = new TextBox { Header = "Rufnummer", MaxLength = 64, Text = existing?.Number ?? Number.Text.Trim() };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(name); form.Children.Add(number); form.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = ((FrameworkElement)Content).XamlRoot, RequestedTheme = ElementTheme.Light,
            Title = existing is null ? "Favorit hinzufügen" : "Favorit bearbeiten", Content = form, PrimaryButtonText = "Speichern", CloseButtonText = "Abbrechen" };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || DialNumber.Normalize(number.Text) is null)
            { args.Cancel = true; error.Text = "Bitte gib einen Namen und eine gültige Rufnummer ein."; }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && DialNumber.Normalize(number.Text) is { } normalized)
        {
            var id = existing is not null && existing.Number == normalized ? existing.Id : Guid.NewGuid().ToString();
            var value = new Favorite(id, name.Text.Trim(), normalized);
            SaveFavorites(existing is null ? favorites.Append(value) : favorites.Select(f => f.Id == existing.Id ? value : f));
        }
        }
        finally { modalOpen = false; }
    }
    private async void AddFavoriteClicked(object sender, RoutedEventArgs e) => await EditFavoriteAsync(null);
    private async void EditFavoriteClicked(object sender, RoutedEventArgs e) { if (((MenuFlyoutItem)sender).Tag is Favorite f) await EditFavoriteAsync(f); }
    private void RemoveFavoriteClicked(object sender, RoutedEventArgs e) { if (((MenuFlyoutItem)sender).Tag is Favorite f) SaveFavorites(favorites.Where(item => item.Id != f.Id)); }
    private async void FavoriteClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FavoriteListItem { Favorite: var favorite } || snapshot.InCall) return;
        await CallNumberAsync(favorite.Number);
    }
}
