using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Diagnostics;
using Replixer.Services;

namespace Replixer.Services.Audio;

public class MicrophoneMonitorService : IMonitorService
{
    private Timer? _pollTimer;
    private volatile bool _isCallActive;
    private string _activeApp = string.Empty;
    private bool _registryErrorReported;

    public event Action<string>? CallDetected;
    public event Action<string>? CallEnded;

    private readonly string[] _targetProcesses = { "Telegram", "WhatsApp", "Viber", "Ringostat" };

    public void Start()
    {
        _pollTimer = new Timer(PollCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void PollCallback(object? state)
    {
        string? micApp   = GetMicrophoneActiveApp();
        string? audioApp = GetAudioOutputActiveApp();

        if (!_isCallActive)
        {
            if (micApp != null && audioApp != null &&
                string.Equals(micApp, audioApp, StringComparison.OrdinalIgnoreCase))
            {
                _isCallActive = true;
                _activeApp    = micApp;
                Debug.WriteLine($"[AudioMonitor] {micApp} — call started (mic + speaker)");
                CallDetected?.Invoke(micApp);
            }
        }
        else
        {
            bool micActive   = string.Equals(micApp,   _activeApp, StringComparison.OrdinalIgnoreCase);
            bool audioActive = string.Equals(audioApp, _activeApp, StringComparison.OrdinalIgnoreCase);

            if (!micActive && !audioActive)
            {
                _isCallActive = false;
                Debug.WriteLine($"[AudioMonitor] {_activeApp} — call ended (mic + speaker both gone)");
                CallEnded?.Invoke(_activeApp);
                _activeApp = string.Empty;
            }
        }
    }

    private string? GetMicrophoneActiveApp()
    {
        const string rootPath =
            @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

        // The registry key can be absent (privacy tools, kiosk images) or become
        // temporarily inaccessible between GetSubKeyNames() and OpenSubKey() (TOCTOU).
        // An unhandled exception here would crash the process because this runs on a
        // ThreadPool timer callback in .NET 5+.
        try
        {
            using var rootKey = Registry.CurrentUser.OpenSubKey(rootPath);
            _registryErrorReported = false; // reset: successful access
            if (rootKey == null) return null;

            using var nonPackagedKey = rootKey.OpenSubKey("NonPackaged");
            if (nonPackagedKey != null)
            {
                foreach (var keyName in nonPackagedKey.GetSubKeyNames())
                {
                    var match = _targetProcesses.FirstOrDefault(p =>
                        keyName.Contains(p, StringComparison.OrdinalIgnoreCase));

                    if (match != null && IsMicActiveInSubKey(nonPackagedKey.OpenSubKey(keyName)))
                        return match;
                }
            }

            foreach (var subKeyName in rootKey.GetSubKeyNames())
            {
                if (subKeyName == "NonPackaged") continue;

                var match = _targetProcesses.FirstOrDefault(p =>
                    subKeyName.Contains(p, StringComparison.OrdinalIgnoreCase));

                if (match == null) continue;

                using var pkgKey = rootKey.OpenSubKey(subKeyName);
                if (pkgKey == null) continue;

                var directStop = pkgKey.GetValue("LastUsedTimeStop");
                if (directStop is long t0 && t0 == 0)
                    return match;

                foreach (var childName in pkgKey.GetSubKeyNames())
                {
                    if (IsMicActiveInSubKey(pkgKey.OpenSubKey(childName)))
                        return match;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioMonitor] Registry read failed: {ex.Message}");
            // Report only the first occurrence — the poll runs every second, so we
            // suppress repeats until the next successful read resets the flag.
            if (!_registryErrorReported)
            {
                _registryErrorReported = true;
                ErrorReporter.Report("MIC_MONITOR", $"Помилка читання реєстру мікрофону: {ex.Message}", ex);
            }
            return null;
        }
    }

    private static bool IsMicActiveInSubKey(RegistryKey? subKey)
    {
        if (subKey == null) return false;
        using (subKey)
        {
            var value = subKey.GetValue("LastUsedTimeStop");
            return value is long stopTime && stopTime == 0;
        }
    }

    private string? GetAudioOutputActiveApp()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            var roles = new[] { Role.Multimedia, Role.Communications };
            foreach (var role in roles)
            {
                MMDevice? device = null;
                try { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role); }
                catch { continue; }

                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (session.State != AudioSessionState.AudioSessionStateActive) continue;

                        try
                        {
                            var process = Process.GetProcessById((int)session.GetProcessID);
                            var match   = _targetProcesses.FirstOrDefault(p =>
                                process.ProcessName.Contains(p, StringComparison.OrdinalIgnoreCase));

                            if (match != null)
                            {
                                Debug.WriteLine($"[AudioMonitor] Output session active: {process.ProcessName} (role={role})");
                                return match;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[AudioMonitor] GetProcessById failed: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioMonitor] WASAPI error: {ex.Message}");
        }

        return null;
    }

    public void Stop() => _pollTimer?.Dispose();
    public void Dispose() => Stop();
}
