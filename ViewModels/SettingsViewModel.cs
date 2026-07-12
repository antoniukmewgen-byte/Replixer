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

    public int WorkDayStartHour
    {
        get => _settings.WorkDayStart.Hours;
        set => _settings.WorkDayStart = new TimeSpan(value, _settings.WorkDayStart.Minutes, 0);
    }

    public int WorkDayStartMinute
    {
        get => _settings.WorkDayStart.Minutes;
        set => _settings.WorkDayStart = new TimeSpan(_settings.WorkDayStart.Hours, value, 0);
    }

    public int WorkDayEndHour
    {
        get => _settings.WorkDayEnd.Hours;
        set => _settings.WorkDayEnd = new TimeSpan(value, _settings.WorkDayEnd.Minutes, 0);
    }

    public int WorkDayEndMinute
    {
        get => _settings.WorkDayEnd.Minutes;
        set => _settings.WorkDayEnd = new TimeSpan(_settings.WorkDayEnd.Hours, value, 0);
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
        else if (e.PropertyName == nameof(AppSettings.WorkDayStart))
        {
            OnPropertyChanged(nameof(WorkDayStartHour));
            OnPropertyChanged(nameof(WorkDayStartMinute));
        }
        else if (e.PropertyName == nameof(AppSettings.WorkDayEnd))
        {
            OnPropertyChanged(nameof(WorkDayEndHour));
            OnPropertyChanged(nameof(WorkDayEndMinute));
        }
    }
}
