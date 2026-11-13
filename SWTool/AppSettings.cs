using System;
using Microsoft.Win32;

namespace SWTool
{
    /// <summary>Small per-user settings store (HKCU\Software\SWTool). Failures are ignored: settings are a convenience.</summary>
    internal static class AppSettings
    {
        private const string Key = @"Software\SWTool";

        public static string Get(string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
                    return k != null ? k.GetValue(name) as string : null;
            }
            catch (Exception) { return null; }
        }

        public static void Set(string name, string value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (k != null) k.SetValue(name, value ?? "");
                }
            }
            catch (Exception) { }
        }
    }
}
