using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace OmenFlow_App.Helpers;

public static class StartupHelper
{
    private const string RUN_LOCATION = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string APP_NAME = "OmenFlow";

    public static bool IsStartupEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RUN_LOCATION);
                if (key != null)
                {
                    return key.GetValue(APP_NAME) != null;
                }
            }
            catch { }
            return false;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RUN_LOCATION, true);
                if (key != null)
                {
                    if (value)
                    {
                        string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                        key.SetValue(APP_NAME, $"\"{exePath}\" --hidden");
                    }
                    else
                    {
                        key.DeleteValue(APP_NAME, false);
                    }
                }
            }
            catch { }
        }
    }
}
