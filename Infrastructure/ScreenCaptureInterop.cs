using System.Runtime.InteropServices;

namespace Replixer.Infrastructure;

internal static class ScreenCaptureInterop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public const uint PW_RENDERFULLCONTENT = 0x00000002;
    public const uint SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // Використовується, щоб визначити, якому процесу належить поточне foreground-вікно —
    // так ми чекаємо, поки саме вікно месенджера (Telegram/Viber/WhatsApp) стане активним,
    // перш ніж автоматично зробити скрін.
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    // Потрібні, щоб прочитати заголовок вікна — резервна перевірка для UWP/Store-застосунків
    // (див. коментар в MessengerProcessNames.Matches).
    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    // Резервний спосіб зняти скрін, коли PrintWindow повертає порожній/сірий кадр — типово
    // трапляється з UWP/Store-застосунками (WhatsApp Desktop) чи будь-яким вікном з апаратним
    // композитингом, чий реальний вміст малюється в окремому дочірньому CoreWindow через
    // DirectComposition і не потрапляє в PrintWindow. BitBlt натомість копіює вже фактично
    // відрендерений вміст екрана — те, що користувач бачить, а не окрему "картинку на запит".
    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiObj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);
}
