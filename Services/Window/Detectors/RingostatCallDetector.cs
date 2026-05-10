using FlaUI.Core.AutomationElements;

namespace EchoVault.Services.Window.Detectors;

public class RingostatCallDetector : ICallDetector
{
    public string ProcessName => "Ringostat";

    public bool IsCallWindow(AutomationElement element)
    {
        return element.ClassName.StartsWith("CallPreviewWindow_QMLTYPE_");
    }
}
