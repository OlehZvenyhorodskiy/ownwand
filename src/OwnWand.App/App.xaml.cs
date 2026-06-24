using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OwnWand.App.Services;
using OwnWand.App.ViewModels;

namespace OwnWand.App;

/// <summary>
/// Application entry point. Configures the Generic Host with dependency injection
/// for all services, view-models, and the main window.
/// </summary>
public partial class App : Application
{
    private static readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureServices((context, services) =>
        {
            // Core services
            services.AddSingleton<PresetService>();
            services.AddSingleton<ProcessService>();
            services.AddSingleton<GameDetectionService>();
            services.AddSingleton<InjectionService>();
            services.AddSingleton<IpcService>();
            services.AddSingleton<ProfileService>();
            services.AddSingleton<SettingsService>();

            // View-models
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<GameLibraryViewModel>();
            services.AddTransient<CheatPanelViewModel>();
            services.AddSingleton<SettingsViewModel>();

            // Main window
            services.AddSingleton<MainWindow>();
        })
        .Build();

    /// <summary>
    /// Gets the application-wide service provider for resolving dependencies.
    /// </summary>
    public static IServiceProvider Services => _host.Services;

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    /// <inheritdoc />
    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }
}
