using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using SmartEditor.App.Services;
using SmartEditor.App.ViewModels;
using SmartEditor.App.Views;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Services;

namespace SmartEditor.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static Action<IServiceCollection>? ConfigurePlatformServices { get; set; }

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
        _ = Services.GetRequiredService<GenerationRecoveryService>().RecoverPendingAsync();

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
        services.AddSingleton<AssetThumbnailCache>();
        services.AddSingleton<GenerationRecoveryStore>();
        services.AddSingleton<IFilePickerService, AvaloniaFilePickerService>();
        services.AddSingleton<IGenerationKeepAlive, NoOpGenerationKeepAlive>();
        ConfigurePlatformServices?.Invoke(services);

        var workflowsDirectory = Path.Combine(AppContext.BaseDirectory, "Workflows");
        var guidanceDirectory = Path.Combine(AppContext.BaseDirectory, "Guidance");
        EnsureBundledFilesExtracted("Workflows", workflowsDirectory);
        EnsureBundledFilesExtracted("Guidance", guidanceDirectory);

        services.AddSingleton<ManagedWorkflowCatalog>(_ => new ManagedWorkflowCatalog(new FileWorkflowCatalog(workflowsDirectory), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartEditor", "CustomWorkflows")));
        services.AddSingleton<IWorkflowCatalog>(sp => sp.GetRequiredService<ManagedWorkflowCatalog>());
        services.AddSingleton<WorkflowsViewModel>();
        services.AddSingleton<IModelGuidanceCatalog>(_ =>
            new FileModelGuidanceCatalog(Path.Combine(guidanceDirectory, "model-guidance.json")));
        services.AddSingleton<IEditSessionFactory, EditSessionFactory>();

        services.AddTransient<EditorViewModel>();
        // Each editor tab needs its own EditorViewModel instance created on demand (not just one
        // resolved at ShellViewModel construction time), so ShellViewModel takes a factory delegate
        // instead of a concrete instance.
        services.AddTransient<Func<EditorViewModel>>(sp => sp.GetRequiredService<EditorViewModel>);
        // One Assets tab for the whole app, unlike editor tabs — a true singleton, not per-tab.
        services.AddSingleton<AssetsViewModel>();
        services.AddSingleton<GenerationRecoveryService>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ShellViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>Copies every <c>avares://SmartEditor.App/{avaresFolderName}/...</c> resource (see
    /// the matching <c>AvaloniaResource</c> items in SmartEditor.App.csproj) into
    /// <paramref name="targetDirectory"/>, so <see cref="FileWorkflowCatalog"/> and
    /// <see cref="FileModelGuidanceCatalog"/> can keep reading real files from a real path on every
    /// platform. Each embedded file is content-hash checked on launch and replaced only if it has
    /// changed. This keeps Android installs current across app upgrades while preserving custom
    /// workflows, which live in a separate application-data directory.</summary>
    private static void EnsureBundledFilesExtracted(string avaresFolderName, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        var folderUri = new Uri($"avares://SmartEditor.App/{avaresFolderName}/");
        var assetUris = AssetLoader.GetAssets(folderUri, null).ToList();
        var bundledPaths = assetUris.Select(assetUri =>
        {
            var relativePath = assetUri.AbsolutePath[folderUri.AbsolutePath.Length..].TrimStart('/');
            return Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }).ToHashSet(StringComparer.Ordinal);

        // Bundled workflows are never user-owned (custom workflows are stored elsewhere), so a
        // removed resource must also disappear after an upgrade rather than remain selectable.
        foreach (var existingPath in Directory.EnumerateFiles(targetDirectory, "*.json"))
        {
            if (!bundledPaths.Contains(existingPath)) File.Delete(existingPath);
        }

        foreach (var assetUri in assetUris)
        {
            var relativePath = assetUri.AbsolutePath[folderUri.AbsolutePath.Length..].TrimStart('/');
            var destinationPath = Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            using var source = AssetLoader.Open(assetUri);
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            var bundledBytes = memory.ToArray();
            if (File.Exists(destinationPath) &&
                SHA256.HashData(File.ReadAllBytes(destinationPath)).AsSpan().SequenceEqual(SHA256.HashData(bundledBytes)))
            {
                continue;
            }

            var temporaryPath = destinationPath + ".tmp";
            File.WriteAllBytes(temporaryPath, bundledBytes);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
    }
}
