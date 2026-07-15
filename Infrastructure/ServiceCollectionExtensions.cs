using Microsoft.Extensions.DependencyInjection;
using Replixer.Models;
using Replixer.Services;
using Replixer.Services.Manager;
using Replixer.Services.Recording;
using Replixer.Services.Upload;
using Replixer.ViewModels;
using Replixer.Views;

namespace Replixer.Infrastructure;

internal static class ServiceCollectionExtensions
{
    internal static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton(AppSettings.Load());
        services.AddSingleton<IWindowManager, WindowManager>();
        services.AddSingleton<UpdateService>();

        return services;
    }

    internal static IServiceCollection AddUploadServices(this IServiceCollection services)
    {
        services.AddSingleton<GoogleDriveUploadService>();
        services.AddSingleton<TelegramUploadService>();
        services.AddSingleton<KommoService>();
        services.AddSingleton<IUploadOrchestrator, UploadOrchestrator>();
        services.AddSingleton<PendingUploadRetryService>();
        return services;
    }

    internal static IServiceCollection AddRecordingServices(this IServiceCollection services)
    {
        services.AddSingleton<AudioRecordingService>();
        return services;
    }

    internal static IServiceCollection AddViewModels(this IServiceCollection services)
    {
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<RecordingsViewModel>();
        services.AddSingleton<ProfileViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<NotificationsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<TrayViewModel>();
        return services;
    }

    internal static IServiceCollection AddViews(this IServiceCollection services)
    {
        services.AddTransient<SetupWindow>();
        services.AddTransient<MainWindow>();
        return services;
    }
}
