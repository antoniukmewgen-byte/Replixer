using Replixer.Services.Window.Detectors;
using FlaUI.UIA3;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Replixer.Services.Window;

public class WindowMonitorService : IMonitorService
{
    private readonly IReadOnlyList<ICallDetector> _detectors;
    private readonly ConcurrentDictionary<string, bool> _callState = new();
    private UIA3Automation? _automation;
    private Timer? _pollTimer;

    public event Action<string>? CallDetected;
    public event Action<string>? CallEnded;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    public WindowMonitorService(IEnumerable<ICallDetector> detectors)
    {
        _detectors = detectors.ToList();
        foreach (var d in _detectors)
            _callState[d.ProcessName] = false;
    }

    public void Start()
    {
        _automation = new UIA3Automation();
        _pollTimer  = new Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void Poll(object? state)
    {
        var automation = _automation;
        if (automation is null) return;

        foreach (var detector in _detectors)
        {
            bool isActive  = IsCallActive(detector, automation);
            bool wasActive = _callState[detector.ProcessName];

            if (isActive && !wasActive)
            {
                _callState[detector.ProcessName] = true;
                CallDetected?.Invoke(detector.ProcessName);
            }
            else if (!isActive && wasActive)
            {
                _callState[detector.ProcessName] = false;
                CallEnded?.Invoke(detector.ProcessName);
            }
        }
    }

    private bool IsCallActive(ICallDetector detector, UIA3Automation automation)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(detector.ProcessName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowMonitor] Error getting process '{detector.ProcessName}': {ex.Message}");
            return false;
        }

        foreach (var process in processes)
        {
            bool found = false;

            try
            {
                EnumWindows((hWnd, _) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid != (uint)process.Id) return true;

                    if (!IsWindowVisible(hWnd)) return true;

                    var titleBuf = new System.Text.StringBuilder(256);
                    if (GetWindowText(hWnd, titleBuf, titleBuf.Capacity) == 0) return true;

                    try
                    {
                        var element = automation.FromHandle(hWnd);
                        if (element != null && detector.IsCallWindow(element))
                        {
                            Debug.WriteLine($"[WindowMonitor] [{detector.ProcessName}] Call window found!");
                            found = true;
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WindowMonitor] UIA access failed: {ex.Message}");
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowMonitor] [{detector.ProcessName}] EnumWindows error: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }

            if (found) return true;
        }

        return false;
    }

    public void Stop()
    {
        // Change() disables future firings immediately without blocking.
        // We null out _automation before disposing it so that any Poll() callback
        // already running captures null on its next check and exits early,
        // rather than calling into a half-disposed UIA3Automation instance.
        _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _pollTimer?.Dispose();
        _pollTimer = null;

        var automation = _automation;
        _automation    = null;
        automation?.Dispose();

        // Reset all call states so that when this monitor is restarted (or swapped out
        // for another monitor mode) stale "active" flags don't produce phantom CallEnded
        // or suppress the first real CallDetected event.
        foreach (var key in _callState.Keys)
            _callState[key] = false;
    }

    public void Dispose() => Stop();
}
