using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;
using OmenFlow.Core.Services;

namespace OmenFlow.Worker;

public class AutoProfileService : BackgroundService, IAutoProfileService
{
    private const string CONFIG_PATH = @"C:\ProgramData\OmenFlow\autoprofiles_config.json";
    
    public bool IsEnabled { get; set; } = false;
    private List<string> _games = new();
    
    private readonly IPerformanceModeService _perfModeService;
    private ThermalProfile _previousProfile = ThermalProfile.Default;
    private bool _isGameRunningAndApplied = false;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public AutoProfileService(IPerformanceModeService perfModeService)
    {
        _perfModeService = perfModeService;
        LoadConfig();
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(CONFIG_PATH))
            {
                var json = File.ReadAllText(CONFIG_PATH);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("IsEnabled", out var enProp))
                {
                    IsEnabled = enProp.GetBoolean();
                }
                if (doc.RootElement.TryGetProperty("Games", out var gProp) && gProp.ValueKind == JsonValueKind.Array)
                {
                    _games = gProp.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogInfo($"[AutoProfile] Load config failed: {ex.Message}");
        }
    }

    public void SaveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(CONFIG_PATH);
            if (dir != null) Directory.CreateDirectory(dir);

            var data = new
            {
                IsEnabled = this.IsEnabled,
                Games = this._games
            };
            File.WriteAllText(CONFIG_PATH, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.LogInfo($"[AutoProfile] Save config failed: {ex.Message}");
        }
    }

    public IReadOnlyList<string> GetGames() => _games.AsReadOnly();

    public void AddGame(string exeName)
    {
        exeName = exeName.Trim().ToLowerInvariant();
        if (exeName.EndsWith(".exe")) exeName = exeName.Substring(0, exeName.Length - 4);
        
        if (!_games.Contains(exeName, StringComparer.OrdinalIgnoreCase))
        {
            _games.Add(exeName);
            SaveConfig();
            Logger.LogInfo($"[AutoProfile] Added game: {exeName}");
        }
    }

    public void RemoveGame(string exeName)
    {
        exeName = exeName.Trim().ToLowerInvariant();
        if (exeName.EndsWith(".exe")) exeName = exeName.Substring(0, exeName.Length - 4);
        
        if (_games.RemoveAll(g => g.Equals(exeName, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            SaveConfig();
            Logger.LogInfo($"[AutoProfile] Removed game: {exeName}");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInfo("[AutoProfile] Background service started.");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsEnabled && _games.Count > 0)
                {
                    await CheckForegroundProcessAsync();
                }
                else if (_isGameRunningAndApplied)
                {
                    // Feature turned off while game was running, restore profile
                    await RestorePreviousProfileAsync();
                }
            }
            catch (Exception ex)
            {
                // Suppress background errors to avoid crash
                Logger.LogInfo($"[AutoProfile] Loop error: {ex.Message}");
            }

            await Task.Delay(2500, stoppingToken); // 2.5 seconds poll rate
        }
    }

    private async Task CheckForegroundProcessAsync()
    {
        IntPtr hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return;

        GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == 0) return;

        try
        {
            var process = Process.GetProcessById((int)processId);
            string processName = process.ProcessName.ToLowerInvariant();

            bool isGameActive = _games.Any(g => g.Equals(processName, StringComparison.OrdinalIgnoreCase));

            if (isGameActive && !_isGameRunningAndApplied)
            {
                // Game launched/focused -> Switch to Performance
                _previousProfile = await _perfModeService.GetCurrentModeAsync();
                if (_previousProfile != ThermalProfile.Performance)
                {
                    Logger.LogInfo($"[AutoProfile] Game '{processName}' detected in foreground. Switching to Performance Mode.");
                    await _perfModeService.SetPerformanceModeAsync(ThermalProfile.Performance);
                }
                _isGameRunningAndApplied = true;
            }
            else if (!isGameActive && _isGameRunningAndApplied)
            {
                // Wait, if they just alt-tabbed, do we revert immediately? 
                // Let's check if the process is STILL running at all. If it's running but minimized, maybe keep it.
                // But foreground-only is also a valid battery-saving strategy.
                // Let's check if ANY game in the list is still running.
                bool anyGameRunning = false;
                var allProcesses = Process.GetProcesses();
                foreach (var p in allProcesses)
                {
                    try
                    {
                        if (_games.Contains(p.ProcessName.ToLowerInvariant()))
                        {
                            anyGameRunning = true;
                            break;
                        }
                    }
                    catch { } // Ignore access denied
                }

                if (!anyGameRunning)
                {
                    Logger.LogInfo($"[AutoProfile] No games running. Restoring previous profile: {_previousProfile}");
                    await RestorePreviousProfileAsync();
                }
            }
        }
        catch (ArgumentException)
        {
            // Process already exited
        }
        catch (Exception)
        {
            // Access denied for system processes, etc.
        }
    }

    private async Task RestorePreviousProfileAsync()
    {
        if (_previousProfile != ThermalProfile.Performance)
        {
            await _perfModeService.SetPerformanceModeAsync(_previousProfile);
        }
        _isGameRunningAndApplied = false;
    }
}
