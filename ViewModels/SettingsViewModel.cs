using Replixer.Models;
using System.ComponentModel;

namespace Replixer.ViewModels;

public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettings _settings;

    public bool IsWindowMonitorActive
    {
        get => _settings.MonitorMode == MonitorMode.Window;
        set
        {
            if (!value) return;
            _settings.MonitorMode = MonitorMode.Window;
        }
    }

    public bool IsMicrophoneMonitorActive
    {
        get => _settings.MonitorMode == MonitorMode.Microphone;
        set
        {
            if (!value) return;
            _settings.MonitorMode = MonitorMode.Microphone;
        }
    }

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        _settings.PropertyChanged += OnSettingsChanged;
    }

    public void Dispose() => _settings.PropertyChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.MonitorMode))
        {
            OnPropertyChanged(nameof(IsWindowMonitorActive));
            OnPropertyChanged(nameof(IsMicrophoneMonitorActive));
        }
    }
}
