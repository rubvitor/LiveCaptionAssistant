using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace LiveCaptionAssistant.Infrastructure.Speech;

internal sealed record WhisperGpuDevice(int Index, string Name, bool IsDedicated);

/// <summary>Prefers a dedicated Vulkan GPU when the machine also has integrated graphics.</summary>
internal static class WhisperGpuDeviceSelector
{
    private const int DedicatedGpuType = 1;
    private const int IntegratedGpuType = 2;

    public static WhisperGpuDevice? Select(ILogger logger)
    {
        try
        {
            // Initializes Whisper.net's selected native runtime and its backend registry without loading a model.
            _ = WhisperFactory.GetRuntimeInfo();
            if (RuntimeOptions.LoadedLibrary != RuntimeLibrary.Vulkan)
            {
                return null;
            }

            var devices = EnumerateVulkanDevices();
            foreach (var device in devices)
            {
                logger.LogInformation("Whisper Vulkan GPU {Index}: {Name} ({Kind}).", device.Index, device.Name, device.IsDedicated ? "dedicated" : "integrated");
            }

            var selected = SelectPreferred(devices);
            if (selected is null)
            {
                logger.LogWarning("Vulkan GPU enumeration is unavailable; using the runtime's default GPU (device 0).");
            }
            else
            {
                logger.LogInformation("Selected Whisper Vulkan GPU {Index}: {Name}.", selected.Index, selected.Name);
            }

            return selected;
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException)
        {
            logger.LogWarning(ex, "Could not enumerate Whisper GPUs; using the runtime's default GPU (device 0).");
            return null;
        }
    }

    internal static WhisperGpuDevice? SelectPreferred(IReadOnlyList<WhisperGpuDevice> devices) =>
        devices.FirstOrDefault(device => device.IsDedicated) ?? devices.FirstOrDefault();

    private static IReadOnlyList<WhisperGpuDevice> EnumerateVulkanDevices()
    {
        // These public GGML C exports are split between two DLLs in the pinned Whisper.net runtime.
        // Resolve their actual loaded paths so architecture, installation layout and custom paths agree with Whisper.net.
        using var process = Process.GetCurrentProcess();
        var registryPath = FindLoadedModulePath(process, "ggml-whisper.dll");
        var deviceApiPath = FindLoadedModulePath(process, "ggml-base-whisper.dll");
        if (registryPath is null || deviceApiPath is null || !NativeLibrary.TryLoad(registryPath, out var registry))
        {
            return [];
        }

        try
        {
            if (!NativeLibrary.TryLoad(deviceApiPath, out var deviceApi))
            {
                return [];
            }

            try
            {
                var countDevices = GetExport<CountDevices>(registry, "ggml_backend_dev_count");
                var getDevice = GetExport<GetDevice>(registry, "ggml_backend_dev_get");
                var getDeviceType = GetExport<GetDeviceType>(deviceApi, "ggml_backend_dev_type");
                var getDeviceDescription = GetExport<GetDeviceDescription>(deviceApi, "ggml_backend_dev_description");
                if (countDevices is null || getDevice is null || getDeviceType is null || getDeviceDescription is null)
                {
                    return [];
                }

                var devices = new List<WhisperGpuDevice>();
                var gpuIndex = 0;
                var deviceCount = countDevices();
                for (nuint index = 0; index < deviceCount; index++)
                {
                    var device = getDevice(index);
                    var type = getDeviceType(device);
                    if (type is not (DedicatedGpuType or IntegratedGpuType))
                    {
                        continue;
                    }

                    // Whisper's GpuDevice counts GPUs only; CPU and accelerator registry entries do not count.
                    var name = Marshal.PtrToStringUTF8(getDeviceDescription(device)) ?? $"Vulkan GPU {gpuIndex}";
                    devices.Add(new WhisperGpuDevice(gpuIndex++, name, type == DedicatedGpuType));
                }

                return devices;
            }
            finally
            {
                NativeLibrary.Free(deviceApi);
            }
        }
        finally
        {
            NativeLibrary.Free(registry);
        }
    }

    private static string? FindLoadedModulePath(Process process, string name) =>
        process.Modules.Cast<ProcessModule>().FirstOrDefault(module => string.Equals(module.ModuleName, name, StringComparison.OrdinalIgnoreCase))?.FileName;

    private static T? GetExport<T>(IntPtr library, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(library, name, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint CountDevices();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetDevice(nuint index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDeviceType(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetDeviceDescription(IntPtr device);
}
