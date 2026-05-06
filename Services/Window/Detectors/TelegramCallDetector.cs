using FlaUI.Core.AutomationElements;

namespace EchoVault.Services.Window.Detectors;

public class TelegramCallDetector : ICallDetector
{
    public string ProcessName => "Telegram";

    public bool IsCallWindow(AutomationElement element)
    {
        return HasClassName(element, "class Ui::CallButton");
    }

    private bool HasClassName(AutomationElement parent, string className, int depth = 0)
    {
        if (depth > 10) return false;
        try
        {
            foreach (var child in parent.FindAllChildren())
            {
                try
                {
                    if (child.ClassName == className) return true;
                    if (HasClassName(child, className, depth + 1)) return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }
}
