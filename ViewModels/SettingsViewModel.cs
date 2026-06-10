using Replixer.Models;
using Replixer.Services;
using Replixer.Services.Manager;
using System.ComponentModel;

namespace Replixer.ViewModels;

public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettings _settings;

    public string AppVersion => $"v{UpdateService.GetCurrentVersion().ToString(3)}";

    public bool IsAutoStartEnabled
    {
        get => _settings.IsAutoStartEnabled;
        set
        {
            _settings.IsAutoStartEnabled = value;
            AutoStartManager.SetState(value);
        }
    }

    public bool IsNotificationsEnabled
    {
        get => _settings.IsNotificationsEnabled;
        set => _settings.IsNotificationsEnabled = value;
    }

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        _settings.PropertyChanged += OnSettingsChanged;
    }

    public void Dispose() => _settings.PropertyChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.IsAutoStartEnabled))
        {
            OnPropertyChanged(nameof(IsAutoStartEnabled));
        }
        else if (e.PropertyName == nameof(AppSettings.IsNotificationsEnabled))
        {
            OnPropertyChanged(nameof(IsNotificationsEnabled));
        }
    }
}
