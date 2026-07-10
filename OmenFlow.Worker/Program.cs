using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;
using OmenFlow.Hardware;
using OmenFlow.Hardware.Lighting;

namespace OmenFlow.Worker;

public class CommandRequest
{
    public string Command { get; set; } = "";
    public JsonElement? Value { get; set; }
    public string? Effect { get; set; }
}

class Program
{
    static int s_activeFanMode = 0; // 0 = Auto, 1 = OmenFlow, 2 = Max Fan, 3 = Custom
    const string FanModeCacheFile = @"C:\ProgramData\OmenFlow\fan_mode_cache.txt";
    const string FanCurveCacheFile = @"C:\ProgramData\OmenFlow\fan_curve_cache.json";

    static bool s_thermalSafetyEnabled = true;
    const string ThermalSafetyCacheFile = @"C:\ProgramData\OmenFlow\thermal_safety_cache.txt";

    static void SaveFanMode(int mode)
    {
        s_activeFanMode = mode;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FanModeCacheFile);
            if (dir != null) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(FanModeCacheFile, mode.ToString());
        }
        catch { }
    }

    static void SaveThermalSafety(bool enabled)
    {
        s_thermalSafetyEnabled = enabled;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(ThermalSafetyCacheFile);
            if (dir != null) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(ThermalSafetyCacheFile, enabled.ToString());
        }
        catch { }
    }

    private sealed record FanCurveCacheDto(string Kind, FanTarget Target, List<FanCurvePoint> Points);

    static void SaveFanCurveCache(FanCurve? curve, string kind)
    {
        try
        {
            if (curve == null)
            {
                if (System.IO.File.Exists(FanCurveCacheFile))
                {
                    System.IO.File.Delete(FanCurveCacheFile);
                }
                return;
            }

            var dir = System.IO.Path.GetDirectoryName(FanCurveCacheFile);
            if (dir != null) System.IO.Directory.CreateDirectory(dir);

            var dto = new FanCurveCacheDto(kind, curve.Target, curve.Points.ToList());
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(FanCurveCacheFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Failed to save fan curve cache: {ex.Message}");
        }
    }

    static FanCurve? LoadFanCurveCache()
    {
        try
        {
            if (!System.IO.File.Exists(FanCurveCacheFile))
            {
                return null;
            }

            var json = System.IO.File.ReadAllText(FanCurveCacheFile);
            var dto = JsonSerializer.Deserialize<FanCurveCacheDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto == null || dto.Points.Count == 0)
            {
                return null;
            }

            return new FanCurve(dto.Target, dto.Points);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Failed to load fan curve cache: {ex.Message}");
            return null;
        }
    }

    static FanCurve BuildOmenFlowPresetCurve()
    {
        return new FanCurve(FanTarget.Both, new List<FanCurvePoint>
        {
            new FanCurvePoint(50, 0),
            new FanCurvePoint(60, 25),
            new FanCurvePoint(70, 40),
            new FanCurvePoint(80, 65),
            new FanCurvePoint(85, 80),
            new FanCurvePoint(90, 90),
            new FanCurvePoint(95, 100),
        });
    }

    static async Task RestoreLastFanModeAsync(FanCurveHostedService fanCurveService, FanControlService fanControlService)
    {
        try
        {
            FanCurve? cachedCurve = LoadFanCurveCache();

            switch (s_activeFanMode)
            {
                case 2:
                    fanCurveService.SetMaxModeActive(true);
                    await fanCurveService.ApplyCustomCurveAsync(null);
                    await fanControlService.SetMaxFanAsync(true);
                    Console.WriteLine("[Startup] Restored Max Fan mode from cache.");
                    break;
                case 1:
                    fanCurveService.SetMaxModeActive(false);
                    await fanCurveService.ApplyCustomCurveAsync(cachedCurve ?? BuildOmenFlowPresetCurve());
                    Console.WriteLine(cachedCurve != null
                        ? "[Startup] Restored cached fan curve from disk."
                        : "[Startup] Restored OmenFlow fan curve from cache.");
                    break;
                case 3:
                    fanCurveService.SetMaxModeActive(false);
                    if (cachedCurve != null)
                    {
                        await fanCurveService.ApplyCustomCurveAsync(cachedCurve);
                        Console.WriteLine("[Startup] Restored custom fan curve from disk.");
                    }
                    else
                    {
                        await fanCurveService.ApplyCustomCurveAsync(null);
                        await fanControlService.RestoreAutoControlAsync();
                        Console.WriteLine("[Startup] Custom fan mode was cached but curve data is missing; restored BIOS auto control.");
                    }
                    break;
                default:
                    fanCurveService.SetMaxModeActive(false);
                    await fanCurveService.ApplyCustomCurveAsync(null);
                    await fanControlService.RestoreAutoControlAsync();
                    Console.WriteLine("[Startup] Restored Auto fan mode from cache.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Failed to restore fan mode: {ex.Message}");
        }
    }

    static async Task Main(string[] args)
    {
        Console.WriteLine("OmenFlow Worker Process (HTTP Minimal API) starting...");
        try
        {
            if (System.IO.File.Exists(FanModeCacheFile))
            {
                if (int.TryParse(System.IO.File.ReadAllText(FanModeCacheFile), out int m))
                {
                    s_activeFanMode = m;
                }
            }
            if (System.IO.File.Exists(ThermalSafetyCacheFile))
            {
                if (bool.TryParse(System.IO.File.ReadAllText(ThermalSafetyCacheFile), out bool ts))
                {
                    s_thermalSafetyEnabled = ts;
                }
            }
        }
        catch { }
        
        int currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("OmenFlow.Worker"))
        {
            if (p.Id != currentProcessId)
            {
                try
                {
                    Console.WriteLine($"Killing existing worker process (ID: {p.Id})...");
                    p.Kill();
                    p.WaitForExit(2000);
                }
                catch { }
            }
        }

        var builder = WebApplication.CreateBuilder(args);
        
        // Use local port 50312
        builder.WebHost.UseUrls("http://localhost:50312");

        var app = builder.Build();

        try
        {
            using var sensorReader = new SensorReader();
            
            // Initialize Hardware Services (Exact match with TestConsole setup)
            var capabilityService = new CapabilityDetectionService();
            var boardConfig = capabilityService.DetectBoard();
            using var ecService        = new EcService(boardConfig);
            using var biosService      = new BiosService();
            using var fanControlService  = new FanControlService(biosService, boardConfig, ecService);
            using var wmiBiosMonitor     = new WmiBiosMonitor(biosService, sensorReader);
            using var fanCurveService    = new FanCurveHostedService(fanControlService);
            var gpuControlService        = new GpuControlService(biosService);
            var lightingService          = new KeyboardLightingService(biosService);
            using var rgbEffectEngine    = new RgbEffectEngine(lightingService);
            using var perfModeService    = new PerformanceModeService(biosService, ecService, boardConfig, gpuControlService);
            var powerService             = new PowerService(biosService);
            var presetService            = new PresetService();

            fanCurveService.TelemetryProvider = () => wmiBiosMonitor.Read();
            fanCurveService.SetThermalSafetyEnabled(s_thermalSafetyEnabled);
            _ = fanCurveService.StartAsync(CancellationToken.None);

            // ── New Services (Faz 4-6) ──────────────────────────────────────
            var fanVerifyService  = new FanVerificationService(fanControlService, boardConfig);
            var fanCalibService   = new FanCalibrationService(boardConfig);
            var powerLimitService = new PowerLimitService(biosService, ecService, boardConfig);

            // Power Automation (AC/Battery auto profile switch)
            using var powerAutoService = new PowerAutomationService(perfModeService);
            _ = powerAutoService.StartAsync(CancellationToken.None);

            // Quiet Safety Monitor
            using var quietSafety = new QuietSafetyMonitor(perfModeService);
            quietSafety.TelemetryProvider = () => wmiBiosMonitor.Read();
            _ = quietSafety.StartAsync(CancellationToken.None);

            // Diagnostics export
            var diagnosticsService = new DiagnosticsExportService(
                fanCurveService, fanCalibService, ecService, boardConfig, powerLimitService, fanVerifyService);
            diagnosticsService.TelemetryProvider = () => wmiBiosMonitor.Read();
            diagnosticsService.CurrentProfileProvider = () => perfModeService.GetCurrentModeAsync().GetAwaiter().GetResult();
            diagnosticsService.CurrentFanModeProvider = () => s_activeFanMode;

            // Suspend/resume recovery service
            var suspendRecovery = new SuspendRecoveryService(fanControlService, fanCurveService, perfModeService);
            suspendRecovery.GetCurrentFanMode = () => s_activeFanMode;
            suspendRecovery.GetCurrentCurve = () => LoadFanCurveCache();
            suspendRecovery.GetCurrentIndependentCurves = () => (null, null);
            _ = suspendRecovery.StartAsync(CancellationToken.None);

            await RestoreLastFanModeAsync(fanCurveService, fanControlService);

            var currentMode = await perfModeService.GetCurrentModeAsync();
            if (currentMode != ThermalProfile.Default)
            {
                await perfModeService.SetPerformanceModeAsync(currentMode);
                Console.WriteLine($"[Startup] Reinforced current performance mode: {currentMode}");
            }

            Console.WriteLine("Hardware Services initialized. Setting up HTTP Endpoints...");

            app.MapGet("/api/telemetry", async () =>
            {
                var rpmTask      = fanControlService.GetFanRpmAsync();
                var gpuModeTask  = gpuControlService.GetGpuModeAsync();
                var gpuPowerTask = gpuControlService.GetGpuPowerAsync();
                var lightingTask = lightingService.GetLightingAsync();
                var profileTask  = perfModeService.GetCurrentModeAsync();

                await Task.WhenAll(rpmTask, gpuModeTask, gpuPowerTask, lightingTask, profileTask);

                var rpm = rpmTask.Result;

                // WmiBiosMonitor önbelleğinden al; WMI RPM değerlerini enjekte et
                var telemetry = wmiBiosMonitor.Read(rpm.CpuFanRpm, rpm.GpuFanRpm);

                telemetry.GpuMode      = gpuModeTask.Result;
                telemetry.GpuPowerLevel = gpuPowerTask.Result;
                telemetry.ActiveProfile = profileTask.Result;
                telemetry.ActiveFanMode = s_activeFanMode;
                telemetry.KeyboardType  = await lightingService.DetectKeyboardTypeAsync();

                var lightingResult  = lightingTask.Result;
                telemetry.BacklightOn = lightingResult.backlightOn;
                telemetry.ZoneColors  = lightingResult.zoneColors;
                telemetry.GpuMaxTgp   = gpuControlService.GetGpuMaxPowerLimit();

                return Results.Json(telemetry);
            });


            app.MapPost("/api/command", async (CommandRequest req) =>
            {
                string cmd = req.Command;
                var root = req.Value;
                Console.WriteLine($"[Command API] Received: {cmd}");

                try
                {
                    switch (cmd)
                    {
                        case "SetFanLevel":
                        {
                            int level = root?.ValueKind == JsonValueKind.Number ? root.Value.GetInt32() : 100;
                            Console.WriteLine($"[Command] SetFanLevel: {level}%");
                            SaveFanMode(3); // Custom
                            SaveFanCurveCache(null, "custom");
                            fanCurveService.SetMaxModeActive(false);
                            await fanCurveService.ApplyCustomCurveAsync(null);
                            await fanControlService.SetFanLevelAsync(level);
                            break;
                        }
                        case "SetAuto":
                        {
                            Console.WriteLine("[Command] SetAuto");
                            SaveFanMode(0); // Auto
                            SaveFanCurveCache(null, "auto");
                            fanControlService.NotifyFanTransitionStarted();
                            fanCurveService.SetMaxModeActive(false);
                            await fanCurveService.ApplyCustomCurveAsync(null);
                            await fanControlService.RestoreAutoControlAsync();
                            break;
                        }
                        case "SetMaxFan":
                        {
                            bool enabled = true;
                            if (root?.ValueKind == JsonValueKind.True || root?.ValueKind == JsonValueKind.False)
                            {
                                enabled = root.Value.GetBoolean();
                            }
                            Console.WriteLine($"[Command] SetMaxFan: {enabled}");
                            SaveFanMode(enabled ? 2 : 0);
                            SaveFanCurveCache(null, "max");
                            fanControlService.NotifyFanTransitionStarted();
                            fanCurveService.SetMaxModeActive(enabled);
                            await fanCurveService.ApplyCustomCurveAsync(null);
                            await fanControlService.SetMaxFanAsync(enabled);
                            break;
                        }
                        case "ApplyCurve":
                        {
                            Console.WriteLine("[Command] ApplyCurve");
                            FanCurve? curve = ParseFanCurve(root);
                            if (curve != null && curve.Points.Count == 7 && curve.Points[0].TemperatureCelsius == 50 && curve.Points[0].FanSpeedPercent == 0)
                            {
                                SaveFanMode(1); // OmenFlow Preset
                            }
                            else
                            {
                                SaveFanMode(3); // Custom
                            }
                            SaveFanCurveCache(curve, curve != null && curve.Points.Count == 7 && curve.Points[0].TemperatureCelsius == 50 && curve.Points[0].FanSpeedPercent == 0 ? "omenflow_preset" : "custom");
                            fanCurveService.SetMaxModeActive(false);
                            await fanCurveService.ApplyCustomCurveAsync(curve);
                            break;
                        }
                        case "ApplyPreset":
                        {
                            string presetId = root?.ValueKind == JsonValueKind.String ? root.Value.GetString() ?? "" : "";
                            Console.WriteLine($"[Command] ApplyPreset: {presetId}");
                            var preset = presetService.GetAll().FirstOrDefault(p => p.Id == presetId);
                            if (preset == null) break;

                            if (preset.IsMaxMode)
                            {
                                SaveFanMode(2);
                                SaveFanCurveCache(null, "max");
                                fanCurveService.SetMaxModeActive(true);
                                await fanCurveService.ApplyCustomCurveAsync(null);
                                await fanControlService.SetMaxFanAsync(true);
                            }
                            else if (preset.Curve != null)
                            {
                                SaveFanMode(1);
                                SaveFanCurveCache(preset.Curve, preset.Id);
                                fanCurveService.SetMaxModeActive(false);
                                await fanCurveService.ApplyCustomCurveAsync(preset.Curve);
                            }
                            else
                            {
                                SaveFanMode(0);
                                SaveFanCurveCache(null, "auto");
                                fanCurveService.SetMaxModeActive(false);
                                await fanCurveService.ApplyCustomCurveAsync(null);
                                await fanControlService.RestoreAutoControlAsync();
                            }
                            break;
                        }
                        case "SetThermalProfile":
                        {
                            int profile = root?.ValueKind == JsonValueKind.Number ? root.Value.GetInt32() : 0;
                            Console.WriteLine($"[Command] SetThermalProfile: 0x{profile:X2}");
                            bool perfSuccess = await perfModeService.SetPerformanceModeAsync((ThermalProfile)profile);
                            fanControlService.RecordCommand("SetThermalProfile", $"0x{profile:X2}", perfSuccess, $"Switched thermal profile to {(ThermalProfile)profile}");

                            // OmenCore physical fan reaction kick when switching to Performance Mode (31)
                            if (profile == 31)
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        Console.WriteLine("[PerformanceMode] ⚡ Starting profile switch fan kick to 55%...");
                                        fanCurveService.SetTemporaryOverride(true);
                                        await fanControlService.SetFanLevelAsync(55);
                                        await Task.Delay(3000);
                                        fanCurveService.SetTemporaryOverride(false);

                                        if (s_activeFanMode == 0) // Auto
                                        {
                                            await fanControlService.RestoreAutoControlAsync();
                                        }
                                        else if (s_activeFanMode == 2) // Max
                                        {
                                            await fanControlService.SetMaxFanAsync(true);
                                        }
                                        else
                                        {
                                            fanCurveService.TriggerImmediateApply();
                                        }
                                        Console.WriteLine("[PerformanceMode] ✓ Profile switch fan kick complete.");
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[PerformanceMode] Profile switch fan kick failed: {ex.Message}");
                                    }
                                });
                            }
                            break;
                        }
                        case "SetGpuMode":
                        {
                            int mode = root?.ValueKind == JsonValueKind.Number ? root.Value.GetInt32() : 2;
                            Console.WriteLine($"[Command] SetGpuMode: {(GpuMode)mode}");
                            await gpuControlService.SetGpuModeAsync((GpuMode)mode);
                            break;
                        }
                        case "SetGpuPower":
                        {
                            int power = root?.ValueKind == JsonValueKind.Number ? root.Value.GetInt32() : 0;
                            Console.WriteLine($"[Command] SetGpuPower: {(GpuPowerLevel)power}");
                            await gpuControlService.SetGpuPowerAsync((GpuPowerLevel)power);
                            break;
                        }
                        case "SetLighting":
                        {
                            bool on = true;
                            string colors = "";

                            if (root?.ValueKind == JsonValueKind.Object)
                            {
                                if (root.Value.TryGetProperty("BacklightOn", out var onProp)) on = onProp.GetBoolean();
                                if (root.Value.TryGetProperty("ZoneColors", out var cProp)) colors = cProp.GetString() ?? "";
                            }
                            else if (root?.ValueKind == JsonValueKind.True || root?.ValueKind == JsonValueKind.False)
                            {
                                on = root.Value.ValueKind == JsonValueKind.True;
                            }

                            Console.WriteLine($"[Command] SetLighting: On={on}, Colors={colors}");
                            rgbEffectEngine.Stop();
                            await lightingService.SetLightingAsync(on, colors);
                            break;
                        }
                        case "SetLightingEffect":
                        {
                            string effectName = req.Effect ?? "static";
                            if (root?.ValueKind == JsonValueKind.Object && root.Value.TryGetProperty("Effect", out var innerE))
                                effectName = innerE.GetString() ?? "static";
                            else if (root?.ValueKind == JsonValueKind.String)
                                effectName = root.Value.GetString() ?? "static";

                            double speed = 0.5;
                            if (root?.ValueKind == JsonValueKind.Object && root.Value.TryGetProperty("Speed", out var speedProp))
                                speed = speedProp.GetDouble();

                            double brightness = 1.0;
                            if (root?.ValueKind == JsonValueKind.Object && root.Value.TryGetProperty("Brightness", out var bProp))
                                brightness = bProp.GetDouble();

                            string colors = "";
                            if (root?.ValueKind == JsonValueKind.Object && root.Value.TryGetProperty("ZoneColors", out var zcProp))
                                colors = zcProp.GetString() ?? "";

                            byte r = 255, g = 0, b = 0;
                            if (!string.IsNullOrEmpty(colors))
                            {
                                try {
                                    byte[] decoded = Convert.FromBase64String(colors);
                                    if (decoded.Length >= 3) { r = decoded[0]; g = decoded[1]; b = decoded[2]; }
                                } catch {}
                            }

                            Console.WriteLine($"[Command] SetLightingEffect: {effectName}, Speed={speed}, Brightness={brightness}");
                            rgbEffectEngine.SetEffect(effectName.ToLowerInvariant() switch
                            {
                                "breathing"  => new OmenFlow.Hardware.Lighting.BreathingEffect(r, g, b, speed),
                                "colorcycle" => new OmenFlow.Hardware.Lighting.ColorCycleEffect(speed, brightness),
                                "wave"       => new OmenFlow.Hardware.Lighting.WaveEffect(speed, brightness),
                                _            => new OmenFlow.Hardware.Lighting.StaticEffect(string.IsNullOrEmpty(colors) ? "AP8A////////////" : colors)
                            });
                            break;
                        }
                        case "SetBatteryCare":
                        {
                            bool enabled = root?.ValueKind == JsonValueKind.True;
                            Console.WriteLine($"[Command] SetBatteryCare: {enabled}");
                            await powerService.SetBatteryCareModeAsync(enabled ? BatteryCareMode.Enabled : BatteryCareMode.Disabled);
                            break;
                        }
                        case "SetThermalSafety":
                        {
                            bool enabled = root?.ValueKind == JsonValueKind.True;
                            Console.WriteLine($"[Command] SetThermalSafety: {enabled}");
                            SaveThermalSafety(enabled);
                            fanCurveService.SetThermalSafetyEnabled(enabled);
                            break;
                        }
                        case "GetFanDiagnostics":
                        {
                            Console.WriteLine("[Command] GetFanDiagnostics");
                            var report = fanCurveService.GetCommandHistoryReport();
                            return Results.Ok(new { Success = true, Report = report });
                        }
                        case "ExportDiagnostics":
                        {
                            Console.WriteLine("[Command] ExportDiagnostics");
                            string zipPath = await diagnosticsService.ExportAsync();
                            return Results.Ok(new { Success = true, ZipPath = zipPath });
                        }
                        case "SetPowerLimits":
                        {
                            Console.WriteLine("[Command] SetPowerLimits");
                            int pl1 = 0, pl2 = 0, tgp = 0;
                            if (root?.ValueKind == JsonValueKind.Object)
                            {
                                if (root.Value.TryGetProperty("CpuPl1W", out var p1)) pl1 = p1.GetInt32();
                                if (root.Value.TryGetProperty("CpuPl2W", out var p2)) pl2 = p2.GetInt32();
                                if (root.Value.TryGetProperty("GpuTgpW", out var tg)) tgp = tg.GetInt32();
                            }
                            bool cpuOk = pl1 > 0 || pl2 > 0
                                ? await powerLimitService.SetCpuPowerLimitsAsync(pl1 > 0 ? pl1 : pl2, pl2 > 0 ? pl2 : pl1)
                                : true;
                            bool gpuOk = tgp > 0 ? await powerLimitService.SetGpuTgpAsync(tgp) : true;
                            return Results.Ok(new { Success = cpuOk && gpuOk, CpuOk = cpuOk, GpuOk = gpuOk });
                        }
                        case "SetPowerAutomation":
                        {
                            Console.WriteLine("[Command] SetPowerAutomation");
                            if (root?.ValueKind == JsonValueKind.Object)
                            {
                                if (root.Value.TryGetProperty("IsEnabled", out var en)) powerAutoService.IsEnabled = en.GetBoolean();
                                if (root.Value.TryGetProperty("OnAcProfile", out var ac) && Enum.TryParse<ThermalProfile>(ac.GetString(), out var acP)) powerAutoService.OnAcProfile = acP;
                                if (root.Value.TryGetProperty("OnBatProfile", out var bat) && Enum.TryParse<ThermalProfile>(bat.GetString(), out var batP)) powerAutoService.OnBatProfile = batP;
                                powerAutoService.SaveConfig();
                                if (powerAutoService.IsEnabled) await powerAutoService.ForceApplyCurrentSourceAsync();
                            }
                            return Results.Ok(new { Success = true, IsEnabled = powerAutoService.IsEnabled });
                        }
                        case "SetQuietSafety":
                        {
                            bool enabled = root?.ValueKind != JsonValueKind.False;
                            Console.WriteLine($"[Command] SetQuietSafety: {enabled}");
                            quietSafety.IsEnabled = enabled;
                            return Results.Ok(new { Success = true, IsEnabled = enabled });
                        }
                        default:
                            Console.WriteLine($"[Command] Unknown command: {cmd}");
                            break;
                    }
                    return Results.Ok(new { Success = true });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Command API Error] {ex.Message}");
                    return Results.Problem(ex.Message);
                }
            });

            Console.WriteLine("HTTP Server starting on http://localhost:50312...");
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL ERROR] Worker crashed: {ex}");
        }

        Console.WriteLine("Worker Process exited cleanly.");
    }

    static FanCurve? ParseFanCurve(JsonElement? root)
    {
        if (root == null || root.Value.ValueKind != JsonValueKind.Object) return null;
        try
        {
            FanTarget target = FanTarget.Both;
            if (root.Value.TryGetProperty("Target", out var tProp)) target = (FanTarget)tProp.GetInt32();

            var points = new List<FanCurvePoint>();
            if (root.Value.TryGetProperty("Points", out var pProp) && pProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var pt in pProp.EnumerateArray())
                {
                    int temp = 0, speed = 0;
                    if (pt.TryGetProperty("TemperatureCelsius", out var tC)) temp = tC.GetInt32();
                    if (pt.TryGetProperty("FanSpeedPercent", out var fS)) speed = fS.GetInt32();
                    points.Add(new FanCurvePoint(temp, speed));
                }
            }
            if (points.Count == 0) return null;
            return new FanCurve(target, points);
        }
        catch
        {
            return null;
        }
    }
}
