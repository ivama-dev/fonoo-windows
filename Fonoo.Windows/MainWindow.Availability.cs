using Fonoo.Windows.Accounts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private async void AvailabilityClicked(object sender, RoutedEventArgs e)
    {
        if (modalOpen || audioDialogOpen || operation is not null || previewMode) return;
        await RunAsync(async ct => availability = await account.GetAvailabilityAsync(activeTenant, accountId, ct));
        if (availability is not { } current || !account.SignedIn) return;
        modalOpen = true;
        try
        {
            var form = new StackPanel { Spacing = 16 };
            var reason = new TextBlock { Text = current.Effective.ReasonText + (current.Effective.NextAvailableAt is { } next ? "\nWieder verfügbar: " + next : ""), TextWrapping = TextWrapping.Wrap };
            form.Children.Add(reason);
            if (!current.Company.RoutingEnabled) form.Children.Add(new TextBlock { Text = "Status, Arbeitszeiten und Rufteams benötigen die Freigabe deiner Administration. Deine Anrufprofile funktionieren unabhängig davon.", TextWrapping = TextWrapping.Wrap });
            var statusOptions = PersonalAvailability.States.ToArray();
            var status = new ComboBox { Header = "Verfügbarkeit", ItemsSource = statusOptions.Select(p => p.Value).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
            status.SelectedIndex = Math.Max(0, Array.FindIndex(statusOptions, p => p.Key == (current.Settings.Presence?.State ?? "available")));
            form.Children.Add(status);
            var description = new TextBox { Header = "Statustext", MaxLength = 160, Text = current.Settings.Presence?.Description ?? "" }; form.Children.Add(description);
            var expiry = new ComboBox { Header = "Gültig", ItemsSource = new[] { "30 Minuten", "1 Stunde", "Bis morgen", "Bis zur nächsten Änderung" }, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch }; form.Children.Add(expiry);
            var automatic = new CheckBox { Content = "Verfügbarkeit automatisch nach Zeitplan", IsChecked = current.Settings.Presence is null }; form.Children.Add(automatic);
            automatic.Checked += (_, _) => status.IsEnabled = description.IsEnabled = expiry.IsEnabled = false;
            automatic.Unchecked += (_, _) => status.IsEnabled = description.IsEnabled = expiry.IsEnabled = true;
            status.IsEnabled = description.IsEnabled = expiry.IsEnabled = automatic.IsChecked != true;
            if (current.Effective.EffectiveDevices is { Length: > 0 } effective)
                form.Children.Add(new TextBlock { Text = "Anrufe klingeln auf: " + string.Join(", ", effective.Select(d => d.Name)), TextWrapping = TextWrapping.Wrap });
            var schedule = current.Schedules.FirstOrDefault(s => s.Id == current.Settings.ScheduleId);
            form.Children.Add(new TextBlock { Text = "Arbeitszeit: " + (schedule?.Name ?? "Kein persönlicher Zeitplan hinterlegt"), TextWrapping = TextWrapping.Wrap });
            var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap }; form.Children.Add(feedback);
            var busy = false;
            foreach (var team in current.CallTeams)
            {
                var member = team.Members.FirstOrDefault(m => m.TargetType == "user" && m.TargetId == current.SelfUserId);
                if (member is null) continue;
                var group = new StackPanel { Spacing = 8 };
                group.Children.Add(new TextBlock { Text = team.Name + " · " + team.Number, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                var teamReason = new TextBlock { Text = member.Effective.ReasonText, TextWrapping = TextWrapping.Wrap }; group.Children.Add(teamReason);
                if (team.AllowSelfPause)
                {
                    var pause = new Button { Content = "Für eine Stunde pausieren" }; var resume = new Button { Content = "Pause beenden" };
                    group.Children.Add(pause); group.Children.Add(resume);
                    pause.Click += async (_, _) => await ChangePauseAsync(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
                    resume.Click += async (_, _) => await ChangePauseAsync(null);
                    async Task ChangePauseAsync(long? until)
                    {
                        if (busy) return; busy = true; pause.IsEnabled = resume.IsEnabled = false;
                        try
                        {
                            var latestTeam = current.CallTeams.Single(t => t.Id == team.Id);
                            await account.PauseTeamAsync(current, latestTeam, until, CancellationToken.None);
                            current = await account.GetAvailabilityAsync(activeTenant, accountId, CancellationToken.None); availability = current;
                            teamReason.Text = current.CallTeams.Single(t => t.Id == team.Id).Members.Single(m => m.TargetType == "user" && m.TargetId == current.SelfUserId).Effective.ReasonText;
                            feedback.Text = "Rufteam aktualisiert.";
                        }
                        catch (AccountException ex) { feedback.Text = ex.Message; }
                        catch { feedback.Text = "Das Rufteam konnte nicht geändert werden. Bitte erneut laden."; }
                        finally { busy = false; pause.IsEnabled = resume.IsEnabled = true; }
                    }
                }
                form.Children.Add(group);
            }
            form.Children.Add(new HyperlinkButton { Content = "Zeitpläne, Rufteams und Geräte verwalten", NavigateUri = new Uri("https://dev.fonoo.app/kunden/?tenant_id=" + Uri.EscapeDataString(activeTenant) + "&section=availability") });
            var dialog = NewDialog("Status, Arbeitszeiten & Rufteams", new ScrollViewer { Content = form, MaxHeight = 480, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                if (busy) { args.Cancel = true; return; }
                var deferral = args.GetDeferral(); busy = true; dialog.IsPrimaryButtonEnabled = false;
                try
                {
                    var seconds = new[] { 1800, 3600, 86400, 0 }[expiry.SelectedIndex];
                    long? until = seconds == 0 ? null : DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds();
                    var state = statusOptions[status.SelectedIndex].Key;
                    object? presence = automatic.IsChecked == true ? null : new { state, description = description.Text.Trim(), valid_until = until };
                    await account.SaveAvailabilityAsync(current, new { presence, override_schedule_until = automatic.IsChecked != true && state == "available" ? until : null }, CancellationToken.None);
                    await ApplyAvailabilityAsync(await account.GetAvailabilityAsync(activeTenant, accountId, CancellationToken.None));
                }
                catch (AccountException ex) { args.Cancel = true; feedback.Text = ex.Message; }
                catch { args.Cancel = true; feedback.Text = "Die Einstellungen konnten nicht gespeichert werden. Bitte erneut laden."; }
                finally { busy = false; dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
            };
            await dialog.ShowAsync();
        }
        finally { modalOpen = false; }
        if (!account.SignedIn) ResetSession();
    }
    private async void SetPasswordClicked(object sender, RoutedEventArgs e)
    {
        if (modalOpen || audioDialogOpen || !account.SignedIn) return;
        modalOpen = true;
        try
        {
            var password = new PasswordBox { Header = "Neues Passwort", HorizontalAlignment = HorizontalAlignment.Stretch };
            var confirmation = new PasswordBox { Header = "Passwort wiederholen", HorizontalAlignment = HorizontalAlignment.Stretch };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var form = new StackPanel { Spacing = 12 }; form.Children.Add(password); form.Children.Add(confirmation); form.Children.Add(error);
            var dialog = NewDialog("Dein Konto-Passwort", form);
            dialog.PrimaryButtonClick += (_, args) =>
            { if (password.Password.Length < 20 || System.Text.Encoding.UTF8.GetByteCount(password.Password) > 256 || password.Password != confirmation.Password) { args.Cancel = true; error.Text = "Bitte ein langes Passwort mit mindestens 20 Zeichen eingeben und identisch wiederholen (maximal 256 UTF-8-Bytes)."; } };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await RunAsync(async ct => { try { await account.SetPasswordAsync(password.Password, ct); ShowNotice("Dein Passwort wurde aktualisiert.", InfoBarSeverity.Success); } finally { password.Password = confirmation.Password = ""; } });
            }
            password.Password = confirmation.Password = "";
        }
        finally { modalOpen = false; }
    }
}
