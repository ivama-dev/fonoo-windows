using Fonoo.Windows.Desktop;
using Fonoo.Windows.Telephony;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Windows.Networking.Connectivity;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private TrayHost? tray;
    private MiniCallWindow? miniCall;
    private bool miniDismissed;
    private DispatcherQueueTimer? activityTimer;
    private bool activityBusy;
    private DateTimeOffset lastTrayActivity;
    private string durationText = "";
    private bool reconnectAfterCall;
    private bool quitDialogOpen;
    private bool manualDoNotDisturb;
    private readonly double[] activityLevels = new double[5];
    private string activityCallId = "";

    private void InitializeDesktop()
    {
        Closed += (_, _) =>
        {
            NetworkInformation.NetworkStatusChanged -= NetworkStatusChanged;
            activityTimer?.Stop(); availabilityTimer?.Stop(); tray?.Dispose(); miniCall?.DisposeWindow();
        };
        if (previewMode) return;
        try
        {
            tray = new(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonoo.ico"));
            tray.Command += DesktopCommand;
            tray.Resumed += ScheduleRegistrationRefresh;
            tray.Unavailable += () => DispatcherQueue.TryEnqueue(() => { ShowMain(); ShowNotice("Das Symbol im Infobereich ist nicht verfügbar. Fonoo bleibt im Fenster geöffnet."); });
        }
        catch { ShowNotice("Das Symbol im Infobereich konnte nicht eingerichtet werden. Fonoo bleibt im Fenster geöffnet."); }
        AppWindow.Closing += (ownerWindow, e) =>
        {
            if (closingWithEngine) return;
            e.Cancel = true;
            if (tray?.Available == true) { AppWindow.Hide(); ShowMiniCall(false); }
            else _ = QuitAsync();
        };
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidPresenterChange || e.DidVisibilityChange || e.DidSizeChange)
            {
                var minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
                if (AppWindow.IsVisible && !minimized) miniCall?.AppWindow.Hide();
                else ShowMiniCall(false);
                RefreshActivityCadence();
                RefreshPresenceLifecycle();
            }
        };
        NetworkInformation.NetworkStatusChanged += NetworkStatusChanged;
        activityTimer = DispatcherQueue.CreateTimer(); activityTimer.Interval = TimeSpan.FromMilliseconds(80);
        activityTimer.Tick += ActivityTick;
    }
    public void ShowMain()
    {
        if (closed) return;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        AppWindow.Show(); Activate(); miniCall?.AppWindow.Hide();
    }
    private void ShowMiniCall(bool explicitRequest)
    {
        if (!snapshot.InCall || (miniDismissed && !explicitRequest)) return;
        if (miniCall is null)
        {
            miniCall = new(DesktopCommand); miniCall.Dismissed += () => { miniDismissed = true; RefreshActivityCadence(); };
        }
        miniDismissed = false; miniCall.Update(DisplaySnapshot, callActionPending); miniCall.SetActivity(durationText, activityLevels); miniCall.ShowWithoutFocus(); RefreshActivityCadence();
    }
    private void UpdateDesktop(bool wasInCall)
    {
        tray?.Update(DisplaySnapshot, durationText); miniCall?.Update(DisplaySnapshot, callActionPending);
        if (snapshot.CallId != activityCallId) { activityCallId = snapshot.CallId; Array.Clear(activityLevels); durationText = ""; CallDuration.Text = ""; }
        if (snapshot.InCall && !wasInCall)
        {
            miniDismissed = false; durationText = ""; CallDuration.Text = ""; activityTimer?.Start();
            if (!AppWindow.IsVisible || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) ShowMiniCall(false);
            if (snapshot.Incoming) TrayHost.RequestAttention(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        else if (!snapshot.InCall && wasInCall)
        {
            activityTimer?.Stop(); miniCall?.AppWindow.Hide(); miniDismissed = false; durationText = "";
            if (reconnectAfterCall) { reconnectAfterCall = false; ScheduleRegistrationRefresh(); }
            if (refreshAccountAfterCall) { refreshAccountAfterCall = false; _ = RunAsync(LoadAccountAsync); }
        }
    }
    private void RefreshActivityCadence()
    {
        if (activityTimer is null) return;
        var mainVisible = AppWindow.IsVisible && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized } && CallPanel.Visibility == Visibility.Visible;
        activityTimer.Interval = TimeSpan.FromMilliseconds(mainVisible || miniCall?.AppWindow.IsVisible == true ? 40 : 1000);
    }
    private async void ActivityTick(DispatcherQueueTimer sender, object args)
    {
        if (activityBusy || !snapshot.InCall || engine is not { } current) return;
        activityBusy = true;
        try
        {
            var activity = await current.GetActivityAsync();
            if (closed || !ReferenceEquals(current, engine) || !snapshot.InCall || activity.CallId != snapshot.CallId) return;
            var seconds = activity.ConnectedAt is { } at ? Math.Max(0, (DateTimeOffset.UtcNow - at).TotalSeconds) : 0;
            durationText = activity.ConnectedAt is null ? "" : TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
            if (snapshot.Active) { Array.Copy(activityLevels, 1, activityLevels, 0, 4); activityLevels[4] = activity.Level; } else Array.Clear(activityLevels);
            CallDuration.Text = durationText;
            if (miniCall?.AppWindow.IsVisible == true) miniCall.SetActivity(durationText, activityLevels);
            for (var i = 0; i < 5; i++) ((Rectangle)CallWave.Children[i]).Height = 4 + 20 * activityLevels[i];
            if ((DateTimeOffset.UtcNow - lastTrayActivity).TotalSeconds >= 1) { lastTrayActivity = DateTimeOffset.UtcNow; tray?.Update(DisplaySnapshot, durationText); }
        }
        catch { /* A queued read can be cancelled by logout or shutdown. */ }
        finally { activityBusy = false; }
    }
    private async void DesktopCommand(int command)
    {
        switch (command)
        {
            case 1: ShowMain(); if (snapshot.InCall) DesktopNavigation.SelectedItem = ConversationTab; break;
            case 2: ShowMiniCall(true); break;
            case 3: await PhoneActionAsync(e => e.AnswerAsync()); break;
            case 4: await PhoneActionAsync(e => e.MuteAsync(!snapshot.Muted)); break;
            case 5: await PhoneActionAsync(e => e.HoldAsync(!snapshot.Held)); break;
            case 6: await PhoneActionAsync(e => e.HangUpAsync()); break;
            case 7: await SetLocalDndAsync(!snapshot.DoNotDisturb); break;
            case 8: await QuitAsync(); break;
            case 9: ShowMain(); DesktopNavigation.SelectedItem = DialpadTab; break;
            case 10: ShowMain(); DesktopNavigation.SelectedItem = ConversationTab; TransferClicked(this, new RoutedEventArgs()); break;
            case 11: await PhoneActionAsync(e => e.CompleteTransferAsync()); break;
            case 12: await PhoneActionAsync(e => e.ReturnToOriginalAsync()); break;
        }
    }
    private void NetworkStatusChanged(object sender) => DispatcherQueue.TryEnqueue(ScheduleRegistrationRefresh);
    private void ScheduleRegistrationRefresh()
    {
        if (closed || closingWithEngine || engine is null || configuration is null) return;
        if (snapshot.InCall) { reconnectAfterCall = true; return; }
        _ = RefreshRegistrationSafelyAsync();
    }
    private async Task RefreshRegistrationSafelyAsync()
    {
        try { if (engine is { } current) await current.RefreshRegistrationAsync(); }
        catch { if (!closed) ShowNotice("Telefonie konnte nach dem Netzwechsel nicht erneut verbunden werden. Bitte erneut verbinden."); }
    }
    private async Task SetLocalDndAsync(bool value)
    {
        if (engine is null) { ShowNotice("Bitte verbinde zuerst deine Nebenstelle.", InfoBarSeverity.Informational); return; }
        manualDoNotDisturb = value;
        var companyDnd = availability?.Company.RoutingEnabled == true && availability.Effective.Presence?.State == "do_not_disturb";
        await PhoneActionAsync(e => e.SetDoNotDisturbAsync(value || companyDnd));
        if (!value && companyDnd) ShowNotice("In deiner Firmenverfügbarkeit ist „Nicht stören“ aktiv. Du kannst den Status unter Team → Meine Verfügbarkeit ändern.", InfoBarSeverity.Informational);
    }
    private async void DndClicked(object sender, RoutedEventArgs e) => await SetLocalDndAsync(!snapshot.DoNotDisturb);
    private async void QuitClicked(object sender, RoutedEventArgs e) => await QuitAsync();
    private async Task QuitAsync()
    {
        if (closingWithEngine || quitDialogOpen) return;
        if (audioDialogOpen || modalOpen) { ShowMain(); return; }
        if (snapshot.InCall)
        {
            quitDialogOpen = true; ShowMain();
            try
            {
                var dialog = new ContentDialog { XamlRoot = ((FrameworkElement)Content).XamlRoot, RequestedTheme = ElementTheme.Light, Title = "Gespräch beenden und Fonoo schließen?",
                    Content = "Dein laufendes Gespräch wird beendet. Danach bist du auf diesem PC nicht mehr erreichbar.", PrimaryButtonText = "Beenden", CloseButtonText = "Weiter telefonieren" };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            finally { quitDialogOpen = false; }
        }
        closingWithEngine = true; StopPresence(); StopHistorySync(); operation?.Cancel(); Actions.IsEnabled = false; activityTimer?.Stop(); availabilityTimer?.Stop();
        try { await StopSipAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        Close();
    }
}
