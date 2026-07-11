using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OmenFlow_App.Helpers;

public static class TouchpadHelper
{
    private const string REG_KEY = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status";
    private const string REG_VAL = "Enabled";

    public static bool IsLocked
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(REG_KEY);
                if (key != null)
                {
                    var val = key.GetValue(REG_VAL);
                    if (val is int intVal) return intVal == 0;
                }
            }
            catch { }
            return false;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(REG_KEY);
                if (key != null)
                {
                    key.SetValue(REG_VAL, value ? 0 : 1, RegistryValueKind.DWord);
                }
            }
            catch { }
        }
    }
}
