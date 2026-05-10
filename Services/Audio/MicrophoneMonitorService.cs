using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Diagnostics;

namespace Replixer.Services.Audio;

public class MicrophoneMonitorService : IMonitorService
{
    private Timer? _pollTimer;
    private bool _isCallActive = false;
    private string _activeApp = string.Empty;

    public event Action<string>? CallDetected;
    public event Action<string>? CallEnded;

    private readonly string[] _targetProcesses = { "Telegram", "WhatsApp", "Viber", "Ringostat" };

    public void Start()
    {
        _pollTimer = new Timer(PollCallback, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(1000));
    }

    private void PollCallback(object? state)
    {
        string? micApp   = GetMicrophoneActiveApp();
        string? audioApp = GetAudioOutputActiveApp();

        if (!_isCallActive)
        {
            // START: both mic AND speaker must be active for the same app
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
            // END: BOTH mic AND speaker must be gone for the app that started the call.
            // Muting the mic while speaker is still active → call continues.
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

    // ── Microphone (registry) ─────────────────────────────────────────────────

    private string? GetMicrophoneActiveApp()
    {
        const string rootPath =
            @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

        using var rootKey = Registry.CurrentUser.OpenSubKey(rootPath);
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

            if (match != null && IsMicActiveInSubKey(rootKey.OpenSubKey(subKeyName)))
                return match;
        }

        return null;
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

    // ── Audio output / speaker (WASAPI) ───────────────────────────────────────

    private string? GetAudioOutputActiveApp()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device     = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var sessions = device.AudioSessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State != AudioSessionState.AudioSessionStateActive) continue;

                try
                {
                    var process = Process.GetProcessById((int)session.GetProcessID);

                    var match = _targetProcesses.FirstOrDefault(p =>
                        process.ProcessName.Contains(p, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        Debug.WriteLine($"[AudioMonitor] Output session active: {process.ProcessName}");
                        return match;
                    }
                }
                catch { /* process may have exited */ }
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
