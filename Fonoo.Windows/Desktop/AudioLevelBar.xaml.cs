using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fonoo.Windows.Desktop;

public sealed partial class AudioLevelBar : UserControl
{
    private readonly Border[] segments = new Border[24];
    private double displayedLevel, heldPeak;
    private long peakUntil, lastUpdate = Environment.TickCount64;

    public AudioLevelBar()
    {
        InitializeComponent();
        for (var i = 0; i < segments.Length; i++)
        {
            Segments.ColumnDefinitions.Add(new ColumnDefinition());
            segments[i] = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, i == segments.Length - 1 ? 0 : 3, 0) };
            Grid.SetColumn(segments[i], i); Segments.Children.Add(segments[i]);
        }
        SetLevel(0, 0, false);
    }

    internal void SetLevel(double level, double peak, bool measuring)
    {
        level = measuring && double.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
        peak = measuring && double.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0;
        var now = Environment.TickCount64;
        var elapsed = Math.Clamp(now - lastUpdate, 0, 1000); lastUpdate = now;
        displayedLevel = measuring ? Math.Max(level, displayedLevel - elapsed * .0009) : 0;
        if (!measuring) { heldPeak = 0; peakUntil = 0; }
        else if (peak >= heldPeak) { heldPeak = peak; peakUntil = now + 700; }
        else if (now > peakUntil) heldPeak = Math.Max(peak, heldPeak - elapsed * .0006);
        var active = (int)Math.Ceiling(displayedLevel * segments.Length);
        var peakIndex = (int)Math.Ceiling(heldPeak * segments.Length) - 1;
        for (var i = 0; i < segments.Length; i++)
        {
            var style = i < active ? i >= 22 ? "High" : i >= 19 ? "Warm" : "Normal"
                : i == peakIndex ? i >= 22 ? "HighPeak" : i >= 19 ? "WarmPeak" : "Peak" : "Empty";
            segments[i].Style = (Style)Resources[style];
        }
    }
}
