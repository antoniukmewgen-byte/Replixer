using FlaUI.Core.AutomationElements;

namespace EchoVault.Services.CallDetectors;

public interface ICallDetector
{
    string ProcessName { get; }
    bool IsCallWindow(AutomationElement element);
}