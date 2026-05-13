using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using Replixer.Models;
using Replixer.Services.Manager;
using Replixer.Services.Upload;
using Replixer.ViewModels;
using Replixer.Views;
using System.Windows;

namespace Replixer;

public partial class App : Application
{
    public static IWindowManager WindowManager { get; } = new WindowManager();

    private ServiceProvider? _services;
    private TaskbarIcon? _notifyIcon;
    private bool _mainWindowStarted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool startInTray = e.Args.Contains("--tray");

        _services = BuildServices();

        var settings = _services.GetRequiredService<AppSettings>();
        if (!settings.IsSetupComplete)
            ShowSetupWindow();
        else
            ShowMainWindow(startInTray);
    }

    private void ShowSetupWindow()
    {
        var setupVm     = _services!.GetRequiredService<SetupViewModel>();
        var setupWindow = _services.GetRequiredService<SetupWindow>();

        Application.Current.MainWindow = setupWindow;

        setupVm.SetupCompleted += () =>
        {
            setupWindow.Close();
            ShowMainWindow();
        };

        setupWindow.Closed += (_, _) =>
        {
            if (!_services!.GetRequiredService<AppSettings>().IsSetupComplete)
                Shutdown();
        };

        setupWindow.Show();
    }

    private void ShowMainWindow(bool startInTray = false)
    {
        _mainWindowStarted = true;

        var settings = _services!.GetRequiredService<AppSettings>();
        AutoStartManager.SetState(settings.IsAutoStartEnabled);

        var mainWindow = _services.GetRequiredService<MainWindow>();
        Application.Current.MainWindow = mainWindow;

        if (!startInTray)
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
        if (_mainWindowStarted)
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

        // Setup
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<SetupWindow>();

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
