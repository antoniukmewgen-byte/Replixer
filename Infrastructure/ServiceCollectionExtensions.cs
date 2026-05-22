using Microsoft.Extensions.DependencyInjection;
using Replixer.Models;
using Replixer.Services.Manager;
using Replixer.Services.Upload;
using Replixer.ViewModels;
using Replixer.Views;

namespace Replixer.Infrastructure;

/// <summary>
/// Groups DI registrations by layer so App.xaml.cs stays a thin entry point.
/// </summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>Core models and cross-cutting infrastructure.</summary>
    internal static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton(AppSettings.Load());
        services.AddSingleton<IWindowManager, WindowManager>();
        return services;
    }

    /// <summary>Upload / integration services (Drive, Telegram, Kommo).</summary>
    internal static IServiceCollection AddUploadServices(this IServiceCollection services)
    {
        services.AddSingleton<GoogleDriveUploadService>();
        services.AddSingleton<TelegramUploadService>();
        services.AddSingleton<KommoService>();
        services.AddSingleton<IUploadOrchestrator, UploadOrchestrator>();
        return services;
    }

    /// <summary>All ViewModels (Setup + Main shell + pages).</summary>
    internal static IServiceCollection AddViewModels(this IServiceCollection services)
    {
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<RecordingsViewModel>();
        services.AddSingleton<ProfileViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<TrayViewModel>();
        return services;
    }

    /// <summary>WPF windows.</summary>
    internal static IServiceCollection AddViews(this IServiceCollection services)
    {
        services.AddSingleton<SetupWindow>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
