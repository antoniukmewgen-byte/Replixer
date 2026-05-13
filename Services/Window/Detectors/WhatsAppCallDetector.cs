using FlaUI.Core.AutomationElements;

namespace Replixer.Services.Window.Detectors;

public class WhatsAppCallDetector : ICallDetector
{
    private static readonly HashSet<string> CallNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Аудіодзвінок", "Відеодзвінок",
        "Аудіовиклик", "Відеовиклик",
        "Аудиозвонок", "Видеозвонок",
        "Audio call",  "Video call",
        "Anruf",       "Videoanruf",
        "Appel audio", "Appel vidéo",
        "Llamada de audio", "Llamada de video",
        "Połączenie audio", "Połączenie wideo",
        "Chiamata audio",   "Chiamata video",
    };

    public string ProcessName => "WhatsApp.Root";

    public bool IsCallWindow(AutomationElement element)
        => CallNames.Contains(element.Name);
}
