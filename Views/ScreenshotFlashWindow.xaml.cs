using Replixer.Infrastructure;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace Replixer.Views;

// Короткий візуальний "спалах" на весь екран (усі монітори, у фірмовому зеленому кольорі
// додатку) — щоб менеджер бачив явне підтвердження "скрін зроблено", навіть якщо Telegram/
// Viber/WhatsApp у цей момент повністю перекриває головне вікно Replixer і жодного іншого
// індикатора не видно. Вікно саме себе закриває по завершенню анімації (Completed → Close).
public partial class ScreenshotFlashWindow : Window
{
    public ScreenshotFlashWindow()
    {
        InitializeComponent();

        // VirtualScreen* охоплює всі монітори одним прямокутником (не лише основний) —
        // на відміну від Width/Height (лише основний екран), щоб спалах було видно незалежно
        // від того, на якому моніторі саме зараз відкритий месенджер.
        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        SourceInitialized += OnSourceInitialized;
        Loaded            += OnLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = ScreenshotFlashInterop.GetWindowLong(hwnd, ScreenshotFlashInterop.GWL_EXSTYLE);
        ScreenshotFlashInterop.SetWindowLong(hwnd, ScreenshotFlashInterop.GWL_EXSTYLE,
            exStyle | ScreenshotFlashInterop.WS_EX_TRANSPARENT | ScreenshotFlashInterop.WS_EX_LAYERED);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 0 → 1 за 90мс (спалах), тримаємо до 220мс, згасаємо до 0 до 450мс — потім самозакриття.
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450))));
        animation.Completed += (_, _) => Close();

        BeginAnimation(OpacityProperty, animation);
    }
}
