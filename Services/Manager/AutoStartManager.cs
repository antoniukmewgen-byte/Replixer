using Microsoft.Win32;
using System.IO;

namespace EchoVault.Services.Manager
{
    public static class AutoStartManager
    {
        private const string AppName = "EchoVault";

        public static void SetState(bool enable)
        {
            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                         AppDomain.CurrentDomain.FriendlyName + ".exe");

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key == null) return;

                if (enable)
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
                else
                {
                    if (key.GetValue(AppName) != null)
                    {
                        key.DeleteValue(AppName);
                    }
                }
            }
        }

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
            {
                return key?.GetValue(AppName) != null;
            }
        }
    }
}
