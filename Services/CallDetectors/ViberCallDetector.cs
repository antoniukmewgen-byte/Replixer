using FlaUI.Core.AutomationElements;

namespace EchoVault.Services.CallDetectors;

public class ViberCallDetector : ICallDetector
{
    public string ProcessName => "Viber";

    public bool IsCallWindow(AutomationElement element)
    {
        return element.ClassName.StartsWith("CallPreviewWindow_QMLTYPE_");
    }
}