using LiveCaptionAssistant.Infrastructure.Speech;

namespace LiveCaptionAssistant.Tests.Infrastructure;

public sealed class WhisperGpuDeviceSelectorTests
{
    [Fact]
    public void Prefers_dedicated_gpu_even_when_integrated_gpu_is_first()
    {
        WhisperGpuDevice[] devices = [new(0, "Integrated graphics", false), new(1, "Dedicated graphics", true)];

        Assert.Equal(devices[1], WhisperGpuDeviceSelector.SelectPreferred(devices));
    }

    [Fact]
    public void Preserves_the_runtime_gpu_index_when_dedicated_gpu_is_first()
    {
        WhisperGpuDevice[] devices = [new(0, "Dedicated graphics", true), new(1, "Integrated graphics", false)];

        Assert.Equal(devices[0], WhisperGpuDeviceSelector.SelectPreferred(devices));
    }

    [Fact]
    public void Uses_integrated_gpu_when_no_dedicated_gpu_is_available()
    {
        WhisperGpuDevice[] devices = [new(0, "Integrated graphics", false)];

        Assert.Equal(devices[0], WhisperGpuDeviceSelector.SelectPreferred(devices));
    }

    [Fact]
    public void Leaves_runtime_default_when_no_gpu_can_be_enumerated() =>
        Assert.Null(WhisperGpuDeviceSelector.SelectPreferred([]));
}
