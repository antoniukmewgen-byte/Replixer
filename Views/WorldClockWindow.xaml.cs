using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Replixer.Views;

public partial class WorldClockWindow : Window
{
    // Іменування полів у XAML: Badge{Name}/Icon{Name}/Sub{Name}/Time{Name}.
    // Київ не має Sub-поля (текст "Домашній пояс" фіксований у XAML), тому SubField == null для нього.
    private static readonly (string TimeZoneId, string BadgeField, string IconField, string TimeField, string? SubField)[] Zones =
    [
        ("FLE Standard Time",      nameof(BadgeKyiv),     nameof(IconKyiv),     nameof(TimeKyiv),     null),
        ("Eastern Standard Time",  nameof(BadgeEastern),  nameof(IconEastern),  nameof(TimeEastern),  nameof(SubEastern)),
        ("Central Standard Time",  nameof(BadgeCentral),  nameof(IconCentral),  nameof(TimeCentral),  nameof(SubCentral)),
        ("Mountain Standard Time", nameof(BadgeMountain), nameof(IconMountain), nameof(TimeMountain), nameof(SubMountain)),
        ("Pacific Standard Time",  nameof(BadgePacific),  nameof(IconPacific),  nameof(TimePacific),  nameof(SubPacific)),
        ("Alaskan Standard Time",  nameof(BadgeAlaska),   nameof(IconAlaska),   nameof(TimeAlaska),   nameof(SubAlaska)),
        ("Hawaiian Standard Time", nameof(BadgeHawaii),   nameof(IconHawaii),   nameof(TimeHawaii),   nameof(SubHawaii)),
    ];

    private sealed record ResolvedZone(
        TimeZoneInfo Zone, Border Badge, Path Icon, TextBlock Time, TextBlock? Sub);

    // ── Фазові іконки доби, побудовані геометрично (без залежності від емодзі-шрифтів,
    // яких може не бути в кастомному InterFont) — узгоджено з рештою застосунку, де іконки
    // теж завжди vector Path, як у ProfilePage.
    private static readonly Geometry SunGeometry   = BuildSun();
    private static readonly Geometry MoonGeometry  = BuildMoon();
    private static readonly Geometry HorizonGeometry = BuildHorizon();

    private static Geometry BuildSun()
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(new EllipseGeometry(new Point(12, 12), 5, 5));
        for (var i = 0; i < 8; i++)
        {
            var ray = new RectangleGeometry(new Rect(11, 0.5, 2, 3.5), 1, 1)
            {
                Transform = new RotateTransform(i * 45, 12, 12)
            };
            group.Children.Add(ray);
        }
        group.Freeze();
        return group;
    }

    private static Geometry BuildMoon()
    {
        var big   = new EllipseGeometry(new Point(11, 12), 8, 8);
        var small = new EllipseGeometry(new Point(15.5, 8.5), 7, 7);
        var moon  = new CombinedGeometry(GeometryCombineMode.Exclude, big, small);
        moon.Freeze();
        return moon;
    }

    // Спільна іконка для світанку/заходу (півсонце над горизонтом) — розрізняються кольором
    // рядка (фіолетовий на світанку, бурштиновий на заході), як у реальних погодних застосунках.
    private static Geometry BuildHorizon()
    {
        var circle  = new EllipseGeometry(new Point(12, 13), 6.5, 6.5);
        var topHalf = new RectangleGeometry(new Rect(0, 3, 24, 10));
        var halfSun = new CombinedGeometry(GeometryCombineMode.Intersect, circle, topHalf);
        var line    = new RectangleGeometry(new Rect(2, 18.4, 20, 1.6), 0.8, 0.8);
        var group   = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(halfSun);
        group.Children.Add(line);
        group.Freeze();
        return group;
    }

    private readonly List<ResolvedZone> _resolved = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public WorldClockWindow()
    {
        InitializeComponent();

        // Резолвимо часові пояси один раз при створенні вікна — FindSystemTimeZoneById читає
        // реєстр Windows, тож не варто робити це щосекунди в Tick. Якщо якийсь id відсутній у
        // конкретній збірці ОС (малоймовірно), просто пропускаємо цей рядок, а не крашимо віджет.
        foreach (var (id, badgeField, iconField, timeField, subField) in Zones)
        {
            try
            {
                var zone  = TimeZoneInfo.FindSystemTimeZoneById(id);
                var badge = (Border)FindName(badgeField)!;
                var icon  = (Path)FindName(iconField)!;
                var time  = (TextBlock)FindName(timeField)!;
                var sub   = subField is null ? null : (TextBlock)FindName(subField)!;
                _resolved.Add(new ResolvedZone(zone, badge, icon, time, sub));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WorldClock] Time zone '{id}' not found: {ex.Message}");
            }
        }

        _timer.Tick += (_, _) => UpdateClocks();
        UpdateClocks();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
    }

    private void UpdateClocks()
    {
        var utcNow = DateTime.UtcNow;
        if (_resolved.Count == 0) return;

        var kyiv    = _resolved[0];
        var kyivNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, kyiv.Zone);

        foreach (var rz in _resolved)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, rz.Zone);
            var (icon, bg, accent) = GetPhaseVisual(local.Hour);

            rz.Icon.Data       = icon;
            rz.Icon.Fill       = accent;
            rz.Badge.Background = bg;
            rz.Time.Text        = local.ToString("HH:mm");
            rz.Time.Foreground  = accent;

            if (rz.Sub is null) continue; // Kyiv — фіксований підпис "Домашній пояс" з XAML

            var diffHours = (int)Math.Round((rz.Zone.GetUtcOffset(utcNow) - kyiv.Zone.GetUtcOffset(utcNow)).TotalHours);
            var dayDiff   = (local.Date - kyivNow.Date).Days;
            var diffLabel = diffHours == 0 ? "той самий час" : $"{(diffHours > 0 ? "+" : "")}{diffHours} год від Києва";
            var dayLabel  = dayDiff switch
            {
                > 0 => " · завтра",
                < 0 => " · вчора",
                _   => ""
            };
            rz.Sub.Text = diffLabel + dayLabel;
        }
    }

    private (Geometry Icon, Brush Background, Brush Accent) GetPhaseVisual(int hour) => hour switch
    {
        >= 5 and < 8   => (HorizonGeometry, (Brush)FindResource("OtherBrushColor5"), (Brush)FindResource("OtherBrushSubColor5")),  // світанок
        >= 8 and < 18  => (SunGeometry,     (Brush)FindResource("OtherBrushColor1"), (Brush)FindResource("OtherBrushSubColor1")),  // день
        >= 18 and < 21 => (HorizonGeometry, (Brush)FindResource("OtherBrushColor3"), (Brush)FindResource("OtherBrushSubColor3")),  // захід
        _              => (MoonGeometry,    (Brush)FindResource("OtherBrushColor2"), (Brush)FindResource("OtherBrushSubColor2")),  // ніч
    };

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Close();
    }
}
