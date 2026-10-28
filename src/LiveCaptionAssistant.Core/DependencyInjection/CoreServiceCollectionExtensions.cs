using LiveCaptionAssistant.Core.Assistant;
using LiveCaptionAssistant.Core.Captioning;
using LiveCaptionAssistant.Core.Conversation;
using LiveCaptionAssistant.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCaptionAssistant.Core.DependencyInjection;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddLiveCaptionCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ConversationTranscript>();
        services.AddSingleton<IQuestionDetector, HeuristicQuestionDetector>();
        services.AddSingleton<CaptionSession>();
        services.AddSingleton<AssistantService>();
        return services;
    }
}
