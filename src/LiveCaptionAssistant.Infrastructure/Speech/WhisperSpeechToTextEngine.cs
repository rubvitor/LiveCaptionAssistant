using System.Diagnostics;
using System.Text;
using LiveCaptionAssistant.Core.Audio;
using LiveCaptionAssistant.Core.Settings;
using LiveCaptionAssistant.Core.Transcription;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace LiveCaptionAssistant.Infrastructure.Speech;

/// <summary>Local, offline speech recognition with whisper.cpp (GPU through Vulkan, CPU fallback).</summary>
internal sealed class WhisperSpeechToTextEngine : ISpeechToTextEngine, IDisposable
{
    private static readonly string[] CandidateLanguages = ["en", "pt"];
    private static int _runtimeConfigured;

    private readonly WhisperModelStore _modelStore;
    private readonly ISettingsService _settings;
    private readonly ILogger<WhisperSpeechToTextEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string? _loadedModelId;
    private string? _processorLanguage;
    private volatile bool _isWarmedUp;

    public WhisperSpeechToTextEngine(WhisperModelStore modelStore, ISettingsService settings, ILogger<WhisperSpeechToTextEngine> logger)
    {
        _modelStore = modelStore;
        _settings = settings;
        _logger = logger;
    }

    public bool IsReady => _isWarmedUp;

    public string? ActiveModelDescription { get; private set; }

    public async Task EnsureReadyAsync(IProgress<ModelLoadProgress>? progress, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var model = WhisperSpeechModelCatalog.Find(settings.SpeechModelId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isWarmedUp && _loadedModelId == model.Id)
            {
                progress?.Report(new ModelLoadProgress(ModelLoadStage.Ready));
                return;
            }

            Unload();
            var path = await _modelStore.EnsureDownloadedAsync(model, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ModelLoadProgress(ModelLoadStage.Loading));
            ConfigureRuntime(settings.UseGpuAcceleration);
            await Task.Run(() => Load(model, path, settings.UseGpuAcceleration), cancellationToken).ConfigureAwait(false);
            progress?.Report(new ModelLoadProgress(ModelLoadStage.WarmingUp));
            await WarmUpAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new ModelLoadProgress(ModelLoadStage.Ready));
        }
        catch
        {
            // A cancelled or failed warmup must not let the next Start reuse a half-ready processor.
            await UnloadAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SpeechRecognitionResult> RecognizeAsync(SpeechRecognitionRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var processor = _isWarmedUp && _processor is { } readyProcessor
                ? readyProcessor
                : throw new InvalidOperationException("The speech engine has not finished loading and warming up.");
            var started = Stopwatch.GetTimestamp();
            var (language, confidence) = request.ForcedLanguage is { } forced
                ? (forced, 1f)
                : DetectLanguage(processor, request);

            var code = language.ToIsoCode();
            if (_processorLanguage != code)
            {
                processor.ChangeLanguage(code);
                _processorLanguage = code;
            }

            var text = new StringBuilder();
            var noSpeechProbability = 0f;
            await foreach (var segment in processor.ProcessAsync(request.Samples, cancellationToken).ConfigureAwait(false))
            {
                text.Append(segment.Text);
                noSpeechProbability = Math.Max(noSpeechProbability, segment.NoSpeechProbability);
            }

            var result = new SpeechRecognitionResult(text.ToString().Trim(), language, confidence, noSpeechProbability);
            _logger.LogInformation("Recognized {AudioSeconds:F2} seconds of audio in {ElapsedSeconds:F2} seconds ({Language}, {Characters} characters).",
                (double)request.Samples.Length / AudioConstants.SampleRate, Stopwatch.GetElapsedTime(started).TotalSeconds, language, result.Text.Length);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        Unload();
        _gate.Dispose();
    }

    private static (SpokenLanguage Language, float Confidence) DetectLanguage(WhisperProcessor processor, SpeechRecognitionRequest request)
    {
        var (code, probability) = processor.DetectLanguageWithProbability(request.Samples, CandidateLanguages);
        return SpokenLanguageExtensions.FromIsoCode(code) is { } detected
            ? (detected, probability)
            : (request.FallbackLanguage, 0f);
    }

    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        var processor = _processor ?? throw new InvalidOperationException("The speech engine has not been loaded.");
        var started = Stopwatch.GetTimestamp();
        _logger.LogInformation("Warming up the speech engine before audio capture starts.");
        // A silent inference initializes the actual encoder/decoder kernels; its output is deliberately discarded.
        await foreach (var _ in processor.ProcessAsync(new float[AudioConstants.SampleRate], cancellationToken).ConfigureAwait(false))
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        _isWarmedUp = true;
        _logger.LogInformation("Speech engine warmed up in {Seconds:F2} seconds.", Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    /// <summary>The native runtime is chosen once per process, before the first model load.</summary>
    private void ConfigureRuntime(bool useGpu)
    {
        if (Interlocked.Exchange(ref _runtimeConfigured, 1) == 1)
        {
            return;
        }

        RuntimeOptions.RuntimeLibraryOrder = useGpu
            ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu]
            : [RuntimeLibrary.Cpu];
        _logger.LogInformation("Whisper runtime order: {Order}.", string.Join(", ", RuntimeOptions.RuntimeLibraryOrder));
    }

    private void Load(WhisperModelDescriptor model, string path, bool useGpu)
    {
        var options = WhisperFactoryOptions.Default;
        options.UseGpu = useGpu;
        var gpu = useGpu ? WhisperGpuDeviceSelector.Select(_logger) : null;
        options.GpuDevice = gpu?.Index ?? 0;

        try
        {
            _factory = WhisperFactory.FromPath(path, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Whisper model {Model}; deleting it so it is downloaded again.", model.Id);
            _modelStore.Delete(model);
            throw new InvalidOperationException("The speech model could not be loaded. It will be downloaded again on the next start.", ex);
        }

        _processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithNoContext()
            .WithSingleSegment()
            .WithThreads(Math.Clamp(Environment.ProcessorCount - 2, 2, 8))
            .Build();
        _processorLanguage = "en";
        _loadedModelId = model.Id;
        var backend = useGpu ? DescribeRuntime(RuntimeOptions.LoadedLibrary) : "CPU";
        ActiveModelDescription = $"Whisper {model.DisplayName.Split(" - ")[0]} · {backend}{(gpu is null ? string.Empty : $" · {gpu.Name}")}";
        _logger.LogInformation("Loaded Whisper model {Model} on {Runtime} ({Device}).", model.Id, RuntimeOptions.LoadedLibrary, gpu?.Name ?? backend);
    }

    private void Unload()
    {
        _isWarmedUp = false;
        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
        _loadedModelId = null;
        _processorLanguage = null;
        ActiveModelDescription = null;
    }

    private async Task UnloadAsync()
    {
        _isWarmedUp = false;
        if (_processor is { } processor)
        {
            // ProcessAsync cancellation can return before the native worker exits; DisposeAsync waits for it.
            await processor.DisposeAsync().ConfigureAwait(false);
            _processor = null;
        }

        Unload();
    }

    private static string DescribeRuntime(RuntimeLibrary? runtime) => runtime switch
    {
        RuntimeLibrary.Vulkan => "GPU (Vulkan)",
        RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 => "GPU (CUDA)",
        RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx => "CPU",
        null => "unknown runtime",
        _ => runtime.Value.ToString(),
    };
}
