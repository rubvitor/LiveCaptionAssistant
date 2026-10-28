using LiveCaptionAssistant.Core.Transcription;
using LiveCaptionAssistant.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Whisper.net.Ggml;

namespace LiveCaptionAssistant.Infrastructure.Speech;

/// <summary>Downloads Whisper models once (through Whisper.net's downloader) and keeps them on disk.</summary>
internal sealed class WhisperModelStore
{
    private const long MinimumModelBytes = 10 * 1024 * 1024;
    private const int BufferSize = 1024 * 1024;
    private const long ReportEveryBytes = 4 * 1024 * 1024;

    private readonly ILogger<WhisperModelStore> _logger;

    public WhisperModelStore(ILogger<WhisperModelStore> logger)
    {
        _logger = logger;
    }

    public string GetPath(WhisperModelDescriptor model) => Path.Combine(AppPaths.ModelsDirectory, model.FileName);

    public bool IsDownloaded(WhisperModelDescriptor model)
    {
        var file = new FileInfo(GetPath(model));
        return file.Exists && file.Length >= MinimumModelBytes;
    }

    public void Delete(WhisperModelDescriptor model)
    {
        var path = GetPath(model);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public async Task<string> EnsureDownloadedAsync(
        WhisperModelDescriptor model,
        IProgress<ModelLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var path = GetPath(model);
        if (IsDownloaded(model))
        {
            return path;
        }

        Directory.CreateDirectory(AppPaths.ModelsDirectory);
        var partialPath = path + ".partial";
        _logger.LogInformation("Downloading Whisper model {Model}.", model.Id);

        try
        {
            var downloaded = await DownloadAsync(model, partialPath, progress, cancellationToken).ConfigureAwait(false);
            if (downloaded < MinimumModelBytes)
            {
                throw new InvalidDataException("The downloaded speech model is incomplete. Please try again.");
            }

            File.Move(partialPath, path, overwrite: true);
            _logger.LogInformation("Whisper model {Model} downloaded ({Bytes:N0} bytes).", model.Id, downloaded);
            return path;
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    private static async Task<long> DownloadAsync(
        WhisperModelDescriptor model,
        string destination,
        IProgress<ModelLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var probe = new ContentLengthProbeHandler { InnerHandler = new SocketsHttpHandler() };
        using var httpClient = new HttpClient(probe) { Timeout = TimeSpan.FromHours(2) };
        var downloader = new WhisperGgmlDownloader(httpClient);

        await using var source = await downloader.GetGgmlModelAsync(model.Type, model.Quantization, cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);

        var total = probe.ContentLength ?? model.ApproximateSizeBytes;
        var buffer = new byte[BufferSize];
        long downloaded = 0;
        long lastReported = 0;
        progress?.Report(new ModelLoadProgress(ModelLoadStage.Downloading, 0, total));

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;
            if (downloaded - lastReported >= ReportEveryBytes)
            {
                lastReported = downloaded;
                progress?.Report(new ModelLoadProgress(ModelLoadStage.Downloading, downloaded, Math.Max(total, downloaded)));
            }
        }

        progress?.Report(new ModelLoadProgress(ModelLoadStage.Downloading, downloaded, downloaded));
        return downloaded;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}.", path);
        }
    }

    /// <summary>Captures the response size so download progress can be reported as a percentage.</summary>
    private sealed class ContentLengthProbeHandler : DelegatingHandler
    {
        public long? ContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ContentLength = response.Content.Headers.ContentLength;
            return response;
        }
    }
}
