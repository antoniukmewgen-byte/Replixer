using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services;
using Replixer.Services.Manager;
using Replixer.ViewModels;
using Replixer.Views;
using System.IO;
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

        ErrorReporter.Configure(settings);
        _ = ErrorReporter.FlushQueueAsync();
        ReportPendingUpdateError();

        DispatcherUnhandledException += (_, e) =>
            ErrorReporter.ReportCrash("CRASH", e.Exception.Message, e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                ErrorReporter.ReportCrash("CRASH_FATAL", ex.Message, ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorReporter.Report("TASK_ERROR",
                e.Exception.InnerException?.Message ?? e.Exception.Message, e.Exception);
            e.SetObserved();
        };

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

        var mainVm     = _services!.GetRequiredService<MainViewModel>();
        var mainWindow = _services!.GetRequiredService<MainWindow>();
        Application.Current.MainWindow = mainWindow;

        if (!startInTray)
            mainWindow.Show();

        _ = mainVm.StartupUpdateCheckAsync();

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

        // Flush pending recordings.json write before ServiceProvider tears down singletons.
        _services?.GetService<RecordingsViewModel>()?.Dispose();

        _services?.GetService<NotificationsViewModel>()?.Dispose();
        _services?.GetService<TrayViewModel>()?.Dispose();

        _notifyIcon?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }

    // If the PowerShell update script failed, it writes the error to this log file.
    // We pick it up on the next launch so the failure reaches Telegram via ErrorReporter.
    private static void ReportPendingUpdateError()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "replixer_update.log");
        try
        {
            if (!File.Exists(logPath)) return;
            var text = File.ReadAllText(logPath).Trim();
            File.Delete(logPath);
            if (!string.IsNullOrEmpty(text))
                ErrorReporter.Report("UPDATE_SCRIPT", $"Помилка PS-скрипту оновлення:\n{text}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] ReportPendingUpdateError failed: {ex.Message}");
        }
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
