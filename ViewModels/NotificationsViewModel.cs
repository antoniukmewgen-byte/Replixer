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
/// Each toast auto-dismisses after a fixed duration with a fade-out.
/// Must be created on the UI thread (DispatcherTimer requires it).
/// </summary>
public sealed class NotificationsViewModel
{
    // Durations
    private const double SuccessSeconds = 3.0;
    private const double ErrorSeconds   = 5.0;
    private const double FadeSeconds    = 0.4; // must match XAML animation duration

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
        var item     = new NotificationItem(message, type);
        double total = type == NotificationType.Error ? ErrorSeconds : SuccessSeconds;

        Items.Add(item);

        // After (total - FadeSeconds): start fade-out animation by flipping IsLeaving.
        var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(total - FadeSeconds) };
        fadeTimer.Tick += (_, _) => { fadeTimer.Stop(); item.IsLeaving = true; };
        fadeTimer.Start();

        // After full duration: remove from collection (animation will have finished by now).
        var removeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(total) };
        removeTimer.Tick += (_, _) => { removeTimer.Stop(); Items.Remove(item); };
        removeTimer.Start();
    }
}
