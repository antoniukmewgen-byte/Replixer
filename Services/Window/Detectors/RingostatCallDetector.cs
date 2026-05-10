using FlaUI.Core.AutomationElements;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EchoVault.Services.Window.Detectors;

public class RingostatCallDetector : ICallDetector
{
    public string ProcessName => "Ringostat Smart Phone";

    private const uint PW_RENDERFULLCONTENT = 2;
    private const int  RelYStart    = 60;
    private const int  RelYEnd      = 350;
    private const int  RelYStep     = 15;
    private const int  RequiredHits = 2;

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr hDC, int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width  => Right  - Left;
        public int Height => Bottom - Top;
    }

    public bool IsCallWindow(AutomationElement element)
    {
        var hWnd = element.Properties.NativeWindowHandle.ValueOrDefault;
        if (hWnd == IntPtr.Zero) return false;

        if (!GetWindowRect(hWnd, out RECT rect)) return false;
        int w = rect.Width, h = rect.Height;
        if (w < 50 || h < 50) return false;

        var screenDC  = GetDC(IntPtr.Zero);
        var memDC     = CreateCompatibleDC(screenDC);
        var bitmap    = CreateCompatibleBitmap(screenDC, w, h);
        var oldBitmap = SelectObject(memDC, bitmap);

        try
        {
            // Forces the GPU-rendered (Flutter) window to paint into our bitmap
            PrintWindow(hWnd, memDC, PW_RENDERFULLCONTENT);

            int sampleX = w / 2;
            int hits = 0, total = 0;

            for (int dy = RelYStart; dy <= RelYEnd && dy < h; dy += RelYStep)
            {
                uint color = GetPixel(memDC, sampleX, dy);
                int r = (int)(color & 0xFF);
                int g = (int)((color >> 8) & 0xFF);
                int b = (int)((color >> 16) & 0xFF);

                bool isBlue = b > 150 && b > r * 2;
                Debug.WriteLine($"[Pixel] ({sampleX,4}, {dy,4}) R={r,3} G={g,3} B={b,3}  {(isBlue ? "← CALL BLUE" : "")}");
                if (isBlue) hits++;
                total++;
            }

            bool isCall = hits >= RequiredHits;
            Debug.WriteLine($"[Pixel] hits={hits}/{total} → isCall={isCall}");
            if (isCall)
                Debug.WriteLine($"[Pixel] >>> CALL DETECTED: {ProcessName}");

            return isCall;
        }
        finally
        {
            SelectObject(memDC, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(memDC);
            ReleaseDC(IntPtr.Zero, screenDC);
        }
    }
}
