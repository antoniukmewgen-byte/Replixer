using FlaUI.Core.AutomationElements;

namespace EchoVault.Services.Window.Detectors;

public interface ICallDetector
{
    string ProcessName { get; }
    bool IsCallWindow(AutomationElement element);
}
