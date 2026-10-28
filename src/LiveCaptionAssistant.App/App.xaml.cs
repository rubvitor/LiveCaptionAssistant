using System.IO;
using System.Windows;
using System.Windows.Threading;
using LiveCaptionAssistant.App.DependencyInjection;
using LiveCaptionAssistant.App.Views;
using LiveCaptionAssistant.Core.Authentication;
using LiveCaptionAssistant.Core.DependencyInjection;
using LiveCaptionAssistant.Infrastructure.DependencyInjection;
using LiveCaptionAssistant.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace LiveCaptionAssistant.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\LiveCaptionAssistant.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Live Caption Assistant is already running.", "Live Caption Assistant", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ConfigureLogging();
        RegisterGlobalExceptionHandlers();

        try
        {
            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog();
            builder.Services
                .AddLiveCaptionCore()
                .AddLiveCaptionInfrastructure()
                .AddLiveCaptionApp();
            builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            }));

            _host = builder.Build();
            await _host.StartAsync();

            await _host.Services.GetRequiredService<IChatGptAuthenticator>().RestoreAsync(CancellationToken.None);

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            Log.Information("Live Caption Assistant started.");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed.");
            MessageBox.Show($"Live Caption Assistant could not start:\n\n{ex.Message}", "Live Caption Assistant", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while disposing services.");
        }
        finally
        {
            Log.Information("Live Caption Assistant exited with code {ExitCode}.", e.ApplicationExitCode);
            Log.CloseAndFlush();
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }
    }

    private static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating: {IsTerminating}).", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        Log.Error(args.Exception, "Unhandled UI exception.");
        args.Handled = true;
        MessageBox.Show($"Something went wrong:\n\n{args.Exception.Message}\n\nDetails were written to the log file.",
            "Live Caption Assistant", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
