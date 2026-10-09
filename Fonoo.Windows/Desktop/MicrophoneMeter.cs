using System.Runtime.InteropServices;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Render;
using WinRT;

namespace Fonoo.Windows.Desktop;

// Local PCM analysis, with speaker output only for an explicitly started timed monitor.
// No recording or network output; stopped before SIP owns capture.
public sealed class MicrophoneMeter : IDisposable
{
    private readonly object gate = new();
    private AudioGraph? graph;
    private AudioFrameOutputNode? output;
    private double level;
    private double peakLevel;
    private float[] samples = [];
    private bool healthy;
    private long lastQuantum;
    private int quanta;
    private string lastProblem = "";
    private System.Threading.Timer? monitorTimeout;
    private long monitorUntil;
    public double Level => Volatile.Read(ref level);
    public double PeakLevel => Volatile.Read(ref peakLevel);
    public bool Healthy => Volatile.Read(ref healthy) && Environment.TickCount64 - Volatile.Read(ref lastQuantum) < 1000;
    public string InputId { get; private set; } = "";
    public bool Completed => Volatile.Read(ref graph) is null;
    public bool Monitoring => !Completed && monitorUntil > Environment.TickCount64;
    public long MonitorUntil => monitorUntil;
#if FONOO_DESIGN_PREVIEW
    private double syntheticPeak;
    internal static string SyntheticResult = "";
    internal static async Task<bool> CheckSyntheticPcmAsync(string path)
    {
        using var meter = new MicrophoneMeter();
        var result = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Communications));
        if (result.Status != AudioGraphCreationStatus.Success) { SyntheticResult = "Graph: " + result.Status; return false; }
        meter.graph = result.Graph;
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var source = await meter.graph.CreateFileInputNodeAsync(file);
        if (source.Status != AudioFileNodeCreationStatus.Success) { SyntheticResult = "Datei: " + source.Status; return false; }
        meter.output = meter.graph.CreateFrameOutputNode(); source.FileInputNode.AddOutgoingConnection(meter.output);
        meter.graph.QuantumStarted += meter.ReadQuantum; meter.graph.Start();
        await Task.Delay(400);
        // A file-only graph can reach EOF before the delay ends. Verify the
        // measured peak, not the final silent quantum after EOF.
        SyntheticResult = $"Frames: {meter.quanta}; Pegel: {Volatile.Read(ref meter.syntheticPeak):F2}; Format: {meter.graph.EncodingProperties.Subtype}/{meter.graph.EncodingProperties.BitsPerSample}; {meter.lastProblem}";
        return Volatile.Read(ref meter.syntheticPeak) > .1 && meter.lastProblem.Length == 0;
    }
#endif
    public static async Task<MicrophoneMeter> StartAsync(string sdkId, string sdkName, string? outputId = null, string? outputName = null)
    {
        if ((outputId is null) != (outputName is null)) throw new ArgumentException("Hörtest-Ausgabe muss vollständig angegeben werden.");
        var meter = new MicrophoneMeter { InputId = sdkId };
        try
        {
            var device = await AudioDeviceResolver.ResolveAsync(sdkId, sdkName, true);
            var settings = new AudioGraphSettings(AudioRenderCategory.Communications) { QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired, DesiredSamplesPerQuantum = 480 };
            if (outputId is not null && outputName is not null) settings.PrimaryRenderDevice = await AudioDeviceResolver.ResolveAsync(outputId, outputName, false);
            var result = await AudioGraph.CreateAsync(settings);
            if (result.Status != AudioGraphCreationStatus.Success) throw new InvalidOperationException();
            meter.graph = result.Graph;
            if (meter.graph.EncodingProperties.BitsPerSample != 32 || meter.graph.EncodingProperties.Subtype != "Float") throw new InvalidOperationException("Unbekanntes PCM-Format.");
            var capture = await meter.graph.CreateDeviceInputNodeAsync(MediaCategory.Communications, meter.graph.EncodingProperties, device);
            if (capture.Status != AudioDeviceNodeCreationStatus.Success) throw new InvalidOperationException();
            meter.output = meter.graph.CreateFrameOutputNode(); capture.DeviceInputNode.AddOutgoingConnection(meter.output);
            if (outputId is not null)
            {
                var speaker = await meter.graph.CreateDeviceOutputNodeAsync();
                if (speaker.Status != AudioDeviceNodeCreationStatus.Success) throw new InvalidOperationException("Hörtest-Ausgabe konnte nicht geöffnet werden.");
                capture.DeviceInputNode.AddOutgoingConnection(speaker.DeviceOutputNode);
                meter.monitorUntil = Environment.TickCount64 + 15000;
                meter.monitorTimeout = new System.Threading.Timer(_ => meter.Dispose(), null, 15000, Timeout.Infinite);
            }
            meter.graph.QuantumStarted += meter.ReadQuantum;
            meter.graph.Start(); return meter;
        }
        catch { meter.Dispose(); throw; }
    }
    private void ReadQuantum(AudioGraph sender, object args)
    {
        lock (gate)
        {
            if (output is null) return;
            quanta++;
            try
            {
                using var frame = output.GetFrame();
                using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
                using var reference = buffer.CreateReference();
                var iid = new Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(((IWinRTObject)reference).NativeObject.ThisPtr, in iid, out var access));
                IntPtr pointer; uint capacity;
                try
                {
                    var vtable = Marshal.ReadIntPtr(access);
                    var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBuffer>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
                    Marshal.ThrowExceptionForHR(getBuffer(access, out pointer, out capacity));
                }
                finally { Marshal.Release(access); }
                var count = (int)Math.Min(buffer.Length, capacity) / sizeof(float);
                if (count is <= 0 or > 65536) { Volatile.Write(ref level, 0); return; }
                if (samples.Length < count) samples = new float[count];
                Marshal.Copy(pointer, samples, 0, count);
                var reading = PcmLevel.Measure(samples.AsSpan(0, count));
                var measured = reading.Rms;
                Volatile.Write(ref level, measured);
                Volatile.Write(ref peakLevel, Math.Max(reading.Peak, peakLevel - .015));
#if FONOO_DESIGN_PREVIEW
                Volatile.Write(ref syntheticPeak, Math.Max(syntheticPeak, measured));
#endif
                Volatile.Write(ref healthy, true);
                Volatile.Write(ref lastQuantum, Environment.TickCount64);
            }
            catch (Exception ex) { lastProblem = ex.GetType().Name; Volatile.Write(ref level, 0); Volatile.Write(ref healthy, false); }
        }
    }
    public void Dispose()
    {
        Interlocked.Exchange(ref monitorTimeout, null)?.Dispose();
        AudioGraph? old;
        lock (gate) { old = graph; graph = null; output = null; Array.Clear(samples); Volatile.Write(ref level, 0); Volatile.Write(ref peakLevel, 0); Volatile.Write(ref healthy, false); }
        if (old is null) return;
        old.QuantumStarted -= ReadQuantum;
        try { old.Stop(); } catch { }
        finally { try { old.Dispose(); } catch { } }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBuffer(IntPtr self, out IntPtr buffer, out uint capacity);
}
