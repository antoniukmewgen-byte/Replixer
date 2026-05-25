using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services.Manager;
using Replixer.ViewModels;
using Replixer.Views;
using System.Windows;

namespace Replixer;

public partial class App : Application
{
    private ServiceProvider? _services;
    private TaskbarIcon? _notifyIcon;
    private bool _mainWindowStarted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--uninstall"))
        {
            AutoStartManager.SetState(false);
            Shutdown();
            return;
        }

        bool startInTray = e.Args.Contains("--tray");

        _services = BuildServices();

        _services.GetRequiredService<AppSettings>().InitializeDispatch();

        var settings = _services.GetRequiredService<AppSettings>();
        if (!settings.IsSetupComplete)
            ShowSetupWindow();
        else
            ShowMainWindow(startInTray);
    }

    private void ShowSetupWindow()
    {
        var setupVm     = _services!.GetRequiredService<SetupViewModel>();
        var setupWindow = _services!.GetRequiredService<SetupWindow>();

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

        var mainWindow = _services!.GetRequiredService<MainWindow>();
        Application.Current.MainWindow = mainWindow;

        if (!startInTray)
            mainWindow.Show();

        _notifyIcon = (TaskbarIcon)FindResource("NotifyIcon");

        var trayVm = _services!.GetRequiredService<TrayViewModel>();
        _notifyIcon.DataContext = trayVm;
        trayVm.BalloonRequested += (title, msg) =>
            _notifyIcon.ShowBalloonTip(title, msg, BalloonIcon.Info);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<AppSettings>()?.Flush();
        if (_mainWindowStarted)
            _services?.GetService<MainViewModel>()?.Dispose();

        _services?.GetService<NotificationsViewModel>()?.Dispose();

        _services?.GetService<TrayViewModel>()?.Dispose();

        _notifyIcon?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider BuildServices()
        => new ServiceCollection()
            .AddCoreServices()
            .AddRecordingServices()
            .AddUploadServices()
            .AddViewModels()
            .AddViews()
            .BuildServiceProvider();
}
