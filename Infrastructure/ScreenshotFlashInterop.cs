using System.Runtime.InteropServices;

namespace Replixer.Infrastructure;

// Робить ScreenshotFlashWindow повністю "прозорим" для миші (клік-через) — коротка анімація
// накладається поверх вікна месенджера, і якщо не зняти захоплення вводу, випадковий клік чи
// рух миші під час 450мс спалаху міг би піти у сам оверлей замість Telegram/Viber/WhatsApp,
// куди менеджер саме зараз щось вводить.
internal static class ScreenshotFlashInterop
{
    public const int GWL_EXSTYLE       = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED     = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
