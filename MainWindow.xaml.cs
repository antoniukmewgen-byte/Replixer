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


        PreviewMouseDown += (_, _) =>
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), null);
        };
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

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        RootBorder.CornerRadius = WindowState == WindowState.Maximized
            ? new CornerRadius(0)
            : new CornerRadius(12);
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
