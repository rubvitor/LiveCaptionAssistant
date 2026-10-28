using System.Windows;
using LiveCaptionAssistant.App.Services;
using LiveCaptionAssistant.App.ViewModels;
using LiveCaptionAssistant.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCaptionAssistant.App.DependencyInjection;

internal static class AppServiceCollectionExtensions
{
    public static IServiceCollection AddLiveCaptionApp(this IServiceCollection services)
    {
        services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Application.Current.Dispatcher));
        services.AddSingleton<IClipboardService, WpfClipboardService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<GlobalHotkeyService>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        return services;
    }
}
