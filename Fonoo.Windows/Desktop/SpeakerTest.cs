using Windows.Media.Audio;
using Windows.Media.Render;
using Windows.Storage;

namespace Fonoo.Windows.Desktop;

// Playback of an app-owned test tone through the selected endpoint; no microphone node.
internal sealed class SpeakerTest : IDisposable
{
    private AudioGraph? graph;
    private int completed;
    private int failed;
    private long deadline;
    internal bool Completed => Volatile.Read(ref completed) != 0 || Environment.TickCount64 >= deadline;
    internal bool Failed => Volatile.Read(ref failed) != 0;

    internal static async Task<SpeakerTest> StartAsync(string sdkId, string sdkName)
    {
        var test = new SpeakerTest();
        try
        {
            var device = await AudioDeviceResolver.ResolveAsync(sdkId, sdkName, false);
            var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Communications) { PrimaryRenderDevice = device, MaxPlaybackSpeedFactor = 1 });
            if (created.Status != AudioGraphCreationStatus.Success) throw new InvalidOperationException("Ausgabegerät ist nicht verfügbar.");
            test.graph = created.Graph;
            test.graph.UnrecoverableErrorOccurred += (_, _) => { Volatile.Write(ref test.failed, 1); Volatile.Write(ref test.completed, 1); };
            var output = await test.graph.CreateDeviceOutputNodeAsync();
            if (output.Status != AudioDeviceNodeCreationStatus.Success) throw new InvalidOperationException("Ausgabegerät konnte nicht geöffnet werden.");
            var file = await StorageFile.GetFileFromPathAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "AudioTest.wav"));
            var source = await test.graph.CreateFileInputNodeAsync(file);
            if (source.Status != AudioFileNodeCreationStatus.Success) throw new InvalidOperationException("Testton konnte nicht geladen werden.");
            source.FileInputNode.LoopCount = 0;
            source.FileInputNode.AddOutgoingConnection(output.DeviceOutputNode);
            source.FileInputNode.FileCompleted += (_, _) => Volatile.Write(ref test.completed, 1);
            test.deadline = Environment.TickCount64 + 4000;
            test.graph.Start();
            return test;
        }
        catch { test.Dispose(); throw; }
    }

    public void Dispose()
    {
        Volatile.Write(ref completed, 1);
        var old = Interlocked.Exchange(ref graph, null);
        if (old is null) return;
        try { old.Stop(); } catch { }
        finally { try { old.Dispose(); } catch { } }
    }
}
