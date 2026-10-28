using Microsoft.Extensions.Logging;

namespace LiveCaptionAssistant.Core.Settings;

public sealed class SettingsService : ISettingsService
{
    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly Lock _gate = new();
    private AppSettings _current;

    public SettingsService(ISettingsStore store, ILogger<SettingsService> logger)
    {
        _store = store;
        _logger = logger;
        _current = store.Load();
    }

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Update(Func<AppSettings, AppSettings> update)
    {
        AppSettings updated;
        lock (_gate)
        {
            updated = update(_current);
            if (updated == _current)
            {
                return;
            }

            _current = updated;
            try
            {
                _store.Save(updated);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Failed to save settings.");
            }
        }

        Changed?.Invoke(this, updated);
    }
}
