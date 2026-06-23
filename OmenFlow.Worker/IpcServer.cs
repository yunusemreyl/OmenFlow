using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;
using OmenFlow.Hardware;

namespace OmenFlow.Worker;

public class IpcServer
{
    private readonly SensorReader _sensorReader;
    private readonly FanControlService _fanControlService;
    private readonly FanCurveHostedService _fanCurveService;
    private readonly GpuControlService _gpuControlService;
    private readonly KeyboardLightingService _lightingService;
    private readonly OmenFlow.Hardware.Lighting.RgbEffectEngine _rgbEffectEngine;
    private readonly IPerformanceModeService _performanceModeService;
    private readonly IPowerService _powerService;
    private readonly IPresetService _presetService;

    public IpcServer(
        SensorReader sensorReader,
        FanControlService fanControlService,
        FanCurveHostedService fanCurveService,
        GpuControlService gpuControlService,
        KeyboardLightingService lightingService,
        OmenFlow.Hardware.Lighting.RgbEffectEngine rgbEffectEngine,
        IPerformanceModeService performanceModeService,
        IPowerService powerService,
        IPresetService presetService)
    {
        _sensorReader = sensorReader;
        _fanControlService = fanControlService;
        _fanCurveService = fanCurveService;
        _gpuControlService = gpuControlService;
        _lightingService = lightingService;
        _rgbEffectEngine = rgbEffectEngine;
        _performanceModeService = performanceModeService;
        _powerService = powerService;
        _presetService = presetService;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var t1 = RunTelemetryServerAsync(ct);
        var t2 = RunCommandServerAsync(ct);
        await Task.WhenAll(t1, t2);
    }

    // ─────────────────── Telemetry Pipe ───────────────────

