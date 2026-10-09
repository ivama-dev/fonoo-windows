using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Fonoo.Windows.Desktop;

public enum AudioMeterMode { Starting, Local, Call, Muted, EchoTest, Paused, Unavailable }

public sealed partial class AudioSettingsView : UserControl
{
    internal ComboBox Inputs => InputSelector;
    internal ComboBox Outputs => OutputSelector;
    internal Button EchoTest => EchoTestButton;
    internal Button SpeakerTest => SpeakerTestButton;
    internal Button Refresh => RefreshButton;
    internal Button WindowsSettings => WindowsSettingsButton;

    public AudioSettingsView()
    {
        InitializeComponent();
        SetMeter(0, 0, AudioMeterMode.Starting);
    }

    internal void SetMeter(double level, double peak, AudioMeterMode mode)
    {
        var measuring = mode is AudioMeterMode.Local or AudioMeterMode.Call;
        level = measuring && double.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
        peak = measuring && double.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0;
        LevelMeter.SetLevel(level, peak, measuring);
        var status = mode switch
        {
            AudioMeterMode.Starting => "Wird vorbereitet …",
            AudioMeterMode.Muted => "Stummgeschaltet",
            AudioMeterMode.Paused => "Messung pausiert",
            AudioMeterMode.EchoTest => "Hörtest läuft",
            AudioMeterMode.Unavailable => "Nicht verfügbar",
            _ => level >= .94 ? "Sehr hoher Pegel" : level > .2 ? "Signal erkannt" : "Bereit zum Sprechen"
        };
        LevelStatus.Text = status;
        LevelStatus.Style = (Style)Resources[level >= .94 ? "AudioHighLevelText" : "AudioLevelText"];
        AutomationProperties.SetName(LevelMeter, "Mikrofonpegel: " + status);
        LevelHint.Text = mode switch
        {
            AudioMeterMode.Call => "Der Pegel zeigt dein Mikrofon im laufenden Gespräch.",
            AudioMeterMode.Muted => "Dein Mikrofon ist im Gespräch stummgeschaltet.",
            AudioMeterMode.EchoTest => "Du hörst deine Stimme direkt über das ausgewählte Ausgabegerät.",
            AudioMeterMode.Paused => "Die Messung wird fortgesetzt, sobald dieses Fenster wieder aktiv ist.",
            AudioMeterMode.Unavailable => "Prüfe dein Mikrofon und den Mikrofonzugriff für Desktop-Apps in Windows.",
            _ when level >= .94 => "Der Pegel ist sehr hoch. Halte etwas mehr Abstand zum Mikrofon.",
            _ => "Sprich kurz in dein Mikrofon. Die Umrandung hält den Spitzenpegel kurz fest."
        };
    }

    internal void SetTests(bool echo, int remaining, bool speaker, bool inCall, bool busy, bool hasInput, bool hasOutput, bool active)
    {
        InputSelector.IsEnabled = !busy && hasInput;
        OutputSelector.IsEnabled = !busy && hasOutput;
        RefreshButton.IsEnabled = !busy;
        EchoTestButton.IsEnabled = !busy && !inCall && !speaker && (echo || hasInput && hasOutput) && active;
        SpeakerTestButton.IsEnabled = !busy && !inCall && !echo && (speaker || hasOutput) && active;
        EchoTestLabel.Text = echo ? "Hörtest beenden" : "Mich selbst hören";
        if (echo) LevelHint.Text = "Du hörst deine Stimme über das ausgewählte Gerät. Der Pegel bleibt dabei sichtbar.";
        SpeakerTestLabel.Text = speaker ? "Testton stoppen" : "Testton abspielen";
        AutomationProperties.SetName(EchoTestButton, EchoTestLabel.Text);
        AutomationProperties.SetName(SpeakerTestButton, SpeakerTestLabel.Text);
        EchoProgress.Visibility = echo ? Visibility.Visible : Visibility.Collapsed;
        EchoTime.Value = remaining;
        EchoTimeLabel.Text = $"Hörtest endet in {remaining} Sekunden";
        SpeakerHint.Text = inCall ? "Audiotests sind während eines Gesprächs pausiert. Geräte kannst du weiterhin wechseln."
            : speaker ? "Testton läuft · endet automatisch nach wenigen Sekunden"
            : "Du hörst einen kurzen Testton über das ausgewählte Gerät.";
    }

    internal void SetNotice(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (Notice.Message != message) Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = message.Length > 0;
    }

#if FONOO_DESIGN_PREVIEW
    internal void MarkPreview() => Introduction.Text = "Designvorschau · Beispielgeräte und Beispielpegel. Es wird kein Mikrofon verwendet und kein Ton abgespielt.";
#endif
}
