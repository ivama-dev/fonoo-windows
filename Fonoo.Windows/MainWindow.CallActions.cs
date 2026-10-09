using Fonoo.Windows.Telephony;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private void PreviewCallClicked(object sender, RoutedEventArgs e)
    {
#if FONOO_DESIGN_PREVIEW
        UpdatePhone(new(false, true, false, false, "Gesprächsansicht · Designvorschau", "Alex Beispiel", Active: true));
        CallDuration.Text = "1:34";
        durationText = "1:34";
#endif
    }
    private void PreviewMiniClicked(object sender, RoutedEventArgs e)
    {
#if FONOO_DESIGN_PREVIEW
        if (!snapshot.InCall) PreviewCallClicked(sender, e);
        if (miniCall is null)
        {
            miniCall = new(PreviewMiniCommand);
            miniCall.Dismissed += () => miniDismissed = true;
        }
        if (previewMiniTimer is null)
        {
            previewMiniTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            previewMiniTimer.Tick += (_, _) =>
            {
                if (!snapshot.InCall || miniCall?.AppWindow.IsVisible != true) return;
                var level = snapshot.Active ? .48 + .16 * Math.Sin(Environment.TickCount64 / 210d) : 0;
                miniCall.SetActivity("1:34", [level * .8, level * .9, level, level + .05, level]);
            };
            Closed += (_, _) => previewMiniTimer.Stop();
        }
        previewMiniTimer.Start();
        ShowMiniCall(true);
        if (miniCall is not null) miniCall.Title = "Fonoo · Mini-Designvorschau";
#endif
    }
#if FONOO_DESIGN_PREVIEW
    private Microsoft.UI.Xaml.DispatcherTimer? previewMiniTimer;
    private void PreviewMiniCommand(int command)
    {
        // Only this explicitly compiled preview simulates actions and audio feedback.
        switch (command)
        {
            case 1: ShowMain(); DesktopNavigation.SelectedItem = ConversationTab; break;
            case 4: UpdatePhone(snapshot with { Muted = !snapshot.Muted }); break;
            case 5: UpdatePhone(snapshot with { Held = !snapshot.Held, Active = snapshot.Held, Status = snapshot.Held ? "Gesprächsansicht · Designvorschau" : "Gehalten · Designvorschau" }); break;
            case 6: UpdatePhone(new(false, false, false, false, "Designvorschau · nicht verbunden", "")); break;
            case 9: ShowMain(); DesktopNavigation.SelectedItem = DialpadTab; break;
            case 10: UpdatePhone(snapshot with { Consulting = true, ConsultationActive = true, Held = true, Active = true, Peer = "Mara Beispiel", OriginalPeer = "Alex Beispiel", Status = "Rückfrage · Designvorschau" }); break;
            case 11: case 12: UpdatePhone(snapshot with { Consulting = false, ConsultationActive = false, Held = false, Active = true, Peer = "Alex Beispiel", OriginalPeer = "", Status = "Gesprächsansicht · Designvorschau" }); break;
        }
    }
#endif
    private async void HoldClicked(object sender, RoutedEventArgs e) => await PhoneActionAsync(e => e.HoldAsync(!snapshot.Held));
    private async void CompleteTransferClicked(object sender, RoutedEventArgs e) => await PhoneActionAsync(e => e.CompleteTransferAsync());
    private async void ReturnOriginalClicked(object sender, RoutedEventArgs e) => await PhoneActionAsync(e => e.ReturnToOriginalAsync());
    private async void TransferClicked(object sender, RoutedEventArgs e)
    {
        if (modalOpen || audioDialogOpen || !snapshot.CanTransfer) return;
        modalOpen = true;
        try
        {
            var number = new TextBox { Header = "Zielrufnummer oder Nebenstelle", MaxLength = 64, PlaceholderText = "z. B. 101", InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.TelephoneNumber) } } };
            var options = new ComboBox { Header = "Weiterleitung", ItemsSource = new[] { "Zuerst Rückfrage halten", "Direkt weiterleiten" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var form = new StackPanel { Spacing = 14 }; form.Children.Add(number); form.Children.Add(options); form.Children.Add(error);
            var dialog = NewDialog("Gespräch weiterleiten", form, "Starten");
            dialog.PrimaryButtonClick += (_, args) => { if (DialNumber.Normalize(number.Text) is null) { args.Cancel = true; error.Text = "Bitte eine gültige Rufnummer eingeben."; } };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && snapshot.CanTransfer && DialNumber.Normalize(number.Text) is { } normalized)
                await PhoneActionAsync(e => e.TransferAsync(normalized, options.SelectedIndex == 0));
        }
        finally { modalOpen = false; }
    }
}
