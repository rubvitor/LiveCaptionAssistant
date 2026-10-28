using System.Text.Json;
using LiveCaptionAssistant.Core.Settings;
using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.Infrastructure.Storage;

internal sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path = Path.Combine(AppPaths.RoamingDataDirectory, "settings.json");
    private readonly ILogger<JsonSettingsStore> _logger;

    public JsonSettingsStore(ILogger<JsonSettingsStore> logger)
    {
        _logger = logger;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return ApplyDefaultsUpgrade(new AppSettings());
        }

        try
        {
            var json = File.ReadAllBytes(_path);
            return ApplyDefaultsUpgrade(JsonSerializer.Deserialize(json, StorageJsonContext.Default.AppSettings) ?? new AppSettings());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Settings file is unreadable; using defaults.");
            TryBackUpCorruptFile();
            return ApplyDefaultsUpgrade(new AppSettings());
        }
    }

    // Apply the new Medium default once to existing profiles, while preserving later effort choices.
    internal static AppSettings ApplyDefaultsUpgrade(AppSettings settings) => settings.ResponsePreferencesVersion < 1
        ? settings with { ChatReasoningEffort = "medium", ResponsePreferencesVersion = 1 }
        : settings;

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, StorageJsonContext.Default.AppSettings);
        AtomicFile.WriteAllBytes(_path, json);
    }

    private void TryBackUpCorruptFile()
    {
        try
        {
            File.Copy(_path, _path + ".corrupt", overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not back up the unreadable settings file.");
        }
    }
}
