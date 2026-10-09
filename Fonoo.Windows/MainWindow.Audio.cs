using Fonoo.Windows.Telephony;
using Fonoo.Windows.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Fonoo.Windows;

public sealed partial class MainWindow
{
    private bool audioDialogOpen;
    private MicrophoneMeter? microphoneMeter;
    private SpeakerTest? speakerTest;
    private int microphoneMeterGeneration;
    private int speakerTestGeneration;
    private void StopMicrophoneMeter() { microphoneMeterGeneration++; microphoneMeter?.Dispose(); microphoneMeter = null; }
    private void StopSpeakerTest() { speakerTestGeneration++; speakerTest?.Dispose(); speakerTest = null; }

    private ContentDialog CreateAudioDialog(AudioSettingsView view)
    {
        view.MaxHeight = Math.Max(180, Math.Min(570, ((FrameworkElement)Content).ActualHeight - 170));
        var dialog = new ContentDialog { XamlRoot = ((FrameworkElement)Content).XamlRoot, Title = "Audio einrichten", RequestedTheme = ((FrameworkElement)Content).RequestedTheme, Content = view, CloseButtonText = "Fertig" };
        dialog.Resources["ContentDialogMaxWidth"] = 570d;
        return dialog;
    }

    private async void AudioClicked(object sender, RoutedEventArgs e)
    {
        if (audioDialogOpen || modalOpen || quitDialogOpen || operation is not null || closingWithEngine) return;
#if FONOO_DESIGN_PREVIEW
        if (previewMode) { await ShowAudioPreviewAsync(); return; }
#endif
        audioDialogOpen = true;
        var localOnly = engine is null;
        SipEngine? current = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var view = new AudioSettingsView();
        var dialog = CreateAudioDialog(view);
        var dismissed = false;
        var fetching = false;
        var commandRunning = false;
        var updating = false;
        var active = true;
        var testing = false;
        var meterStarting = false;
        var uiError = "";
        var success = "";
        string? failedMeterInput = null;
        long echoUntil = 0;
        AudioSnapshot? lastState = null;
        var reloadRequested = false;

        void Render(AudioSnapshot state)
        {
            if (speakerTest?.Completed == true)
            {
                if (speakerTest.Failed) uiError = "Der Testton wurde unterbrochen. Prüfe die Verbindung deines Ausgabegeräts.";
                else success = "Testton beendet. Wenn du ihn gehört hast, ist deine Ausgabe bereit.";
                StopSpeakerTest();
            }
            testing = microphoneMeter?.Monitoring == true;
            if (!testing) echoUntil = 0;
            var mode = !active ? AudioMeterMode.Paused
                : state.InCall ? state.Muted ? AudioMeterMode.Muted : AudioMeterMode.Call
                : microphoneMeter is { Healthy: true } ? AudioMeterMode.Local
                : meterStarting ? AudioMeterMode.Starting : AudioMeterMode.Unavailable;
            view.SetMeter(state.InCall ? state.Level : microphoneMeter?.Level ?? 0,
                state.InCall ? state.Level : microphoneMeter?.PeakLevel ?? 0, mode);
            view.SetTests(testing, (int)Math.Clamp(Math.Ceiling((echoUntil - Environment.TickCount64) / 1000d), 0, 15),
                speakerTest is not null, state.InCall, commandRunning, state.Inputs.Length > 0, state.Outputs.Length > 0, active);
            var notice = uiError.Length > 0 ? uiError : state.Notice.Length > 0 ? state.Notice : success;
            view.SetNotice(notice, uiError.Length > 0 ? InfoBarSeverity.Error : state.Notice.Length > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
        }

        async Task RefreshAsync(bool reload = false)
        {
            reloadRequested |= reload;
            if (fetching || commandRunning || dismissed || current is null) return;
            fetching = true;
            try
            {
                var reloadDevices = reloadRequested; reloadRequested = false;
                var state = await current.GetAudioAsync(reloadDevices);
                if (dismissed || commandRunning || closed || !ReferenceEquals(current, engine)) return;
                if (snapshot.Incoming) { StopMicrophoneMeter(); StopSpeakerTest(); dialog.Hide(); return; }
                updating = true;
                try { SetOptions(view.Inputs, state.Inputs, state.InputId); SetOptions(view.Outputs, state.Outputs, state.OutputId); }
                finally { updating = false; }
                lastState = state;
                if (state.InCall) StopSpeakerTest();
                if (microphoneMeter?.Completed == true) StopMicrophoneMeter();
                if (state.InCall || state.Testing || !active) StopMicrophoneMeter();
                else if (!meterStarting && state.InputId is { } id && failedMeterInput != id && microphoneMeter?.InputId != id)
                {
                    StopMicrophoneMeter(); meterStarting = true;
                    Render(state);
                    var generation = microphoneMeterGeneration;
                    try
                    {
                        var created = await MicrophoneMeter.StartAsync(id, state.Inputs.First(d => d.Id == id).Name);
                        if (generation != microphoneMeterGeneration || dismissed || commandRunning || snapshot.InCall || closed || !active) created.Dispose();
                        else microphoneMeter = created;
                    }
                    catch { failedMeterInput = id; }
                    finally { meterStarting = false; }
                }
                if (!dismissed && !closed && !commandRunning) Render(state);
            }
            catch
            {
                if (!dismissed)
                {
                    StopMicrophoneMeter(); StopSpeakerTest();
                    view.SetMeter(0, 0, AudioMeterMode.Unavailable);
                    view.SetTests(false, 0, false, snapshot.InCall, false, false, false, active);
                    view.SetNotice("Audio ist gerade nicht verfügbar. Aktualisiere die Geräte oder öffne den Dialog erneut.", InfoBarSeverity.Error);
                }
            }
            finally { updating = false; fetching = false; }
        }

        async Task RunCommandAsync(Func<Task> action)
        {
            if (commandRunning || dismissed || current is null) return;
            commandRunning = true;
            uiError = success = "";
            if (lastState is { } state) Render(state);
            try { await action(); }
            catch { uiError = "Audio konnte nicht gestartet oder geändert werden. Prüfe die gewählten Geräte und aktualisiere die Liste."; }
            finally { commandRunning = false; if (!dismissed) await RefreshAsync(); }
        }

        async Task SelectAsync(ComboBox box, bool input)
        {
            if (updating || box.SelectedValue is not string id || current is null) return;
            await RunCommandAsync(async () =>
            {
                StopMicrophoneMeter(); StopSpeakerTest(); failedMeterInput = null;
                await current.SelectAudioAsync(input, id);
            });
        }

        void OnActivation(object sender, WindowActivatedEventArgs args)
        {
            if (dismissed || current is null) return;
            active = args.WindowActivationState != WindowActivationState.Deactivated;
            if (active) { failedMeterInput = null; timer.Start(); return; }
            timer.Stop(); StopMicrophoneMeter(); StopSpeakerTest();
            if (lastState is { } state) Render(state);
        }

        try
        {
            if (localOnly)
            {
                await engineShutdown;
                if (closed) return;
                engine = new SipEngine(null);
            }
            current = engine!;
            await current.Ready;
            view.Inputs.SelectionChanged += async (_, _) => await SelectAsync(view.Inputs, true);
            view.Outputs.SelectionChanged += async (_, _) => await SelectAsync(view.Outputs, false);
            view.EchoTest.Click += async (_, _) => await RunCommandAsync(async () =>
            {
                var start = microphoneMeter?.Monitoring != true;
                StopMicrophoneMeter(); StopSpeakerTest();
                if (!start) return;
                var input = lastState?.Inputs.FirstOrDefault(d => d.Id == lastState.InputId) ?? throw new InvalidOperationException();
                var output = lastState?.Outputs.FirstOrDefault(d => d.Id == lastState.OutputId) ?? throw new InvalidOperationException();
                var generation = microphoneMeterGeneration;
                var created = await MicrophoneMeter.StartAsync(input.Id, input.Name, output.Id, output.Name);
                if (generation != microphoneMeterGeneration || dismissed || !active || snapshot.InCall || closed) created.Dispose();
                else { microphoneMeter = created; echoUntil = created.MonitorUntil; }
            });
            view.SpeakerTest.Click += async (_, _) => await RunCommandAsync(async () =>
            {
                if (speakerTest is not null) { StopSpeakerTest(); return; }
                var output = lastState?.Outputs.FirstOrDefault(d => d.Id == lastState.OutputId)
                    ?? throw new InvalidOperationException();
                StopSpeakerTest();
                var generation = speakerTestGeneration;
                var created = await SpeakerTest.StartAsync(output.Id, output.Name);
                if (generation != speakerTestGeneration || dismissed || !active || snapshot.InCall || closed) created.Dispose();
                else speakerTest = created;
            });
            view.Refresh.Click += async (_, _) =>
            {
                if (commandRunning) return;
                StopMicrophoneMeter(); StopSpeakerTest(); failedMeterInput = null; uiError = success = "";
                await RefreshAsync(true);
            };
            view.WindowsSettings.Click += async (_, _) =>
            {
                try
                {
                    if (!await Launcher.LaunchUriAsync(new Uri("ms-settings:sound")))
                        view.SetNotice("Öffne in Windows die Einstellungen unter System → Sound.");
                }
                catch { view.SetNotice("Öffne in Windows die Einstellungen unter System → Sound."); }
            };
            timer.Tick += async (_, _) => await RefreshAsync();
            Activated += OnActivation;
            await RefreshAsync();
            if (closed) return;
            timer.Start();
            await dialog.ShowAsync();
        }
        catch { if (!closed) ShowNotice("Die Audiogeräte konnten nicht geöffnet werden."); }
        finally
        {
            dismissed = true;
            StopMicrophoneMeter(); StopSpeakerTest();
            timer.Stop();
            Activated -= OnActivation;
            if (localOnly && ReferenceEquals(current, engine)) await StopSipAsync();
            audioDialogOpen = false;
        }
    }

    private static void SetOptions(ComboBox box, AudioOption[] options, string? selected)
    {
        if (box.ItemsSource is not AudioOption[] previous || !previous.SequenceEqual(options)) box.ItemsSource = options;
        box.SelectedValue = selected;
        ToolTipService.SetToolTip(box, options.FirstOrDefault(d => d.Id == selected)?.Name);
    }
}

