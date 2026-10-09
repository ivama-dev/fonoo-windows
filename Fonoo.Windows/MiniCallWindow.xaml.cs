using Fonoo.Windows.Telephony;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Fonoo.Windows;

public sealed partial class MiniCallWindow : Window
{
    private bool disposing;
    private readonly Action<int> action;
    private bool active, consulting, incoming, positioned;
    private int sizedWidth, sizedHeight;
    private string callId = "";
    public event Action? Dismissed;

    public MiniCallWindow(Action<int> action)
    {
        this.action = action;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonoo.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        { presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsAlwaysOnTop = true; }
        ResizeCompact();
        Root.Loaded += (_, _) =>
        {
            ResizeCompact();
            Root.XamlRoot.Changed += (_, _) => ResizeCompact();
        };
        AppWindow.Closing += (_, e) => { if (!disposing) { e.Cancel = true; AppWindow.Hide(); Dismissed?.Invoke(); } };
    }

    private void ResizeCompact()
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? 1;
        var width = (int)Math.Ceiling(320 * scale);
        var height = (int)Math.Ceiling((consulting ? 220 : incoming ? 214 : 176) * scale);
        if (sizedWidth == width && sizedHeight == height) return;
        sizedWidth = width; sizedHeight = height;
        AppWindow.ResizeClient(new SizeInt32(width, height));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var margin = (int)(16 * scale);
        var position = AppWindow.Position;
        var x = positioned ? position.X : area.X + area.Width - AppWindow.Size.Width - margin;
        var y = positioned ? position.Y : area.Y + area.Height - AppWindow.Size.Height - margin;
        AppWindow.Move(new PointInt32(Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - AppWindow.Size.Width)), Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - AppWindow.Size.Height))));
        positioned = true;
    }

    public void Update(SipSnapshot state, bool pending)
    {
        Peer.Text = state.Peer; State.Text = state.Status;
        ToolTipService.SetToolTip(Peer, state.Peer); ToolTipService.SetToolTip(State, state.Status);
        active = state.Active; consulting = state.Consulting; incoming = state.Incoming;
        if (state.CallId != callId || !active) LevelMeter.SetLevel(0, 0, false);
        callId = state.CallId;
        Answer.Visibility = incoming ? Visibility.Visible : Visibility.Collapsed;
        Answer.IsEnabled = !pending;
        MuteSlash.Visibility = state.Muted ? Visibility.Visible : Visibility.Collapsed;
        Mute.Style = (Style)Root.Resources[state.Muted ? "MiniMuteActive" : "MiniButton"];
        SetAction(Mute, state.Muted ? "Mikrofon aktivieren" : "Mikrofon stummschalten");
        Mute.IsEnabled = state.Active && !pending;
        HoldIcon.Glyph = state.Held ? "\uE768" : "\uE769";
        SetAction(Hold, state.Held ? "Gespräch fortsetzen" : "Gespräch halten");
        Hold.IsEnabled = (state.Active || state.Held) && !state.HoldPending && !state.Consulting && !state.TransferPending && !pending;
        SetAction(End, state.Incoming ? "Anruf ablehnen" : "Auflegen"); End.IsEnabled = !pending;
        Keypad.IsEnabled = state.Active;
        Transfer.IsEnabled = state.CanTransfer && !pending;
        ConsultationActions.Visibility = consulting ? Visibility.Visible : Visibility.Collapsed;
        Complete.IsEnabled = state.ConsultationActive && state.Held && !state.TransferPending && !pending;
        ReturnOriginal.IsEnabled = !state.TransferPending && !pending;
        ResizeCompact();
    }

    private static void SetAction(Button button, string text)
    { ToolTipService.SetToolTip(button, text); AutomationProperties.SetName(button, text); }

    public void SetActivity(string duration, double[] levels)
    {
        Duration.Text = duration;
        var level = levels.Length > 0 ? levels[^1] : 0;
        LevelMeter.SetLevel(level, levels.Length > 0 ? levels.Max() : 0, active);
    }
    public void ShowWithoutFocus() => AppWindow.Show(false);
    public void DisposeWindow() { disposing = true; Close(); }
    private void AnswerClicked(object sender, RoutedEventArgs e) => action(3);
    private void MuteClicked(object sender, RoutedEventArgs e) => action(4);
    private void HoldClicked(object sender, RoutedEventArgs e) => action(5);
    private void EndClicked(object sender, RoutedEventArgs e) => action(6);
    private void OpenClicked(object sender, RoutedEventArgs e) => action(1);
    private void KeypadClicked(object sender, RoutedEventArgs e) => action(9);
    private void TransferClicked(object sender, RoutedEventArgs e) => action(10);
    private void CompleteClicked(object sender, RoutedEventArgs e) => action(11);
    private void ReturnClicked(object sender, RoutedEventArgs e) => action(12);
}

