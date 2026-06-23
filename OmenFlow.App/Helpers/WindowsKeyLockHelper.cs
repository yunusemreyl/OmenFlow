using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OmenFlow_App.Helpers;

public static class WindowsKeyLockHelper
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private static LowLevelKeyboardProc _proc = HookCallback;
    private static IntPtr _hookID = IntPtr.Zero;
    private static bool _isLocked = false;

    public static bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (_isLocked != value)
            {
                _isLocked = value;
                if (_isLocked) SetHook();
                else Unhook();
            }
        }
    }

    private static void SetHook()
    {
        using (Process curProcess = Process.GetCurrentProcess())
        using (ProcessModule curModule = curProcess.MainModule!)
        {
            _hookID = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
                GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    private static void Unhook()
    {
        UnhookWindowsHookEx(_hookID);
        _hookID = IntPtr.Zero;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == VK_LWIN || vkCode == VK_RWIN)
            {
                // Swallow the key press
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
