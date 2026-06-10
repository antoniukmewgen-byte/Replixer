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
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--uninstall"))
        {
            AutoStartManager.SetState(false);
            Shutdown();
            return;
        }

        // Prevent two instances from running simultaneously — a second instance would
        // try to open the same WTelegram session file and fail with IOException.
        // "Local\" scope is sufficient (same user session); "Global\" can throw
        // UnauthorizedAccessException in restricted environments.
        const string MutexName = "Local\\Replixer_SingleInstance";
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                _instanceMutex.Dispose();
                _instanceMutex = null;
                BringExistingInstanceToFront();
                Shutdown();
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Mutex check failed: {ex.Message}");
            // If we can't create the mutex at all, allow the instance to start
            // rather than blocking the app entirely.
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

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void BringExistingInstanceToFront()
    {
        var current = System.Diagnostics.Process.GetCurrentProcess();
        var existing = System.Diagnostics.Process
            .GetProcessesByName(current.ProcessName)
            .FirstOrDefault(p => p.Id != current.Id);

        if (existing?.MainWindowHandle is { } hwnd && hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, 9 /* SW_RESTORE */);
            SetForegroundWindow(hwnd);
        }
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
