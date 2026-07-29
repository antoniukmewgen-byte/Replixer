using System.Diagnostics;
using System.Media;
using Replixer.Views;

namespace Replixer.Services;

// Короткий звук + візуальний спалах на весь екран у момент, коли ScreenCaptureService знімає
// скрін вікна месенджера. Без цього менеджер не мав жодного індикатора, що скрін взагалі
// відбувся — особливо помітно, коли Telegram/Viber/WhatsApp розгорнуте на весь екран і
// повністю перекриває Replixer: "тиша" виглядала так, ніби нічого не сталось.
public static class ScreenshotFeedbackService
{
    // Викликається синхронно з UI-потоку (CaptureForegroundWindow завжди йде з команди
    // ViewModel, до першого await), тому створення Window тут безпечне без Dispatcher.Invoke.
    public static void Flash()
    {
        try
        {
            SystemSounds.Asterisk.Play(); // асинхронний виклик, не блокує
            new ScreenshotFlashWindow().Show();
        }
        catch (Exception ex)
        {
            // Суто косметичний фідбек — сам скрін і завантаження вже зроблені й не залежать
            // від того, чи вдалось показати спалах.
            Debug.WriteLine($"[ScreenshotFeedback] Flash failed: {ex.Message}");
        }
    }
}
