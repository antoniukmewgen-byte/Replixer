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
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showSignalWait;

    // Signaled by a second instance (e.g. desktop shortcut) to ask the running
    // instance to restore its window from the tray.
    private const string ShowSignalName = "Local\\Replixer_ShowWindow";

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
                SignalExistingInstance();
                Shutdown();
                return;
            }

            // First instance: listen for the "show window" signal from later launches.
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            _showSignalWait = ThreadPool.RegisterWaitForSingleObject(
                _showSignal,
                (_, _) => Dispatcher.BeginInvoke(RestoreMainWindow),
                state: null,
                millisecondsTimeOutInterval: -1,
                executeOnlyOnce: false);
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
            e.SetObserved();

            // WTelegram fires background KeepAlive/Reactor tasks that throw IOException
            // on network drops — this is expected and not actionable by the user.
            var inner = e.Exception.InnerException ?? e.Exception;
            if (inner is System.IO.IOException or System.Net.Sockets.SocketException &&
                (e.Exception.StackTrace ?? inner.StackTrace ?? "").Contains("WTelegram"))
                return;

            ErrorReporter.Report("TASK_ERROR",
                inner.Message, e.Exception);
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

        _showSignalWait?.Unregister(null);
        _showSignal?.Dispose();

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
            {
                using (signal)
                    signal.Set();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] SignalExistingInstance failed: {ex.Message}");
        }
    }

    // Runs on the UI thread: restores the main window from tray / minimized state.
    private void RestoreMainWindow()
    {
        var window = MainWindow;
        if (window is null) return;

        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
        window.Topmost = true;   // pull above other windows,
        window.Topmost = false;  // then release so it behaves normally
        window.Focus();
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
