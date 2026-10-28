namespace LiveCaptionAssistant.Core.Audio;

public static class AudioConstants
{
    /// <summary>Sample rate of the mono float audio delivered to the speech pipeline (what Whisper expects).</summary>
    public const int SampleRate = 16_000;
}

public enum AudioEndpointKind
{
    SystemOutput,
    Microphone,
}

public static class AudioDeviceIds
{
    public const string DefaultDevice = "default";
    public const string DefaultCommunicationsDevice = "default-communications";
}

public sealed record AudioDeviceInfo(string Id, string Name);

public interface IAudioDeviceCatalog
{
    IReadOnlyList<AudioDeviceInfo> GetDevices(AudioEndpointKind kind);
}

public interface IAudioCaptureSource
{
    /// <summary>
    /// Captures audio until <paramref name="cancellationToken"/> is cancelled and yields
    /// mono float chunks at <see cref="AudioConstants.SampleRate"/>.
    /// </summary>
    IAsyncEnumerable<float[]> CaptureAsync(CancellationToken cancellationToken);
}

public interface IAudioCaptureSourceFactory
{
    IAudioCaptureSource Create(AudioEndpointKind kind, string? deviceId);
}
