using Microsoft.Win32;
using System.Diagnostics;

namespace reRecorder.Services;

public class MicrophoneMonitorService : IDisposable
{
    private System.Threading.Timer? _pollTimer;
    private bool _isCallActive = false;

    public event Action? CallStarted;
    public event Action? CallEnded;

    private readonly string[] _targetProcesses = { "Telegram", "WhatsApp", "Viber" };

    public void Start()
    {
        _pollTimer = new System.Threading.Timer(PollCallback, null,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(1000));
    }

    private void PollCallback(object? state)
    {
        bool isTargetUsingMic = IsTargetProcessUsingMicrophone();

        if (isTargetUsingMic && !_isCallActive)
        {
            _isCallActive = true;
            Debug.WriteLine("[MicMonitor] Target messenger started using microphone");
            CallStarted?.Invoke();
        }
        else if (!isTargetUsingMic && _isCallActive)
        {
            _isCallActive = false;
            Debug.WriteLine("[MicMonitor] Target messenger stopped using microphone");
            CallEnded?.Invoke();
        }
    }

    private bool IsTargetProcessUsingMicrophone()
    {
        string rootPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        using var rootKey = Registry.CurrentUser.OpenSubKey(rootPath);
        if (rootKey == null) return false;

        using var nonPackagedKey = rootKey.OpenSubKey("NonPackaged");
        if (nonPackagedKey != null)
        {
            foreach (var keyName in nonPackagedKey.GetSubKeyNames())
            {
                if (_targetProcesses.Any(p => keyName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    if (IsMicActiveInSubKey(nonPackagedKey.OpenSubKey(keyName)))
                        return true;
                }
            }
        }

        foreach (var subKeyName in rootKey.GetSubKeyNames())
        {
            if (subKeyName == "NonPackaged") continue;

            if (_targetProcesses.Any(p => subKeyName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                if (IsMicActiveInSubKey(rootKey.OpenSubKey(subKeyName)))
                    return true;
            }
        }

        return false;
    }

    private bool IsMicActiveInSubKey(RegistryKey? subKey)
    {
        if (subKey == null) return false;
        using (subKey)
        {
            var lastUsedTimeStop = subKey.GetValue("LastUsedTimeStop");
            return lastUsedTimeStop is long stopTime && stopTime == 0;
        }
    }

    public void Stop() => _pollTimer?.Dispose();
    public void Dispose() => Stop();
}