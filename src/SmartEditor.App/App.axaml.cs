using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SmartEditor.App.Services;
using SmartEditor.App.ViewModels;
using SmartEditor.App.Views;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Services;

namespace SmartEditor.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = BuildServiceProvider();
        var shell = Services.GetRequiredService<ShellViewModel>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = shell,
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            activityLifetime.MainViewFactory = () => new MainView { DataContext = shell };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = shell,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddHttpClient();
        // Auto-redirect disabled: ComfyCloudClient follows Comfy Cloud's /api/view redirect to a
        // signed third-party URL manually, so it never forwards the Cloud API key to that host.
        services.AddHttpClient(EditSessionFactory.ComfyCloudHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<AssetTagsStore>();
        services.AddSingleton<IFilePickerService, AvaloniaFilePickerService>();
        services.AddSingleton<IWorkflowCatalog>(_ =>
            new FileWorkflowCatalog(Path.Combine(AppContext.BaseDirectory, "Workflows")));
        services.AddSingleton<IModelGuidanceCatalog>(_ =>
            new FileModelGuidanceCatalog(Path.Combine(AppContext.BaseDirectory, "Guidance", "model-guidance.json")));
        services.AddSingleton<IEditSessionFactory, EditSessionFactory>();

        services.AddTransient<EditorViewModel>();
        // Each editor tab needs its own EditorViewModel instance created on demand (not just one
        // resolved at ShellViewModel construction time), so ShellViewModel takes a factory delegate
        // instead of a concrete instance.
        services.AddTransient<Func<EditorViewModel>>(sp => sp.GetRequiredService<EditorViewModel>);
        // One Assets tab for the whole app, unlike editor tabs — a true singleton, not per-tab.
        services.AddSingleton<AssetsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ShellViewModel>();

        return services.BuildServiceProvider();
    }
}
