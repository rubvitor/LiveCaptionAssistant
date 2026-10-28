using System.Runtime.InteropServices;
using LiveCaptionAssistant.Core.Audio;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace LiveCaptionAssistant.Infrastructure.Audio;

internal sealed class WasapiAudioDeviceCatalog : IAudioDeviceCatalog
{
    private readonly ILogger<WasapiAudioDeviceCatalog> _logger;

    public WasapiAudioDeviceCatalog(ILogger<WasapiAudioDeviceCatalog> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioEndpointKind kind)
    {
        var devices = new List<AudioDeviceInfo>
        {
            new(AudioDeviceIds.DefaultDevice, kind == AudioEndpointKind.SystemOutput ? "Automatic — all Windows outputs" : "Default device"),
            new(AudioDeviceIds.DefaultCommunicationsDevice, "Default communications device"),
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var flow = kind == AudioEndpointKind.SystemOutput ? DataFlow.Render : DataFlow.Capture;
            using var endpoints = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            foreach (var endpoint in endpoints)
            {
                using (endpoint)
                {
                    devices.Add(new AudioDeviceInfo(endpoint.ID, endpoint.FriendlyName));
                }
            }
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "Could not enumerate {Kind} devices.", kind);
        }

        return devices;
    }

    internal static IReadOnlyList<AudioDeviceInfo> GetActivePlaybackDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var devices = new List<AudioDeviceInfo>();
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
            {
                devices.Add(new AudioDeviceInfo(endpoint.ID, endpoint.FriendlyName));
            }
        }

        return devices;
    }
}
