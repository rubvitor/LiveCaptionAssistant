using System.Reflection;
using LiveCaptionAssistant.Core.Assistant;
using LiveCaptionAssistant.Core.Audio;
using LiveCaptionAssistant.Core.Authentication;
using LiveCaptionAssistant.Core.Platform;
using LiveCaptionAssistant.Core.Settings;
using LiveCaptionAssistant.Core.Transcription;
using LiveCaptionAssistant.Infrastructure.Audio;
using LiveCaptionAssistant.Infrastructure.Authentication;
using LiveCaptionAssistant.Infrastructure.OpenAI;
using LiveCaptionAssistant.Infrastructure.Platform;
using LiveCaptionAssistant.Infrastructure.Speech;
using LiveCaptionAssistant.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCaptionAssistant.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddLiveCaptionInfrastructure(this IServiceCollection services)
    {
        var userAgent = $"LiveCaptionAssistant/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";

        services.AddHttpClient(HttpClientNames.OpenAIApi, client =>
        {
            client.BaseAddress = new Uri("https://api.openai.com/v1/");
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });
        services.AddHttpClient(HttpClientNames.OpenAIAuth, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });

        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IBrowserLauncher, ShellBrowserLauncher>();

        services.AddSingleton<IAudioDeviceCatalog, WasapiAudioDeviceCatalog>();
        services.AddSingleton<IAudioCaptureSourceFactory, WasapiAudioCaptureSourceFactory>();

        services.AddSingleton<WhisperModelStore>();
        services.AddSingleton<ISpeechModelCatalog, WhisperSpeechModelCatalog>();
        services.AddSingleton<ISpeechToTextEngine, WhisperSpeechToTextEngine>();

        services.AddSingleton<ChatGptAuthStateStore>();
        services.AddSingleton<ChatGptTokenClient>();
        services.AddSingleton<OpenIdTokenValidator>();
        services.AddSingleton<ChatGptAuthService>();
        services.AddSingleton<IChatGptAuthenticator>(provider => provider.GetRequiredService<ChatGptAuthService>());
        services.AddSingleton<IAccessTokenProvider>(provider => provider.GetRequiredService<ChatGptAuthService>());

        services.AddSingleton<IChatResponseClient, OpenAiResponsesClient>();
        services.AddSingleton<IModelCatalogClient, OpenAiModelCatalogClient>();
        return services;
    }
}
