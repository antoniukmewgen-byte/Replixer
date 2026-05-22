using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Replixer.Views;

public partial class CallCheatSheetWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy, flags;
    }
    private const int WM_WINDOWPOSCHANGING = 0x0046;

    private int _fixedCx;
    private int _fixedCy;

    public CallCheatSheetWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Measure real physical pixels (accounts for DPI scaling)
            var ps   = PresentationSource.FromVisual(this);
            double sx = ps?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = ps?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            _fixedCx = (int)(ActualWidth  * sx);
            _fixedCy = (int)(ActualHeight * sy);

            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)
                      ?.AddHook(WndProc);
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING && _fixedCx > 0 && _fixedCy > 0)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.cx = _fixedCx;
            pos.cy = _fixedCy;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        return IntPtr.Zero;
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
