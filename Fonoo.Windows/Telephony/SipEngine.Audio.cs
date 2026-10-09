using Linphone;
using System.Text.Json;

namespace Fonoo.Windows.Telephony;

public sealed record AudioOption(string Id, string Name);
public sealed record AudioSnapshot(AudioOption[] Inputs, AudioOption[] Outputs, string? InputId,
    string? OutputId, bool InCall, bool Muted, double Level, bool Testing, string Notice);

public sealed partial class SipEngine
{
    private string? preferredInput;
    private string? preferredOutput;
    private bool audioDevicesChanged;
    private long audioTestUntil;
    private string audioNotice = "";
    private static string AudioPreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fonoo", "Windows", "audio.json");

    private void LoadAudioPreferences()
    {
        try
        {
            if (!File.Exists(AudioPreferencesPath)) return;
            var values = JsonSerializer.Deserialize<string?[]>(File.ReadAllText(AudioPreferencesPath));
            if (values is { Length: 2 }) { preferredInput = values[0]; preferredOutput = values[1]; }
        }
        catch { audioNotice = "Die gespeicherte Audioauswahl konnte nicht geladen werden."; }
    }

    private void ApplyAudioPreferences()
    {
        var devices = core!.ExtendedAudioDevices.ToArray();
        Apply(true, preferredInput);
        Apply(false, preferredOutput);
        void Apply(bool input, string? preferred)
        {
            var available = devices.Where(d => d.HasCapability(input ? AudioDeviceCapabilities.CapabilityRecord : AudioDeviceCapabilities.CapabilityPlay)).ToArray();
            var current = input ? core.DefaultInputAudioDevice : core.DefaultOutputAudioDevice;
            var device = available.FirstOrDefault(d => d.Id == preferred)
                ?? available.FirstOrDefault(d => d.Id == current?.Id) ?? available.FirstOrDefault();
            if (device is null) return;
            if (input) { core.DefaultInputAudioDevice = device; if (call is not null) call.InputAudioDevice = device; if (consultation is not null) consultation.InputAudioDevice = device; }
            else { core.DefaultOutputAudioDevice = device; if (call is not null) call.OutputAudioDevice = device; if (consultation is not null) consultation.OutputAudioDevice = device;
                foreach (var speaker in available) speaker.UseForRinging = speaker.Id == device.Id; }
        }
    }

    public async Task<AudioSnapshot> GetAudioAsync(bool reload = false)
    {
        AudioSnapshot? result = null;
        await Enqueue(() =>
        {
            if (reload) { StopAudioTest(); core!.ReloadSoundDevices(); ApplyAudioPreferences(); }
            var devices = core!.ExtendedAudioDevices.ToArray();
            AudioOption[] Options(AudioDeviceCapabilities capability) => devices.Where(d => d.HasCapability(capability))
                .Select(d => new AudioOption(d.Id, d.DeviceName switch { "Default Capture" => "Windows-Standardmikrofon", "Default Playback" => "Windows-Standardausgabe", _ => d.DeviceName })).DistinctBy(d => d.Id).OrderBy(d => d.Name).ToArray();
            var inputs = Options(AudioDeviceCapabilities.CapabilityRecord);
            var outputs = Options(AudioDeviceCapabilities.CapabilityPlay);
            var input = ControlledCall?.InputAudioDevice ?? core.DefaultInputAudioDevice;
            var output = ControlledCall?.OutputAudioDevice ?? core.DefaultOutputAudioDevice;
            var message = audioNotice;
            if (inputs.Length == 0 || outputs.Length == 0) message = "Kein Mikrofon oder Lautsprecher verfügbar. Bitte ein Gerät verbinden.";
            else if ((preferredInput is not null && !inputs.Any(d => d.Id == preferredInput)) ||
                     (preferredOutput is not null && !outputs.Any(d => d.Id == preferredOutput)))
                message = "Ein gewähltes Gerät fehlt. Fonoo verwendet ein verfügbares Ersatzgerät.";
            var volume = ControlledCall?.State == CallState.StreamsRunning && !muted ? ControlledCall.RecordVolume : -120;
            var level = double.IsFinite(volume) ? Math.Clamp((volume + 60) / 60.0, 0, 1) : 0;
            result = new(inputs, outputs, input?.Id, output?.Id, call is not null, muted, level, audioTestUntil != 0, message);
        });
        return result!;
    }

    public Task SelectAudioAsync(bool input, string id) => Enqueue(() =>
    {
        StopAudioTest();
        var device = core!.ExtendedAudioDevices.FirstOrDefault(d => d.Id == id && d.HasCapability(
            input ? AudioDeviceCapabilities.CapabilityRecord : AudioDeviceCapabilities.CapabilityPlay))
            ?? throw new InvalidOperationException("Gerät nicht mehr verfügbar.");
        if (input) { core.DefaultInputAudioDevice = device; if (call is not null) call.InputAudioDevice = device; if (consultation is not null) consultation.InputAudioDevice = device; preferredInput = id; }
        else { core.DefaultOutputAudioDevice = device; if (call is not null) call.OutputAudioDevice = device; if (consultation is not null) consultation.OutputAudioDevice = device; preferredOutput = id;
            foreach (var speaker in core.ExtendedAudioDevices.Where(d => d.HasCapability(AudioDeviceCapabilities.CapabilityPlay))) speaker.UseForRinging = speaker.Id == id; }
        audioNotice = "";
        var temporary = AudioPreferencesPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AudioPreferencesPath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new[] { preferredInput, preferredOutput }));
            File.Move(temporary, AudioPreferencesPath, true);
        }
        catch { audioNotice = "Die Auswahl gilt jetzt, konnte aber nicht dauerhaft gespeichert werden."; }
    });

    public Task TestAudioAsync(bool start) => Enqueue(() =>
    {
        StopAudioTest();
        if (!start) return;
        if (call is not null) throw new InvalidOperationException();
        try { core!.StartEchoTester(48000); audioTestUntil = Environment.TickCount64 + 15000; }
        catch { core!.StopEchoTester(); throw; }
    });

    private void StopAudioTest()
    {
        if (audioTestUntil == 0) return;
        audioTestUntil = 0;
        try { core?.StopEchoTester(); } catch { audioNotice = "Der Hörtest konnte nicht regulär beendet werden."; }
    }
}
