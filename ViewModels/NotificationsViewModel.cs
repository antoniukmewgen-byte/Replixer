using Replixer.Models;
using Replixer.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace Replixer.ViewModels;

/// <summary>Single toast item — carries message, type and a leaving-flag for the fade-out animation.</summary>
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

    /// <summary>Called by the view when the leave animation finishes — removes item from the collection.</summary>
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

/// <summary>
/// Subscribes to <see cref="NotificationService"/> and manages the observable list of toasts.
/// Each toast auto-dismisses after a fixed duration. Removal happens only after
/// the view signals AnimationCompleted — so the leave animation always plays in full.
/// </summary>
public sealed class NotificationsViewModel
{
    private const double SuccessSeconds = 3.0;
    private const double ErrorSeconds   = 5.0;
    private const double LeaveSeconds   = 0.4; // must match animation duration in NotificationItemView

    private readonly AppSettings _settings;

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

        // View calls this when its leave animation finishes.
        item.AnimationCompleted = () => Items.Remove(item);

        double total = type == NotificationType.Error ? ErrorSeconds : SuccessSeconds;
        Items.Add(item);

        // After (total - LeaveSeconds): flip IsLeaving → view starts leave animation.
        // Removal is NOT scheduled here — it happens via AnimationCompleted callback.
        var leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(total - LeaveSeconds) };
        leaveTimer.Tick += (_, _) => { leaveTimer.Stop(); item.IsLeaving = true; };
        leaveTimer.Start();
    }
}
