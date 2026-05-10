using FlaUI.Core.AutomationElements;

namespace Replixer.Services.Window.Detectors;

public interface ICallDetector
{
    string ProcessName { get; }
    bool IsCallWindow(AutomationElement element);
}
