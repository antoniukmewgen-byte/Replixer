using Replixer.Models;
using Replixer.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace Replixer.ViewModels;

public sealed class NotificationItem : INotifyPropertyChanged
{
    public string           Message { get; }
    public NotificationType Type    { get; }
    public bool             IsError => Type == NotificationType.Error;

    private bool _isLeaving;
    public bool IsLeaving
    {
        get => _isLeaving;
        internal set { if (_isLeaving == value) return; _isLeaving = value; OnPropertyChanged(); }
    }

    internal Action? AnimationCompleted { get; set; }

    internal NotificationItem(string message, NotificationType type)
    {
        Message = message;
        Type    = type;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class NotificationsViewModel : IDisposable
{
    private const double SuccessSeconds = 3.0;
    private const double ErrorSeconds   = 5.0;
    private const double LeaveSeconds   = 0.4;

    private readonly AppSettings _settings;

    private readonly List<DispatcherTimer> _activeTimers = [];

    public ObservableCollection<NotificationItem> Items { get; } = new();

    public NotificationsViewModel(AppSettings settings)
    {
        _settings = settings;
        NotificationService.Raised += OnRaised;
    }

    private void OnRaised(string message, NotificationType type)
    {
        if (!_settings.IsNotificationsEnabled) return;
        Application.Current.Dispatcher.BeginInvoke(() => Show(message, type));
    }

    private void Show(string message, NotificationType type)
    {
        var item = new NotificationItem(message, type);

        item.AnimationCompleted = () => Items.Remove(item);

        double total = type == NotificationType.Error ? ErrorSeconds : SuccessSeconds;
        Items.Add(item);

        var leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(total - LeaveSeconds) };
        _activeTimers.Add(leaveTimer);
        leaveTimer.Tick += (_, _) =>
        {
            leaveTimer.Stop();
            _activeTimers.Remove(leaveTimer);
            item.IsLeaving = true;
        };
        leaveTimer.Start();
    }

    public void Dispose()
    {
        NotificationService.Raised -= OnRaised;

        foreach (var t in _activeTimers)
            t.Stop();
        _activeTimers.Clear();
    }
}
