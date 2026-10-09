using Fonoo.Windows.Accounts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private bool profileBusy;
    private MenuFlyout? profileMenu;
    private sealed record ProfileContext(string Tenant, string User, CancellationTokenSource? Lifetime);
    private ProfileContext CaptureProfileContext() => new(activeTenant, accountId, historyLifetime);
    private bool ProfileScopeCurrent(ProfileContext context) => !closed && !closingWithEngine &&
        context.Tenant == activeTenant && context.User == accountId && context.Tenant.Length > 0 && context.User.Length > 0 &&
        ReferenceEquals(context.Lifetime, historyLifetime);
    private bool ProfileContextCurrent(ProfileContext context) => ProfileScopeCurrent(context) && (previewMode || account.SignedIn);
    private bool CanOpenProfiles => !profileBusy && !modalOpen && !audioDialogOpen && !quitDialogOpen && operation is null && ProfileContextCurrent(CaptureProfileContext());

    private void RenderProfileButton()
    {
        if (ProfileButton is null) return;
        ProfileButton.Visibility = ProfileContextCurrent(CaptureProfileContext()) ? Visibility.Visible : Visibility.Collapsed;
        ProfileButton.IsEnabled = !profileBusy && !modalOpen && operation is null && !closingWithEngine;
        ProfileName.Text = availability?.Settings.ActiveProfile.Name ?? "Anrufprofile";
        var description = "Anrufprofil: " + ProfileName.Text + " · Geräte für eingehende Anrufe auswählen";
        AutomationProperties.SetName(ProfileButton, description);
        ToolTipService.SetToolTip(ProfileButton, description);
    }
    private async Task<AvailabilitySnapshot> ReadProfilesAsync(ProfileContext context)
    {
        if (!ProfileContextCurrent(context)) throw new OperationCanceledException();
        if (previewMode && availability is { } fixture) return fixture;
        var next = await account.GetAvailabilityAsync(context.Tenant, context.User, context.Lifetime?.Token ?? CancellationToken.None);
        if (!ProfileContextCurrent(context)) throw new OperationCanceledException();
        await ApplyAvailabilityAsync(next);
        if (!ProfileContextCurrent(context)) throw new OperationCanceledException();
        return availability!;
    }
    private async Task<AvailabilitySnapshot> WriteProfilesAsync(ProfileContext context, AvailabilitySnapshot current, DeviceProfileChanges changes)
    {
        if (!ProfileContextCurrent(context) || current.TenantId != context.Tenant || current.UserId != context.User) throw new OperationCanceledException();
        AvailabilitySnapshot next;
        if (previewMode)
        {
            next = changes.Apply(current).Validate(context.Tenant, context.User); next.Revision++;
        }
        else next = await account.SaveDeviceProfilesAsync(current, changes, context.Lifetime?.Token ?? CancellationToken.None);
        if (!ProfileContextCurrent(context)) throw new OperationCanceledException();
        await ApplyAvailabilityAsync(next);
        if (!ProfileContextCurrent(context)) throw new OperationCanceledException();
        return availability!;
    }
    private string ProfileError(Exception error, ProfileContext context)
    {
        if (error is AccountException { SessionExpired: true } && ProfileScopeCurrent(context)) ResetSession();
        return error is AccountException ? error.Message : "Die Anrufprofile konnten nicht aktualisiert werden. Bitte erneut versuchen.";
    }
    private async void ProfilesClicked(object sender, RoutedEventArgs e)
    {
        if (!CanOpenProfiles) return;
        var context = CaptureProfileContext(); profileBusy = true; RenderProfileButton();
        try
        {
            var current = await ReadProfilesAsync(context);
            profileMenu = new MenuFlyout();
            foreach (var profile in current.Settings.Profiles)
            {
                var item = new MenuFlyoutItem { Text = profile.Name, Icon = new FontIcon { Glyph = profile.Id == current.Settings.ActiveProfile.Id ? "\uE73E" : "\uE739" } };
                AutomationProperties.SetName(item, profile.Name + (profile.Id == current.Settings.ActiveProfile.Id ? ", aktiv" : ", aktivieren"));
                item.Click += async (_, _) =>
                {
                    if (profileBusy || !ProfileContextCurrent(context) || profile.Id == availability?.Settings.ActiveProfile.Id) return;
                    profileBusy = true; RenderProfileButton();
                    try { await WriteProfilesAsync(context, current, DeviceProfileChanges.Select(current, profile.Id)); }
                    catch (OperationCanceledException) when (!ProfileContextCurrent(context)) { }
                    catch (Exception ex) { if (ProfileScopeCurrent(context)) ShowNotice(ProfileError(ex, context)); }
                    finally { profileBusy = false; RenderProfileButton(); }
                };
                profileMenu.Items.Add(item);
            }
            profileMenu.Items.Add(new MenuFlyoutSeparator());
            var manage = new MenuFlyoutItem { Text = "Profile verwalten", Icon = new SymbolIcon(Symbol.Setting) };
            manage.Click += ManageProfilesClicked; profileMenu.Items.Add(manage);
        }
        catch (OperationCanceledException) when (!ProfileContextCurrent(context)) { }
        catch (Exception ex) { if (ProfileScopeCurrent(context)) ShowNotice(ProfileError(ex, context)); profileMenu = null; }
        finally { profileBusy = false; RenderProfileButton(); }
        if (ProfileContextCurrent(context)) profileMenu?.ShowAt(ProfileButton);
    }
    private static TextBlock ProfileText(string text, bool heading = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = heading ? 16 : 13,
        FontWeight = heading ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
    };
    private ContentDialog ProfileDialog(string title, StackPanel form) => NewDialog(title,
        new ScrollViewer { Content = form, MaxHeight = 460, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, "");

    private async void ManageProfilesClicked(object sender, RoutedEventArgs e)
    {
        if (!CanOpenProfiles) return;
        var context = CaptureProfileContext(); modalOpen = true; RenderProfileButton();
        var advanced = false;
        try
        {
            while (ProfileContextCurrent(context))
            {
                profileBusy = true; RenderProfileButton();
                var current = await ReadProfilesAsync(context);
                profileBusy = false;
                DeviceProfile? editing = null; var isNew = false;
                var form = new StackPanel { Spacing = 16, MinWidth = 350, MaxWidth = 500 };
                form.Children.Add(ProfileText("Wähle, auf welchen Geräten eingehende Anrufe klingeln. Dein Profil gilt auf allen Fonoo-Geräten dieser Firma."));
                if (current.ProfilesAvailable != true) form.Children.Add(ProfileText("Der Telefonieserver hat die Profilsteuerung noch nicht bestätigt."));
                var feedback = ProfileText("");
                var dialog = ProfileDialog("Deine Anrufprofile", form); dialog.CloseButtonText = "Fertig";
                dialog.Closing += (_, args) => { if (profileBusy && ProfileContextCurrent(context)) args.Cancel = true; };
                void FillRows()
                {
                    form.Children.Clear();
                    form.Children.Add(ProfileText("Wähle, auf welchen Geräten eingehende Anrufe klingeln. Dein Profil gilt auf allen Fonoo-Geräten dieser Firma."));
                    if (current.ProfilesAvailable != true) form.Children.Add(ProfileText("Der Telefonieserver hat die Profilsteuerung noch nicht bestätigt."));
                    foreach (var profile in current.Settings.Profiles)
                    {
                        var active = profile.Id == current.Settings.ActiveProfile.Id;
                        var devices = current.OwnDevices.Where(d => profile.DeviceIds?.Contains(d.Id) ?? true).ToArray();
                        var row = new StackPanel { Spacing = 8 };
                        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                        var activate = new Button { Content = (active ? "✓ " : "") + profile.Name, IsEnabled = !active };
                        AutomationProperties.SetName(activate, profile.Name + (active ? ", aktiv" : ", aktivieren"));
                        activate.Click += async (_, _) =>
                        {
                            if (profileBusy) return; profileBusy = true; ((ScrollViewer)dialog.Content).IsEnabled = false;
                            try { current = await WriteProfilesAsync(context, current, DeviceProfileChanges.Select(current, profile.Id)); FillRows(); }
                            catch (OperationCanceledException) when (!ProfileContextCurrent(context)) { dialog.Hide(); }
                            catch (Exception ex) { feedback.Text = ProfileError(ex, context); }
                            finally { profileBusy = false; ((ScrollViewer)dialog.Content).IsEnabled = true; RenderProfileButton(); }
                        };
                        choices.Children.Add(activate);
                        if (!profile.IsStandard)
                        {
                            var edit = new Button { Content = "Bearbeiten" }; AutomationProperties.SetName(edit, "Profil " + profile.Name + " bearbeiten");
                            edit.Click += (_, _) => { editing = profile; dialog.Hide(); }; choices.Children.Add(edit);
                        }
                        row.Children.Add(choices);
                        row.Children.Add(ProfileText(profile.IsStandard ? "Alle eigenen Geräte · auch neue Geräte" : devices.Length + " Geräte ausgewählt"));
                        row.Children.Add(ProfileText(devices.Length > 0 ? string.Join(" · ", devices.Select(d => d.Name + (d.Enabled == false ? " (deaktiviert)" : ""))) : "Keine Geräte ausgewählt – eingehende Anrufe klingeln nicht."));
                        if (profile.IsStandard) row.Children.Add(ProfileText("Deaktivierte oder abgemeldete Geräte bleiben ausgeschlossen."));
                        form.Children.Add(new Border { Child = row, Padding = new Thickness(14), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(active ? 2 : 1),
                            BorderBrush = new SolidColorBrush(active ? Microsoft.UI.ColorHelper.FromArgb(255, 110, 74, 217) : Microsoft.UI.ColorHelper.FromArgb(255, 232, 228, 238)) });
                    }
                    var add = new Button { Content = "Profil anlegen", IsEnabled = current.Settings.Profiles.Length < 20, Style = SendCodeButton.Style };
                    add.Click += (_, _) => { isNew = true; editing = new() { Id = "p" + Guid.NewGuid().ToString("N"), DeviceIds = current.OwnDevices.Select(d => d.Id).ToArray() }; dialog.Hide(); };
                    form.Children.Add(add);
                    var refresh = new Button { Content = "Aktualisieren" };
                    refresh.Click += async (_, _) =>
                    {
                        if (profileBusy) return; profileBusy = true; ((ScrollViewer)dialog.Content).IsEnabled = false;
                        try { current = await ReadProfilesAsync(context); FillRows(); }
                        catch (OperationCanceledException) when (!ProfileContextCurrent(context)) { dialog.Hide(); }
                        catch (Exception ex) { feedback.Text = ProfileError(ex, context); }
                        finally { profileBusy = false; ((ScrollViewer)dialog.Content).IsEnabled = true; RenderProfileButton(); }
                    };
                    form.Children.Add(refresh);
                    var status = new Button { Content = "Status, Arbeitszeiten & Rufteams", IsEnabled = !previewMode };
                    status.Click += (_, _) => { advanced = true; dialog.Hide(); }; form.Children.Add(status);
                    feedback.Text = ""; form.Children.Add(feedback);
                }
                FillRows(); await dialog.ShowAsync();
                if (!ProfileContextCurrent(context) || advanced || editing is null) break;
                if (!editing.IsStandard) await EditProfileAsync(context, current, editing, isNew);
            }
        }
        catch (OperationCanceledException) when (!ProfileContextCurrent(context)) { }
        catch (Exception ex) { if (ProfileScopeCurrent(context)) ShowNotice(ProfileError(ex, context)); }
        finally { profileBusy = false; modalOpen = false; RenderProfileButton(); }
        if (advanced && ProfileContextCurrent(context)) AvailabilityClicked(sender, e);
    }
    private async Task EditProfileAsync(ProfileContext context, AvailabilitySnapshot current, DeviceProfile profile, bool isNew)
    {
        var form = new StackPanel { Spacing = 14, MinWidth = 350, MaxWidth = 500 };
        var name = new TextBox { Header = "Profilname", PlaceholderText = "Zum Beispiel Büro oder Unterwegs", Text = profile.Name, MaxLength = 100 };
        form.Children.Add(name); form.Children.Add(ProfileText("Hier sollen Anrufe klingeln", true));
        var selected = new Dictionary<string, CheckBox>();
        foreach (var device in current.OwnDevices)
        {
            var box = new CheckBox { Content = device.Name + (device.Enabled == false ? " · Deaktiviert" : ""), IsChecked = profile.DeviceIds?.Contains(device.Id) == true };
            selected[device.Id] = box; form.Children.Add(box);
        }
        if (selected.Count == 0) form.Children.Add(ProfileText("Deinem Konto sind noch keine eigenen Geräte zugewiesen."));
        form.Children.Add(ProfileText("Neue Geräte sind im Profil Standard automatisch enthalten. In eigenen Profilen wählst du sie bei Bedarf dazu."));
        var empty = ProfileText("Ohne ausgewählte Geräte klingeln keine eingehenden Anrufe."); form.Children.Add(empty);
        void RefreshEmpty() => empty.Visibility = selected.Values.Any(b => b.IsChecked == true) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var box in selected.Values) { box.Checked += (_, _) => RefreshEmpty(); box.Unchecked += (_, _) => RefreshEmpty(); } RefreshEmpty();
        var feedback = ProfileText(""); form.Children.Add(feedback);
        var dialog = ProfileDialog(isNew ? "Neues Anrufprofil" : "Profil bearbeiten", form);
        dialog.PrimaryButtonText = "Speichern"; dialog.CloseButtonText = "Zurück";
        if (!isNew) dialog.SecondaryButtonText = "Profil löschen";
        dialog.Closing += (_, args) => { if (profileBusy && ProfileContextCurrent(context)) args.Cancel = true; };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (profileBusy) { args.Cancel = true; return; }
            var deferral = args.GetDeferral(); profileBusy = true; dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false; ((ScrollViewer)dialog.Content).IsEnabled = false;
            try
            {
                var edited = new DeviceProfile { Id = profile.Id, Name = name.Text, DeviceIds = selected.Where(p => p.Value.IsChecked == true).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray() };
                await WriteProfilesAsync(context, current, DeviceProfileChanges.Upsert(current, edited, isNew));
            }
            catch (OperationCanceledException) when (!ProfileContextCurrent(context)) { }
            catch (Exception ex) { args.Cancel = true; feedback.Text = ProfileError(ex, context) + " Zurück zur Übersicht kannst du die Profile neu laden."; }
            finally { profileBusy = false; dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = true; ((ScrollViewer)dialog.Content).IsEnabled = true; RenderProfileButton(); deferral.Complete(); }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Secondary || !ProfileContextCurrent(context)) return;
        var confirm = NewDialog("Profil „" + profile.Name + "“ löschen?", "Wenn dieses Profil aktiv ist, wechselt Fonoo auf Standard mit allen eigenen Geräten.", "Löschen");
        confirm.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); profileBusy = true; confirm.IsPrimaryButtonEnabled = false;
            try { await WriteProfilesAsync(context, current, DeviceProfileChanges.Delete(current, profile.Id)); }
            catch (OperationCanceledException) when (!ProfileContextCurrent(context)) { }
            catch (Exception ex) { args.Cancel = true; confirm.Content = ProfileText(ProfileError(ex, context) + " Bitte abbrechen und die Profile neu laden."); }
            finally { profileBusy = false; confirm.IsPrimaryButtonEnabled = true; RenderProfileButton(); deferral.Complete(); }
        };
        confirm.Closing += (_, args) => { if (profileBusy && ProfileContextCurrent(context)) args.Cancel = true; };
        await confirm.ShowAsync();
    }
}
