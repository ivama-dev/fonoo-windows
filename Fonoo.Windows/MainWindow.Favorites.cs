using Fonoo.Windows.Desktop;
using Fonoo.Windows.Telephony;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private readonly List<FavoriteGroup> favoriteGroups = [];
    private FavoriteListItem[] favoriteRows = [];
    private FavoriteGroupSection? draggedFavoriteGroup;
    private FavoritesStore? favoriteGroupDragStore;
    private global::Windows.Foundation.Point favoriteGroupDragStart;
    private double favoriteGroupDragX, favoriteGroupDragY;
    private Border? favoriteDropMarker;
    private Brush? favoriteMarkerBackground, favoriteMarkerBrush;
    private Thickness favoriteMarkerThickness;
    private long favoriteClickBlockedUntil;
    private FavoriteLibrary FavoriteLibrarySnapshot() => new(favorites.ToArray(), favoriteGroups.ToArray());

    private void LoadFavorites(string tenantId)
    {
        favorites.Clear(); favoriteGroups.Clear(); favoritesStore = null;
        try
        {
            var store = new FavoritesStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fonoo", "Windows"), accountId, tenantId);
            var library = store.LoadLibrary();
            foreach (var favorite in library.Favorites) favorites.Add(favorite);
            favoriteGroups.AddRange(library.Groups); favoritesStore = store;
        }
        catch { ShowNotice("Favoriten konnten nicht geladen werden. Die gespeicherte Datei bleibt unverändert."); }
        RenderFavoriteRows(); UpdateContactFavorites();
    }

    private bool SaveFavorites(IEnumerable<Favorite> updated) => SaveFavoriteLibrary(new(updated.ToArray(), favoriteGroups.ToArray()));
    private bool SaveFavoriteLibrary(FavoriteLibrary library)
    {
        try
        {
            if (favoritesStore is null) throw new InvalidOperationException();
            favoritesStore.SaveLibrary(library);
            favorites.Clear(); foreach (var favorite in library.Favorites) favorites.Add(favorite);
            favoriteGroups.Clear(); favoriteGroups.AddRange(library.Groups);
            RenderFavoriteRows(); UpdateContactFavorites(); RefreshPeerDisplay();
            return true;
        }
        catch { ShowNotice("Favoriten konnten nicht gespeichert werden. Deine bisherige Liste bleibt erhalten."); return false; }
    }

    private ComboBox FavoriteGroupPicker(string? selected)
    {
        var picker = new ComboBox { Header = "Gruppe", HorizontalAlignment = HorizontalAlignment.Stretch };
        picker.Items.Add(new ComboBoxItem { Content = "Ohne Gruppe", Tag = "" });
        foreach (var group in favoriteGroups) picker.Items.Add(new ComboBoxItem { Content = group.Name, Tag = group.Id });
        picker.SelectedIndex = selected is null ? 0 : favoriteGroups.FindIndex(g => g.Id == selected) + 1;
        return picker;
    }
    private static string? PickedGroup(ComboBox picker) => picker.SelectedItem is ComboBoxItem { Tag: string id } && id.Length > 0 ? id : null;

    private async Task EditFavoriteAsync(Favorite? existing)
    {
        if (modalOpen || audioDialogOpen || favoritesStore is null) return;
        var store = favoritesStore;
        modalOpen = true;
        try
        {
            var name = new TextBox { Header = "Name", MaxLength = 160, Text = existing?.Name ?? "" };
            var number = new TextBox { Header = "Rufnummer", MaxLength = 64, Text = existing?.Number ?? Number.Text.Trim() };
            var group = FavoriteGroupPicker(existing?.GroupId);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var form = new StackPanel { Spacing = 12, MinWidth = 280 };
            form.Children.Add(name); form.Children.Add(number); form.Children.Add(group); form.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = ((FrameworkElement)Content).XamlRoot, RequestedTheme = ElementTheme.Light,
                Title = existing is null ? "Favorit hinzufügen" : "Favorit bearbeiten", Content = form, PrimaryButtonText = "Speichern", CloseButtonText = "Abbrechen" };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(name.Text) || DialNumber.Normalize(number.Text) is null)
                { args.Cancel = true; error.Text = "Bitte gib einen Namen und eine gültige Rufnummer ein."; }
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && ReferenceEquals(store, favoritesStore) &&
                DialNumber.Normalize(number.Text) is { } normalized)
            {
                var id = existing is not null && existing.Number == normalized ? existing.Id : Guid.NewGuid().ToString();
                var value = new Favorite(id, name.Text.Trim(), normalized, PickedGroup(group));
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
        if (e.ClickedItem is not FavoriteListItem { Favorite: var favorite } || snapshot.InCall || Environment.TickCount64 < favoriteClickBlockedUntil) return;
        await CallNumberAsync(favorite.Number);
    }

    private async void AddFavoriteGroupClicked(object sender, RoutedEventArgs e) => await EditFavoriteGroupAsync(null);
    private async Task EditFavoriteGroupAsync(FavoriteGroup? existing)
    {
        if (modalOpen || audioDialogOpen || favoritesStore is null) return;
        var store = favoritesStore; modalOpen = true;
        try
        {
            var name = new TextBox { Header = "Gruppenname", MaxLength = 60, Text = existing?.Name ?? "", MinWidth = 280 };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var form = new StackPanel { Spacing = 12 }; form.Children.Add(name); form.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = ((FrameworkElement)Content).XamlRoot, RequestedTheme = ElementTheme.Light,
                Title = existing is null ? "Gruppe anlegen" : "Gruppe umbenennen", Content = form, PrimaryButtonText = "Speichern", CloseButtonText = "Abbrechen" };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (!FavoriteGroups.ValidName(name.Text) || favoriteGroups.Any(g => g.Id != existing?.Id && string.Equals(g.Name.Trim(), name.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                { args.Cancel = true; error.Text = "Bitte wähle einen eindeutigen Namen mit 1 bis 60 Zeichen."; }
                else if (existing is null && favoriteGroups.Count >= 50)
                { args.Cancel = true; error.Text = "Du kannst bis zu 50 Gruppen anlegen."; }
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && ReferenceEquals(store, favoritesStore))
                SaveFavoriteLibrary(FavoriteGroups.Upsert(FavoriteLibrarySnapshot(), existing?.Id ?? Guid.NewGuid().ToString(), name.Text));
        }
        finally { modalOpen = false; }
    }

    private async Task DeleteFavoriteGroupAsync(FavoriteGroup group)
    {
        if (modalOpen || audioDialogOpen || favoritesStore is null) return;
        var store = favoritesStore; modalOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = ((FrameworkElement)Content).XamlRoot, RequestedTheme = ElementTheme.Light,
                Title = "Gruppe entfernen?", Content = $"„{group.Name}“ wird entfernt. Die enthaltenen Favoriten bleiben unter „Ohne Gruppe“ erhalten.",
                PrimaryButtonText = "Gruppe entfernen", CloseButtonText = "Abbrechen", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && ReferenceEquals(store, favoritesStore))
                SaveFavoriteLibrary(FavoriteGroups.Delete(FavoriteLibrarySnapshot(), group.Id));
        }
        finally { modalOpen = false; }
    }

    private void FavoriteGroupMoreClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FavoriteGroupSection { Id: { } id } } button || favoriteGroups.FirstOrDefault(g => g.Id == id) is not { } group) return;
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Umbenennen", Icon = new SymbolIcon(Symbol.Edit) };
        rename.Click += async (_, _) => await EditFavoriteGroupAsync(group);
        var delete = new MenuFlyoutItem { Text = "Gruppe entfernen", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await DeleteFavoriteGroupAsync(group);
        menu.Items.Add(rename); menu.Items.Add(delete); menu.ShowAt(button);
    }
    private void FavoriteGroupUpClicked(object sender, RoutedEventArgs e) => ShiftFavoriteGroup(sender, -1);
    private void FavoriteGroupDownClicked(object sender, RoutedEventArgs e) => ShiftFavoriteGroup(sender, 1);
    private void ShiftFavoriteGroup(object sender, int offset)
    {
        if (sender is not Button { Tag: FavoriteGroupSection { Id: { } id } }) return;
        var index = favoriteGroups.FindIndex(g => g.Id == id); var target = index + offset;
        if (index >= 0 && target >= 0 && target < favoriteGroups.Count)
            SaveFavoriteLibrary(FavoriteGroups.MoveGroup(FavoriteLibrarySnapshot(), id, favoriteGroups[target].Id, offset > 0));
    }

    private void FavoriteMoreClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Favorite favorite } button) return;
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, RoutedEventHandler handler)
        {
            var item = new MenuFlyoutItem { Text = text, Tag = favorite }; item.Click += handler; menu.Items.Add(item); return item;
        }
        Item("Bearbeiten", EditFavoriteClicked);
        Item("In Gruppe verschieben …", MoveFavoriteClicked);
        var peers = favorites.Where(f => f.GroupId == favorite.GroupId).ToArray(); var index = Array.FindIndex(peers, f => f.Id == favorite.Id);
        Item("Nach oben", FavoriteUpClicked).IsEnabled = index > 0;
        Item("Nach unten", FavoriteDownClicked).IsEnabled = index >= 0 && index < peers.Length - 1;
        menu.Items.Add(new MenuFlyoutSeparator()); Item("Entfernen", RemoveFavoriteClicked);
        menu.ShowAt(button);
    }
    private async void MoveFavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: Favorite favorite } || modalOpen || audioDialogOpen || favoritesStore is null) return;
        var store = favoritesStore; modalOpen = true;
        try
        {
            var picker = FavoriteGroupPicker(favorite.GroupId);
            var dialog = new ContentDialog { XamlRoot = ((FrameworkElement)Content).XamlRoot, RequestedTheme = ElementTheme.Light,
                Title = "Favorit verschieben", Content = picker, PrimaryButtonText = "Verschieben", CloseButtonText = "Abbrechen" };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && ReferenceEquals(store, favoritesStore) && PickedGroup(picker) != favorite.GroupId)
                SaveFavoriteLibrary(FavoriteGroups.MoveFavorite(FavoriteLibrarySnapshot(), favorite.Id, PickedGroup(picker)));
        }
        finally { modalOpen = false; }
    }
    private void FavoriteUpClicked(object sender, RoutedEventArgs e) => ShiftFavorite(sender, -1);
    private void FavoriteDownClicked(object sender, RoutedEventArgs e) => ShiftFavorite(sender, 1);
    private void ShiftFavorite(object sender, int offset)
    {
        if (sender is not MenuFlyoutItem { Tag: Favorite favorite }) return;
        var peers = favorites.Where(f => f.GroupId == favorite.GroupId).ToArray();
        var index = Array.FindIndex(peers, f => f.Id == favorite.Id); var target = index + offset;
        if (index >= 0 && target >= 0 && target < peers.Length)
            SaveFavoriteLibrary(FavoriteGroups.MoveFavorite(FavoriteLibrarySnapshot(), favorite.Id, favorite.GroupId, peers[target].Id, offset > 0));
    }

    private void FavoriteGroupThumbStarted(object sender, DragStartedEventArgs args)
    {
        if (sender is not Thumb { Tag: FavoriteGroupSection { Id: not null } section } thumb || favoritesStore is null || modalOpen) return;
        draggedFavoriteGroup = section; favoriteGroupDragStore = favoritesStore;
        favoriteGroupDragStart = thumb.TransformToVisual((UIElement)Content).TransformPoint(new(args.HorizontalOffset, args.VerticalOffset));
        favoriteGroupDragX = favoriteGroupDragY = 0;
    }
    private void FavoriteGroupThumbDelta(object sender, DragDeltaEventArgs args)
    {
        if (draggedFavoriteGroup is null || !ReferenceEquals(favoriteGroupDragStore, favoritesStore)) return;
        favoriteGroupDragX += args.HorizontalChange; favoriteGroupDragY += args.VerticalChange;
        if (Math.Abs(favoriteGroupDragX) + Math.Abs(favoriteGroupDragY) < 6) return;
        ((Thumb)sender).Opacity = .65;
        ClearFavoriteDropMarker();
        var point = new global::Windows.Foundation.Point(favoriteGroupDragStart.X + favoriteGroupDragX, favoriteGroupDragStart.Y + favoriteGroupDragY);
        if (FavoriteGroupTarget(point) is not { } target) return;
        var marker = target.Element as Border ?? (target.Element as StackPanel)?.Children.OfType<Border>().FirstOrDefault();
        if (marker is null) return;
        SetFavoriteDropMarker(marker, target.After);
    }
    private (string Id, FrameworkElement Element, bool After)? FavoriteGroupTarget(global::Windows.Foundation.Point point)
    {
        var root = (UIElement)Content;
        var target = VisualTreeHelper.FindElementsInHostCoordinates(point, root).OfType<FrameworkElement>()
            .FirstOrDefault(el => el.Tag is FavoriteGroupSection { Id: { } id } && id != draggedFavoriteGroup?.Id);
        if (target?.Tag is not FavoriteGroupSection { Id: { } targetId }) return null;
        var top = target.TransformToVisual(root).TransformPoint(new(0, 0)).Y;
        return (targetId, target, point.Y > top + Math.Min(target.ActualHeight, 40) / 2);
    }
    private void FavoriteGroupThumbCompleted(object sender, DragCompletedEventArgs args)
    {
        ((Thumb)sender).Opacity = 1; ClearFavoriteDropMarker();
        var group = draggedFavoriteGroup; var store = favoriteGroupDragStore;
        var point = new global::Windows.Foundation.Point(favoriteGroupDragStart.X + args.HorizontalChange, favoriteGroupDragStart.Y + args.VerticalChange);
        var target = FavoriteGroupTarget(point);
        draggedFavoriteGroup = null; favoriteGroupDragStore = null;
        if (!args.Canceled && group?.Id is { } id && ReferenceEquals(store, favoritesStore) && !modalOpen &&
            Math.Abs(args.HorizontalChange) + Math.Abs(args.VerticalChange) >= 6 && target is { } destination)
            SaveFavoriteLibrary(FavoriteGroups.MoveGroup(FavoriteLibrarySnapshot(), id, destination.Id, destination.After));
    }
    private void ClearFavoriteDropMarker()
    {
        if (favoriteDropMarker is not { } marker) return;
        marker.Background = favoriteMarkerBackground; marker.BorderBrush = favoriteMarkerBrush; marker.BorderThickness = favoriteMarkerThickness;
        favoriteDropMarker = null;
    }
    private Favorite? draggedFavorite;
    private void FavoriteThumbStarted(object sender, DragStartedEventArgs args)
    {
        if (sender is not Thumb { Tag: Favorite favorite } thumb || favoritesStore is null || modalOpen) return;
        favoriteClickBlockedUntil = Environment.TickCount64 + 500;
        draggedFavorite = favorite; favoriteGroupDragStore = favoritesStore;
        favoriteGroupDragStart = thumb.TransformToVisual((UIElement)Content).TransformPoint(new(args.HorizontalOffset, args.VerticalOffset));
        favoriteGroupDragX = favoriteGroupDragY = 0;
    }
    private (string? GroupId, string? FavoriteId, Border? Marker, bool After)? FavoriteTarget(global::Windows.Foundation.Point point)
    {
        var root = (UIElement)Content;
        var elements = VisualTreeHelper.FindElementsInHostCoordinates(point, root).OfType<FrameworkElement>().ToArray();
        if (elements.OfType<Border>().FirstOrDefault(el => el.Tag is Favorite) is { Tag: Favorite favorite } row)
        {
            if (favorite.Id == draggedFavorite?.Id) return null;
            return (favorite.GroupId, favorite.Id, row, point.Y > row.TransformToVisual(root).TransformPoint(new(0, row.ActualHeight / 2)).Y);
        }
        if (elements.FirstOrDefault(el => el.Tag is FavoriteGroupSection) is not { Tag: FavoriteGroupSection section } target) return null;
        var marker = target as Border ?? (target as StackPanel)?.Children.OfType<Border>().FirstOrDefault();
        return (section.Id, null, marker, true);
    }
    private void FavoriteThumbDelta(object sender, DragDeltaEventArgs args)
    {
        if (draggedFavorite is null || !ReferenceEquals(favoriteGroupDragStore, favoritesStore)) return;
        favoriteGroupDragX += args.HorizontalChange; favoriteGroupDragY += args.VerticalChange;
        if (Math.Abs(favoriteGroupDragX) + Math.Abs(favoriteGroupDragY) < 6) return;
        ((Thumb)sender).Opacity = .65; ClearFavoriteDropMarker();
        var point = new global::Windows.Foundation.Point(favoriteGroupDragStart.X + favoriteGroupDragX, favoriteGroupDragStart.Y + favoriteGroupDragY);
        if (FavoriteTarget(point) is { Marker: { } marker } target) SetFavoriteDropMarker(marker, target.After);
    }
    private void FavoriteThumbCompleted(object sender, DragCompletedEventArgs args)
    {
        // A release that rearranges rows must never also initiate a call.
        favoriteClickBlockedUntil = Environment.TickCount64 + 500;
        ((Thumb)sender).Opacity = 1; ClearFavoriteDropMarker();
        var favorite = draggedFavorite; var store = favoriteGroupDragStore;
        var point = new global::Windows.Foundation.Point(favoriteGroupDragStart.X + args.HorizontalChange, favoriteGroupDragStart.Y + args.VerticalChange);
        var target = FavoriteTarget(point);
        draggedFavorite = null; favoriteGroupDragStore = null;
        if (!args.Canceled && favorite is not null && ReferenceEquals(store, favoritesStore) && !modalOpen &&
            Math.Abs(args.HorizontalChange) + Math.Abs(args.VerticalChange) >= 6 && target is { } destination)
            SaveFavoriteLibrary(FavoriteGroups.MoveFavorite(FavoriteLibrarySnapshot(), favorite.Id, destination.GroupId, destination.FavoriteId, destination.After));
    }
    private void SetFavoriteDropMarker(Border marker, bool after)
    {
        favoriteDropMarker = marker; favoriteMarkerBackground = marker.Background; favoriteMarkerBrush = marker.BorderBrush; favoriteMarkerThickness = marker.BorderThickness;
        marker.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 247, 244, 253));
        marker.BorderBrush = (Brush)Application.Current.Resources["FonooPurple"];
        marker.BorderThickness = after ? new(0, 0, 0, 2) : new(0, 2, 0, 0);
    }
}
