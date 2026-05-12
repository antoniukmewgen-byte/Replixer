using Replixer.ViewModels;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Replixer;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();

        vm.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = vm;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024) // WM_GETMINMAXINFO
        {
            ApplyWorkAreaConstraints(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ApplyWorkAreaConstraints(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        var monitor = NativeMethods.MonitorFromWindow(hwnd, 0x00000002); // MONITOR_DEFAULTTONEAREST
        if (monitor != IntPtr.Zero)
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            NativeMethods.GetMonitorInfo(monitor, ref info);

            var work    = info.rcWork;
            var monitor_ = info.rcMonitor;

            mmi.ptMaxPosition.x = Math.Abs(work.left   - monitor_.left);
            mmi.ptMaxPosition.y = Math.Abs(work.top    - monitor_.top);
            mmi.ptMaxSize.x     = Math.Abs(work.right  - work.left);
            mmi.ptMaxSize.y     = Math.Abs(work.bottom - work.top);
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentViewModel)) return;

        var button = ((MainViewModel)DataContext).CurrentViewModel switch
        {
            HomeViewModel       => HomeButton,
            RecordingsViewModel => RecordingsButton,
            SettingsViewModel   => SettingsButton,
            ProfileViewModel    => ProfileButton,
            _                   => null
        };

        if (button is null) return;

        var targetY = button.TransformToAncestor(SidebarGrid)
                            .Transform(new Point(0, 0)).Y;

        var animation = new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        IndicatorTranslate.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && WindowState != WindowState.Maximized)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private const string IconExpand  = "M22.5,15.5A1.5,1.5,0,0,0,21,17v1.5A2.5,2.5,0,0,1,18.5,21H17a1.5,1.5,0,0,0,0,3h1.5A5.506,5.506,0,0,0,24,18.5V17A1.5,1.5,0,0,0,22.5,15.5Z M7,0H5.5A5.506,5.506,0,0,0,0,5.5V7A1.5,1.5,0,0,0,3,7V5.5A2.5,2.5,0,0,1,5.5,3H7A1.5,1.5,0,0,0,7,0Z M7,21H5.5A2.5,2.5,0,0,1,3,18.5V17a1.5,1.5,0,0,0-3,0v1.5A5.506,5.506,0,0,0,5.5,24H7a1.5,1.5,0,0,0,0-3Z M18.5,0H17a1.5,1.5,0,0,0,0,3h1.5A2.5,2.5,0,0,1,21,5.5V7a1.5,1.5,0,0,0,3,0V5.5A5.506,5.506,0,0,0,18.5,0Z";
    private const string IconCollapse = "M9,2.5A6.5,6.5,0,0,1,2.5,9h-1a1.5,1.5,0,0,1,0-3h1A3.5,3.5,0,0,0,6,2.5v-1a1.5,1.5,0,0,1,3,0Z M16.5,24A1.5,1.5,0,0,1,15,22.5v-1A6.5,6.5,0,0,1,21.5,15h1a1.5,1.5,0,0,1,0,3h-1A3.5,3.5,0,0,0,18,21.5v1A1.5,1.5,0,0,1,16.5,24Z M22.5,9h-1A6.5,6.5,0,0,1,15,2.5v-1a1.5,1.5,0,0,1,3,0v1A3.5,3.5,0,0,0,21.5,6h1a1.5,1.5,0,0,1,0,3Z M7.5,24A1.5,1.5,0,0,1,6,22.5v-1A3.5,3.5,0,0,0,2.5,18h-1a1.5,1.5,0,0,1,0-3h1A6.5,6.5,0,0,1,9,21.5v1A1.5,1.5,0,0,1,7.5,24Z";

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        var maximized = WindowState == WindowState.Maximized;
        RootBorder.CornerRadius  = maximized ? new CornerRadius(0) : new CornerRadius(12);
        FullscreenIcon.Data      = Geometry.Parse(maximized ? IconCollapse : IconExpand);
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int    cbSize;
        public RECT   rcMonitor;
        public RECT   rcWork;
        public uint   dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int left, top, right, bottom;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MINMAXINFO
{
    public POINT ptReserved;
    public POINT ptMaxSize;
    public POINT ptMaxPosition;
    public POINT ptMinTrackSize;
    public POINT ptMaxTrackSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int x, y;
}
