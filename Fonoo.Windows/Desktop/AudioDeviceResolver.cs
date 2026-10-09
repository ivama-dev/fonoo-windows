using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace Fonoo.Windows.Desktop;

internal static class AudioDeviceResolver
{
    internal static async Task<DeviceInformation> ResolveAsync(string sdkId, string sdkName, bool input)
    {
        var devices = await DeviceInformation.FindAllAsync(input ? MediaDevice.GetAudioCaptureSelector() : MediaDevice.GetAudioRenderSelector());
        if (sdkName == (input ? "Windows-Standardmikrofon" : "Windows-Standardausgabe"))
        {
            var id = input ? MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Communications) : MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Communications);
            return devices.FirstOrDefault(d => d.Id == id) ?? throw new InvalidOperationException("Das Windows-Standardgerät ist nicht verfügbar.");
        }
        var exact = devices.Where(d => sdkId.Contains(d.Id, StringComparison.OrdinalIgnoreCase) || d.Id.Contains(sdkId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var named = devices.Where(d => d.Name == sdkName).ToArray();
        return (exact.Length == 1 ? exact[0] : named.Length == 1 ? named[0] : null)
            ?? throw new InvalidOperationException("Audiogerät konnte nicht eindeutig zugeordnet werden.");
    }
}
