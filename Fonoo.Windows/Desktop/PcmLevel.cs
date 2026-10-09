namespace Fonoo.Windows.Desktop;

internal readonly record struct PcmLevel(double Rms, double Peak)
{
    internal static PcmLevel Measure(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return default;
        double square = 0, peak = 0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample)) continue;
            square += (double)sample * sample;
            peak = Math.Max(peak, Math.Abs((double)sample));
        }
        return new(Scale(square > 0 ? 10 * Math.Log10(square / samples.Length) : -120), Scale(peak > 0 ? 20 * Math.Log10(peak) : -120));
    }

    private static double Scale(double db) => Math.Clamp((db + 60) / 60, 0, 1);
}
