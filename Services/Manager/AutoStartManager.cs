using Microsoft.Win32;
using System.IO;

namespace Replixer.Services.Manager;

public static class AutoStartManager
{
    private const string AppName = "Replixer";

    public static void SetState(bool enable)
    {
        string exePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            AppDomain.CurrentDomain.FriendlyName + ".exe");

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);

        if (key is null) return;

        if (enable)
            key.SetValue(AppName, $"\"{exePath}\" --tray");
        else if (key.GetValue(AppName) is not null)
            key.DeleteValue(AppName);
    }

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: false);

        return key?.GetValue(AppName) is not null;
    }
}
