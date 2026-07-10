using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

/// <summary>
/// Automatically switches performance profile and fan mode when AC/battery state changes.
///
/// Mirrors OmenCore's PowerAutomationService:
/// - AC plugged in â†’ apply the "on AC" profile (default: Performance)
/// - AC unplugged  â†’ apply the "on battery" profile (default: Quiet)
/// - Both profiles are user-configurable (saved to ProgramData)
/// - Transitions are logged and the last auto-applied profile is persisted
///
/// This service also handles the initial state check on startup:
/// if the current power source doesn't match the current profile, it corrects it.
/// </summary>
public class PowerAutomationService : BackgroundService
{
    private readonly PerformanceModeService _perfModeService;
    private const string ConfigFile = @"C:\ProgramData\OmenFlow\power_automation.json";

    // User-configurable profiles
    public ThermalProfile OnAcProfile  { get; set; } = ThermalProfile.Performance;
    public ThermalProfile OnBatProfile { get; set; } = ThermalProfile.Quiet;

    // Whether automation is enabled (user can disable in Settings)
    public bool IsEnabled { get; set; } = false;

    private bool _lastKnownAcState = true; // assume AC on startup
    private readonly SemaphoreSlim _switchLock = new(1, 1);

    public PowerAutomationService(PerformanceModeService perfModeService)
    {
        _perfModeService = perfModeService;
        LoadConfig();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var thread = new Thread(() =>
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            OmenFlow.Core.Services.Logger.LogInfo("[PowerAuto] Power source change hook registered.");

            // Check initial state on startup
            bool isAc = IsOnAc();
            _lastKnownAcState = isAc;
            if (IsEnabled)
            {
                _ = ApplyForPowerSourceAsync(isAc, "startup check");
            }

            while (!stoppingToken.IsCancellationRequested)
                Thread.Sleep(500);

            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            OmenFlow.Core.Services.Logger.LogInfo("[PowerAuto] Power source change hook removed.");
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return Task.CompletedTask;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;

        bool isAc = IsOnAc();
        if (isAc == _lastKnownAcState) return; // Spurious event â€” ignore

        _lastKnownAcState = isAc;
        string source = isAc ? "AC plugged in" : "Battery (AC unplugged)";
        OmenFlow.Core.Services.Logger.LogInfo($"[PowerAuto] Power source changed: {source}");

        if (IsEnabled)
        {
            _ = ApplyForPowerSourceAsync(isAc, source);
        }
        else
        {
            OmenFlow.Core.Services.Logger.LogInfo("[PowerAuto] Automation disabled â€” not switching profile automatically.");
        }
    }

    private async Task ApplyForPowerSourceAsync(bool isAc, string reason)
    {
        if (!await _switchLock.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            OmenFlow.Core.Services.Logger.LogInfo("[PowerAuto] Switch already in progress â€” skipping.");
            return;
        }

        try
        {
            ThermalProfile target = isAc ? OnAcProfile : OnBatProfile;
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerAuto] Applying {target} for {reason}...");
            bool ok = await _perfModeService.SetPerformanceModeAsync(target);
            OmenFlow.Core.Services.Logger.LogInfo(ok
                ? $"[PowerAuto] âœ“ {target} applied."
                : $"[PowerAuto] âš  Failed to apply {target}.");
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerAuto] Error during auto profile switch: {ex.Message}");
        }
        finally
        {
            _switchLock.Release();
        }
    }

    private static bool IsOnAc()
    {
        try
        {
            // Use WMI to read AC/battery status â€” no WinForms dependency
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT BatteryStatus FROM Win32_Battery");
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                // BatteryStatus: 2 = AC power (connected to mains), 1 = Discharging
                var status = obj["BatteryStatus"];
                if (status != null && ushort.TryParse(status.ToString(), out ushort bs))
                    return bs == 2;
            }
            return true; // No battery found â†’ desktop/always on AC
        }
        catch
        {
            return true; // safe assumption
        }
    }

    /// <summary>
    /// Immediately forces the appropriate profile for the current power source.
    /// Call this after the user changes the AC/battery profile settings.
    /// </summary>
    public async Task ForceApplyCurrentSourceAsync()
    {
        bool isAc = IsOnAc();
        await ApplyForPowerSourceAsync(isAc, "manual refresh");
    }

    private void LoadConfig()
    {
        try
        {
            if (!System.IO.File.Exists(ConfigFile)) return;
            var json = System.IO.File.ReadAllText(ConfigFile);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("IsEnabled", out var en)) IsEnabled = en.GetBoolean();
            if (root.TryGetProperty("OnAcProfile", out var ac) && Enum.TryParse<ThermalProfile>(ac.GetString(), out var acP)) OnAcProfile = acP;
            if (root.TryGetProperty("OnBatProfile", out var bat) && Enum.TryParse<ThermalProfile>(bat.GetString(), out var batP)) OnBatProfile = batP;

            OmenFlow.Core.Services.Logger.LogInfo($"[PowerAuto] Config loaded: Enabled={IsEnabled}, AC={OnAcProfile}, Bat={OnBatProfile}");
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerAuto] Config load error: {ex.Message}");
        }
    }

    public void SaveConfig()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(ConfigFile);
            if (dir != null) System.IO.Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                IsEnabled,
                OnAcProfile = OnAcProfile.ToString(),
                OnBatProfile = OnBatProfile.ToString()
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(ConfigFile, json);
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[PowerAuto] Config save error: {ex.Message}");
        }
    }
}

