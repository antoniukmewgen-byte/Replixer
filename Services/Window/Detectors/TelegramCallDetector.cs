using FlaUI.Core.AutomationElements;

namespace Replixer.Services.Window.Detectors;

public class TelegramCallDetector : ICallDetector
{
    public string ProcessName => "Telegram";

    public bool IsCallWindow(AutomationElement element)
    {
        try
        {
            return element.FindFirstDescendant(cf => cf.ByClassName("class Ui::CallButton")) is not null;
        }
        catch { return false; }
    }
}
