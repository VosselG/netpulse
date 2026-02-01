using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetPulse.Services;
using NetPulse.ViewModels;
using NetPulse.Views;

namespace NetPulse;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder(e.Args);
        ConfigureServices(builder.Services);

        _host = builder.Build();
        await _host.StartAsync();

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        // Load persisted state after the UI is up.
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        await viewModel.InitializeAsync(CancellationToken.None);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Ensure persistence completes before process exit (avoid async-void fire-and-forget shutdown).
        if (_host is not null)
        {
            try
            {
                Task.Run(async () =>
                {
                    var viewModel = _host.Services.GetService<MainViewModel>();
                    if (viewModel is not null)
                        await viewModel.ShutdownAsync(CancellationToken.None);

                    await _host.StopAsync(TimeSpan.FromSeconds(5));
                }).GetAwaiter().GetResult();
            }
            finally
            {
                _host.Dispose();
                _host = null;
            }
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IPersistenceService, PersistenceService>();

        // Scanner is stubbed for scaffolding (real implementation comes later)
        services.AddSingleton<IScannerService, ScannerService>();

        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainWindow>();
    }
}