using EchoVault.Services.CallDetectors;
using FlaUI.UIA3;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EchoVault.Services;

public class WindowMonitorService : IDisposable
{
    private readonly IReadOnlyList<ICallDetector> _detectors;
    private readonly Dictionary<string, bool> _callState = new();
    private UIA3Automation? _automation;
    private Timer? _pollTimer;

    public event Action<string>? CallDetected;
    public event Action<string>? CallEnded;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public WindowMonitorService(IEnumerable<ICallDetector> detectors)
    {
        _detectors = detectors.ToList();
        foreach (var d in _detectors)
            _callState[d.ProcessName] = false;
    }

    public void Start()
    {
        _automation = new UIA3Automation();
        _pollTimer = new Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void Poll(object? state)
    {
        if (_automation is null) return;

        foreach (var detector in _detectors)
        {
            bool isActive = IsCallActive(detector);
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

    private bool IsCallActive(ICallDetector detector)
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

                    try
                    {
                        var element = _automation!.FromHandle(hWnd);
                        if (element != null && detector.IsCallWindow(element))
                        {
                            Debug.WriteLine($"[WindowMonitor] [{detector.ProcessName}] Call window found!");
                            found = true;
                            return false;
                        }
                    }
                    catch { }

                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowMonitor] [{detector.ProcessName}] EnumWindows error: {ex.Message}");
            }

            if (found) return true;
        }

        return false;
    }

    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        _automation?.Dispose();
        _automation = null;
    }

    public void Dispose() => Stop();
}