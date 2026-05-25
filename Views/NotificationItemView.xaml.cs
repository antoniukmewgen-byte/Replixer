using Replixer.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Replixer.Views;

public partial class NotificationItemView : UserControl
{
    private NotificationItem? _item;

    public NotificationItemView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_item is not null)
            _item.PropertyChanged -= OnItemPropertyChanged;

        _item = e.NewValue as NotificationItem;

        if (_item is not null)
            _item.PropertyChanged += OnItemPropertyChanged;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotificationItem.IsLeaving) && _item?.IsLeaving == true)
            Dispatcher.BeginInvoke(PlayLeaveAnimation);
    }

    private void PlayLeaveAnimation()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

        // Fade out
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.3)) { EasingFunction = ease };

        // Slide right
        var slide = new DoubleAnimation(50, TimeSpan.FromSeconds(0.3)) { EasingFunction = ease };

        // Collapse height — fires AnimationCompleted when done
        var collapse = new DoubleAnimation(0, TimeSpan.FromSeconds(0.25))
        {
            BeginTime      = TimeSpan.FromSeconds(0.15),
            EasingFunction = ease,
            FillBehavior   = FillBehavior.HoldEnd,
        };
        collapse.Completed += (_, _) => _item?.AnimationCompleted?.Invoke();

        RootBorder.BeginAnimation(OpacityProperty, fade);
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, slide);
        RootBorder.BeginAnimation(MaxHeightProperty, collapse);
    }
}
