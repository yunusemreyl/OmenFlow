using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Services;
using System.Threading.Tasks;

namespace OmenFlow.Worker;

public class OmenKeyService : IOmenKeyService
{
    private const string IFEO_PATH = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\OmenCommandCenter.exe";
    private const string OMEN_KEY_CACHE = @"C:\ProgramData\OmenFlow\omenkey_config.txt";

    public bool IsInterceptEnabled { get; set; } = false;

    public OmenKeyService()
    {
        LoadConfig();
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(OMEN_KEY_CACHE))
            {
                if (bool.TryParse(File.ReadAllText(OMEN_KEY_CACHE), out bool enabled))
                {
                    IsInterceptEnabled = enabled;
                }
            }
        }
        catch { }
    }

    public void SaveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(OMEN_KEY_CACHE);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(OMEN_KEY_CACHE, IsInterceptEnabled.ToString());
        }
        catch (Exception ex)
        {
            Logger.LogInfo($"[OmenKey] Save config failed: {ex.Message}");
        }
    }

    public Task SetInterceptEnabledAsync(bool enabled)
    {
        return Task.Run(() =>
        {
            IsInterceptEnabled = enabled;
            SaveConfig();
            
            try
            {
                if (enabled)
                {
                    string workerPath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(workerPath))
                    {
                        using var key = Registry.LocalMachine.CreateSubKey(IFEO_PATH);
                        // Windows will launch: "WorkerPath" --omenkey "OmenCommandCenter.exe"
                        key.SetValue("Debugger", $"\"{workerPath}\" --omenkey");
                        Logger.LogInfo("[OmenKey] Intercept enabled (IFEO registered).");
                    }
                }
                else
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", true);
                    if (key != null)
                    {
                        key.DeleteSubKeyTree("OmenCommandCenter.exe", false);
                        Logger.LogInfo("[OmenKey] Intercept disabled (IFEO removed).");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[OmenKey] Failed to set IFEO: {ex.Message}");
                // Not running as admin? The worker should be.
            }
        });
    }

    // Windows API for Window Management
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    
    private const int SW_RESTORE = 9;

    public static void HandleOmenKeyLaunch()
    {
        Logger.LogInfo("[OmenKey] OMEN Key pressed! Trying to bring OmenFlow to front...");
        try
        {
            IntPtr hWnd = FindWindow(null, "OmenFlow"); // Assuming MainWindow title is "OmenFlow"
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
            }
            else
            {
                // App not running, try to launch it
                string workerDir = AppContext.BaseDirectory;
                string appPath = Path.Combine(workerDir, "OmenFlow.App.exe");
                
                // Fallback for debug environment
                if (!File.Exists(appPath))
                {
                    appPath = Path.Combine(workerDir, @"..\..\..\..\OmenFlow.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\OmenFlow.App.exe");
                    appPath = Path.GetFullPath(appPath);
                }

                if (File.Exists(appPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = appPath,
                        UseShellExecute = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogInfo($"[OmenKey] Launch failed: {ex.Message}");
        }
    }
}
