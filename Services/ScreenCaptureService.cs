using Replixer.Infrastructure;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Replixer.Services;

// Знімає скрін не всього екрана, а конкретного вікна (foreground на момент кліку) через
// PrintWindow — це працює навіть якщо вікно частково перекрите іншим (напр. плаваючою плашкою).
public class ScreenCaptureService
{
    private static readonly string ScreenshotsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "Screenshots");

    // Повертає шлях до збереженого PNG, або null якщо знімок не вдався.
    public string? CaptureForegroundWindow()
    {
        var hWnd = ScreenCaptureInterop.GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return null;
        if (!ScreenCaptureInterop.GetWindowRect(hWnd, out var rect)) return null;

        int width  = rect.Right  - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        var screenDc = ScreenCaptureInterop.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        var memDc   = IntPtr.Zero;
        var hBitmap = IntPtr.Zero;
        try
        {
            memDc   = ScreenCaptureInterop.CreateCompatibleDC(screenDc);
            hBitmap = ScreenCaptureInterop.CreateCompatibleBitmap(screenDc, width, height);
            var oldObject = ScreenCaptureInterop.SelectObject(memDc, hBitmap);

            bool success = ScreenCaptureInterop.PrintWindow(hWnd, memDc, ScreenCaptureInterop.PW_RENDERFULLCONTENT);
            ScreenCaptureInterop.SelectObject(memDc, oldObject);

            if (!success) return null;

            var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            Directory.CreateDirectory(ScreenshotsDir);
            var filePath = Path.Combine(ScreenshotsDir, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            using (var stream = new FileStream(filePath, FileMode.Create))
                encoder.Save(stream);

            return filePath;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) ScreenCaptureInterop.DeleteObject(hBitmap);
            if (memDc   != IntPtr.Zero) ScreenCaptureInterop.DeleteDC(memDc);
            ScreenCaptureInterop.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
