using FlaUI.Core.AutomationElements;

namespace EchoVault.Services.Window.Detectors;

public class WhatsAppCallDetector : ICallDetector
{
    public string ProcessName => "WhatsApp.Root";

    public bool IsCallWindow(AutomationElement element)
    {
        return element.Name is "Аудиозвонок" or "Видеозвонок" or "Audio call" or "Video call";
    }
}
