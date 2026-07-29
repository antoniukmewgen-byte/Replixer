using Replixer.Infrastructure;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Replixer.Services;

// Знімає скрін не всього екрана, а конкретного вікна (foreground на момент кліку). Раніше це
// робилось через PrintWindow, але для UWP/Store-застосунків (типово — WhatsApp Desktop) та
// будь-яких вікон з апаратним композитингом PrintWindow повертає порожній/сірий кадр: реальний
// вміст малюється в окремому дочірньому CoreWindow через DirectComposition і в "знімок на запит"
// не потрапляє. Тому знімаємо через BitBlt — копіюємо вже фактично відрендерений вміст екрана
// в межах прямокутника вікна. Це безпечно саме тут, бо на момент виклику вікно месенджера вже
// гарантовано foreground (WaitForMessengerForegroundAsync) і нічим не перекрите.
public class ScreenCaptureService
{
    private static readonly string ScreenshotsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "Screenshots");

    // Повертає шлях до збереженого PNG, або null якщо знімок не вдався.
    // messenger — назва месенджера ("Viber"/"Telegram"/"WhatsApp"), потрапляє у назву файлу
    // (і, відповідно, у назву файлу на Google Drive — див. GoogleDriveUploadService.UploadAsync,
    // яка бере ім'я з Path.GetFileName), щоб у Drive скріни виглядали як "Telegram_23.04.2026_15-34.png",
    // а не знеособленим таймстемпом.
    public string? CaptureForegroundWindow(string messenger)
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

            bool success = ScreenCaptureInterop.BitBlt(
                memDc, 0, 0, width, height,
                screenDc, rect.Left, rect.Top, ScreenCaptureInterop.SRCCOPY);
            ScreenCaptureInterop.SelectObject(memDc, oldObject);

            if (!success) return null;

            // Фідбек одразу після фактичного захоплення кадру (а не після повільнішого
            // кодування в PNG/збереження нижче) — щоб менеджер бачив підтвердження "скрін
            // зроблено" максимально близько до реального моменту зйомки.
            ScreenshotFeedbackService.Flash();

            var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // BitBlt з екрана не заповнює альфа-байт (GDI лишає його 0), а CreateBitmapSourceFromHBitmap
            // за замовчуванням трактує растр як 32-біт З альфа-каналом — тому збережений PNG виходить
            // майже суцільно прозорим (шаховий візерунок), і крізь нього проступають лише краї шрифтів/
            // іконок, де згладжування випадково дало ненульове значення в тому самому байті. Явно
            // конвертуємо в Bgr24 (без альфи), щоб скрін вийшов суцільним, як на екрані.
            var opaqueBitmap = new FormatConvertedBitmap(bitmapSource, PixelFormats.Bgr24, null, 0);

            Directory.CreateDirectory(ScreenshotsDir);
            var filePath = BuildScreenshotPath(messenger);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(opaqueBitmap));
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

    // Формат "Messenger_dd.MM.yyyy_HH-mm" (напр. "Telegram_23.04.2026_15-34"). ":" замінено на "-",
    // бо двокрапка заборонена у назвах файлів Windows (NTFS сприймає її як роздільник alternate
    // data stream). Якщо в межах тієї ж хвилини для того самого месенджера вже є файл (напр.
    // повторна спроба скріна після невдалої), додаємо "_2", "_3" тощо, щоб не перезаписати його.
    private static string BuildScreenshotPath(string messenger)
    {
        var baseName = $"{messenger}_{DateTime.Now:dd.MM.yyyy_HH-mm}";
        var path = Path.Combine(ScreenshotsDir, $"{baseName}.png");

        int suffix = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(ScreenshotsDir, $"{baseName}_{suffix}.png");
            suffix++;
        }

        return path;
    }
}