    private async Task RunTelemetryServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipeServer = new NamedPipeServerStream(
                    "OmenFlow_HardwareTelemetry",
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                Console.WriteLine("[Telemetry] Waiting for client connection...");
                await pipeServer.WaitForConnectionAsync(ct);
                Console.WriteLine("[Telemetry] Client connected.");

                using var writer = new StreamWriter(pipeServer);
                writer.AutoFlush = true;

                while (!ct.IsCancellationRequested && pipeServer.IsConnected)
                {
                    var rpmTask       = _fanControlService.GetFanRpmAsync(ct);
                    var gpuModeTask   = _gpuControlService.GetGpuModeAsync(ct);
                    var gpuPowerTask  = _gpuControlService.GetGpuPowerAsync(ct);
                    var lightingTask  = _lightingService.GetLightingAsync(ct);
                    var profileTask   = _fanControlService.GetThermalProfileAsync(ct);

                    await Task.WhenAll(rpmTask, gpuModeTask, gpuPowerTask, lightingTask, profileTask);

                    var rpm       = rpmTask.Result;
                    var telemetry = _sensorReader.Read(rpm.CpuFanRpm, rpm.GpuFanRpm);

                    telemetry.GpuMode       = gpuModeTask.Result;
                    telemetry.GpuPowerLevel = gpuPowerTask.Result;
                    telemetry.ActiveProfile  = profileTask.Result;
                    telemetry.KeyboardType   = await _lightingService.DetectKeyboardTypeAsync(ct);

                    var lightingResult    = lightingTask.Result;
                    telemetry.BacklightOn = lightingResult.backlightOn;
                    telemetry.ZoneColors  = lightingResult.zoneColors;

                    string json = JsonSerializer.Serialize(telemetry);
                    await writer.WriteLineAsync(json.AsMemory(), ct);

                    await Task.Delay(2000, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[Telemetry] Error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    // ─────────────────── Command Pipe ───────────────────
    // Architecture: Each SendCommandAsync from the App opens a new pipe connection,
    // sends one JSON line, and closes. We use multiple server instances (up to 5)
    // so rapid commands never block each other waiting for a listener.

    private async Task RunCommandServerAsync(CancellationToken ct)
    {
        // Keep a pool of listeners so back-to-back commands never miss a slot.
        const int poolSize = 3;
        var tasks = new Task[poolSize];
        for (int i = 0; i < poolSize; i++)
            tasks[i] = AcceptCommandLoopAsync(ct);
        await Task.WhenAll(tasks);
    }

    private async Task AcceptCommandLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipeServer = new NamedPipeServerStream(
                    "OmenFlow_HardwareCommand",
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipeServer.WaitForConnectionAsync(ct);

                // Handle this connection (one command), then dispose and loop back
                _ = HandleCommandConnectionAsync(pipeServer, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[Command] Listener error: {ex.Message}");
                await Task.Delay(500, ct);
            }
        }
    }

    private async Task HandleCommandConnectionAsync(NamedPipeServerStream pipeServer, CancellationToken ct)
    {
        try
        {
            using var pipe = pipeServer;
            using var reader = new StreamReader(pipe);

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) break;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("Command", out var cmdProp))
                    {
                        string cmd = cmdProp.GetString() ?? "";
                        await DispatchCommandAsync(cmd, root, ct);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Command] Parse/Dispatch error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Command] Connection error: {ex.Message}");
        }
    }

    private async Task DispatchCommandAsync(string cmd, JsonElement root, CancellationToken ct)
    {
        switch (cmd)
        {
            // ── Fan control ──────────────────────────────────────────────
            case "SetFanLevel":
            {
                int level = root.GetProperty("Value").GetInt32();
                Console.WriteLine($"[Command] SetFanLevel: {level}%");
                await _fanCurveService.ApplyCustomCurveAsync(null);
                await _fanControlService.SetFanLevelAsync(level, ct);
                break;
            }

            case "SetAuto":
            {
                Console.WriteLine("[Command] SetAuto");
                await _fanCurveService.ApplyCustomCurveAsync(null);
                await _fanControlService.RestoreAutoControlAsync(ct);
                break;
            }

            case "SetMaxFan":
            {
                Console.WriteLine("[Command] SetMaxFan");
                await _fanCurveService.ApplyCustomCurveAsync(null);
                await _fanControlService.SetMaxFanAsync(true, ct);
                break;
            }

            case "ApplyCurve":
            {
                Console.WriteLine("[Command] ApplyCurve");
                FanCurve? curve = ParseFanCurve(root);
                await _fanCurveService.ApplyCustomCurveAsync(curve);
                break;
            }

            // ── Preset-based fan curve ───────────────────────────────────
            case "ApplyPreset":
            {
                if (!root.TryGetProperty("Value", out var vProp)) break;
                string presetId = vProp.GetString() ?? "";
                Console.WriteLine($"[Command] ApplyPreset: {presetId}");

                var preset = _presetService.GetAll().FirstOrDefault(p => p.Id == presetId);
                if (preset == null)
                {
                    Console.WriteLine($"[Command] Preset '{presetId}' not found.");
                    break;
                }

                if (preset.IsMaxMode)
                {
                    await _fanCurveService.ApplyCustomCurveAsync(null);
                    await _fanControlService.SetMaxFanAsync(true, ct);
                }
                else if (preset.Curve != null)
                {
                    await _fanCurveService.ApplyCustomCurveAsync(preset.Curve);
                }
                else
                {
                    // No curve → restore auto
                    await _fanCurveService.ApplyCustomCurveAsync(null);
                    await _fanControlService.RestoreAutoControlAsync(ct);
                }
                break;
            }

            // ── Thermal profile ──────────────────────────────────────────
            case "SetThermalProfile":
            {
                int profile = root.GetProperty("Value").GetInt32();
                Console.WriteLine($"[Command] SetThermalProfile: 0x{profile:X2} ({(ThermalProfile)profile})");
                await _performanceModeService.SetPerformanceModeAsync((ThermalProfile)profile, ct);
                break;
            }

            // ── GPU ──────────────────────────────────────────────────────
            case "SetGpuMode":
            {
                int mode = root.GetProperty("Value").GetInt32();
                Console.WriteLine($"[Command] SetGpuMode: {(GpuMode)mode}");
                await _gpuControlService.SetGpuModeAsync((GpuMode)mode, ct);
                break;
            }

            case "SetGpuPower":
            {
                int power = root.GetProperty("Value").GetInt32();
                Console.WriteLine($"[Command] SetGpuPower: {(GpuPowerLevel)power}");
                await _gpuControlService.SetGpuPowerAsync((GpuPowerLevel)power, ct);
                break;
            }

            // ── Lighting ─────────────────────────────────────────────────
            case "SetLighting":
            {
                bool on = true;
                string colors = "";

                if (root.TryGetProperty("Value", out var valObj) && valObj.ValueKind == JsonValueKind.Object)
                {
                    if (valObj.TryGetProperty("BacklightOn", out var onProp))
                        on = onProp.GetBoolean();
                    if (valObj.TryGetProperty("ZoneColors", out var cProp))
                        colors = cProp.GetString() ?? "";
                }
                else if (root.TryGetProperty("Value", out var vProp2)) // Fallback
                {
                    on = vProp2.ValueKind == JsonValueKind.True;
                }

                Console.WriteLine($"[Command] SetLighting: On={on}, Colors={colors}");
                _rgbEffectEngine.SetEffect(new OmenFlow.Hardware.Lighting.StaticEffect(string.IsNullOrEmpty(colors) ? "AP8A////////////" : colors));
                break;
            }

            case "SetLightingEffect":
            {
                string effectName = "static";
                if (root.TryGetProperty("Effect", out var eProp))
                    effectName = eProp.GetString() ?? "static";
                else if (root.TryGetProperty("Value", out var vProp3) && vProp3.ValueKind == JsonValueKind.String)
                    effectName = vProp3.GetString() ?? "static";
                else if (root.TryGetProperty("Value", out var vPropObj) && vPropObj.ValueKind == JsonValueKind.Object)
                {
                    if (vPropObj.TryGetProperty("Effect", out var innerE))
                        effectName = innerE.GetString() ?? "static";
                }

                double speed = 0.5;
                if (root.TryGetProperty("Value", out var val) && val.ValueKind == JsonValueKind.Object)
                {
                    if (val.TryGetProperty("Speed", out var speedProp))
                        speed = speedProp.GetDouble();
                }

                double brightness = 1.0;
                if (root.TryGetProperty("Value", out var valB) && valB.ValueKind == JsonValueKind.Object)
                {
                    if (valB.TryGetProperty("Brightness", out var bProp))
                        brightness = bProp.GetDouble();
                }

                string colors = "";
                if (root.TryGetProperty("Value", out var valC) && valC.ValueKind == JsonValueKind.Object)
                {
                    if (valC.TryGetProperty("ZoneColors", out var zcProp))
                        colors = zcProp.GetString() ?? "";
                }

                // If Breathing, we need the primary color (Zone 0) from the base64
                byte r = 255, g = 0, b = 0;
                if (!string.IsNullOrEmpty(colors))
                {
                    try {
                        byte[] decoded = Convert.FromBase64String(colors);
                        if (decoded.Length >= 3) { r = decoded[0]; g = decoded[1]; b = decoded[2]; }
                    } catch {}
                }

                Console.WriteLine($"[Command] SetLightingEffect: {effectName}, Speed={speed}, Brightness={brightness}");

                _rgbEffectEngine.SetEffect(effectName.ToLowerInvariant() switch
                {
                    "breathing"  => (OmenFlow.Hardware.Lighting.IEffect)new OmenFlow.Hardware.Lighting.BreathingEffect(r, g, b, speed),
                    "colorcycle" => new OmenFlow.Hardware.Lighting.ColorCycleEffect(speed, brightness),
                    "wave"       => new OmenFlow.Hardware.Lighting.WaveEffect(speed, brightness),
                    _            => new OmenFlow.Hardware.Lighting.StaticEffect(string.IsNullOrEmpty(colors) ? "AP8A////////////" : colors)
                });
                break;
            }

            // ── Power ────────────────────────────────────────────────────
            case "SetBatteryCare":
            {
                bool enabled = root.GetProperty("Value").GetBoolean();
                Console.WriteLine($"[Command] SetBatteryCare: {enabled}");
                await _powerService.SetBatteryCareModeAsync(
                    enabled ? BatteryCareMode.Enabled : BatteryCareMode.Disabled, ct);
                break;
            }

            case "SetUsbCharging":
            {
                // WMI command TBD — log only
                bool enabled = root.GetProperty("Value").GetBoolean();
                Console.WriteLine($"[Command] SetUsbCharging: {enabled} (not yet implemented)");
                break;
            }

            default:
                Console.WriteLine($"[Command] Unknown command: {cmd}");
                break;
        }
    }

    // ─────────────────── Helpers ───────────────────

    private static FanCurve? ParseFanCurve(JsonElement root)
    {
        if (!root.TryGetProperty("Value", out var valProp) || valProp.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            FanTarget target = FanTarget.Both;
            if (valProp.TryGetProperty("Target", out var tProp))
                target = (FanTarget)tProp.GetInt32();

            var points = new List<FanCurvePoint>();
            if (valProp.TryGetProperty("Points", out var pProp) && pProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var pt in pProp.EnumerateArray())
                {
                    int temp = 0, speed = 0;
                    if (pt.TryGetProperty("TemperatureCelsius", out var tC)) temp  = tC.GetInt32();
                    if (pt.TryGetProperty("FanSpeedPercent",   out var fS)) speed = fS.GetInt32();
                    points.Add(new FanCurvePoint(temp, speed));
                }
            }

            if (points.Count == 0) return null;

            Console.WriteLine($"[Command] FanCurve parsed: {points.Count} points: " +
                              string.Join(", ", points.Select(p => $"{p.TemperatureCelsius}°C→{p.FanSpeedPercent}%")));
            return new FanCurve(target, points);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Command] Failed to parse FanCurve: {ex.Message}");
            return null;
        }
    }
}
