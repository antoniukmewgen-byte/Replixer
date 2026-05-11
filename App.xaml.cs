using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using Replixer.Models;
using Replixer.Services.Manager;
using Replixer.Services.Upload;
using Replixer.ViewModels;
using System.Windows;

namespace Replixer;

public partial class App : Application
{
    public static IWindowManager WindowManager { get; } = new WindowManager();

    private ServiceProvider? _services;
    private TaskbarIcon? _notifyIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = BuildServices();

        var mainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

        _notifyIcon = (TaskbarIcon)FindResource("NotifyIcon");

        var trayVm = _services.GetRequiredService<TrayViewModel>();
        _notifyIcon.DataContext = trayVm;
        trayVm.BalloonRequested += (title, msg) =>
            _notifyIcon.ShowBalloonTip(title, msg, BalloonIcon.Info);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<AppSettings>()?.Flush();
        _services?.GetService<MainViewModel>()?.Dispose();
        _notifyIcon?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // Core
        services.AddSingleton(AppSettings.Load());

        // Upload
        services.AddSingleton<GoogleDriveUploadService>();
        services.AddSingleton<TelegramUploadService>();
        services.AddSingleton<IUploadOrchestrator, UploadOrchestrator>();

        // ViewModels
        services.AddSingleton<RecordingsViewModel>();
        services.AddSingleton<ProfileViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<TrayViewModel>();

        // View
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
