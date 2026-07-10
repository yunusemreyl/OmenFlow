using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Hardware;
using OmenFlow.Core.Models;

namespace OmenFlow.TestConsole;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== OmenFlow PawnIO EC & WMI Hybrid Fan Control Test ===");

        try
        {
            var capabilityService = new CapabilityDetectionService();
            var boardConfig = capabilityService.DetectBoard();
            using var ecService = new EcService(boardConfig);
            using var biosService = new BiosService();
            using var fanControlService = new FanControlService(biosService, boardConfig, ecService);
            using var fanCurveService = new FanCurveHostedService(fanControlService);
            using var sensorReader = new OmenFlow.Worker.SensorReader();
            var gpuControlService = new GpuControlService(biosService);
            var lightingService = new KeyboardLightingService(biosService);
            using var rgbEffectEngine = new OmenFlow.Hardware.Lighting.RgbEffectEngine(lightingService);
            var perfModeService = new PerformanceModeService(biosService, ecService, boardConfig, gpuControlService);

            // New Services
            var fanVerifyService = new FanVerificationService(fanControlService, boardConfig);
            var fanCalibService = new FanCalibrationService(boardConfig);
            var powerLimitService = new PowerLimitService(biosService, ecService, boardConfig);
            
            using var powerAutoService = new PowerAutomationService(perfModeService);
            _ = powerAutoService.StartAsync(CancellationToken.None);

            using var quietSafety = new QuietSafetyMonitor(perfModeService);
            quietSafety.TelemetryProvider = () => sensorReader.Read(0, 0);
            _ = quietSafety.StartAsync(CancellationToken.None);

            var diagnosticsService = new DiagnosticsExportService(
                fanCurveService, fanCalibService, ecService, boardConfig, powerLimitService, fanVerifyService);
            diagnosticsService.TelemetryProvider = () => sensorReader.Read(0, 0);
            diagnosticsService.CurrentProfileProvider = () => perfModeService.GetCurrentModeAsync().GetAwaiter().GetResult();
            diagnosticsService.CurrentFanModeProvider = () => 0;

            // Bind telemetry to curve service
            fanCurveService.TelemetryProvider = () => sensorReader.Read(0, 0);
            _ = fanCurveService.StartAsync(CancellationToken.None);

            Console.WriteLine("Interactive EC Test Mode Started.");
            Console.WriteLine("Type 'q' to quit.");
            Console.WriteLine("Type 'read <hex_addr>' to read a register.");
            Console.WriteLine("Type 'write <hex_addr> <hex_val>' to write to a register.");
            Console.WriteLine("Type 'rpm' to read current RPMs.");
            Console.WriteLine("Type 'fan <percent>' to set custom fan level via Backend.");
            Console.WriteLine("Type 'maxon' / 'maxoff' to toggle Max Fan via Backend.");
            Console.WriteLine("Type 'auto' to restore BIOS Auto Fan Control via Backend.");
            Console.WriteLine("Type 'pl <pl1> <pl2> <tgp>' to set CPU Power limits and GPU TGP (e.g. pl 45 90 80).");
            Console.WriteLine("Type 'autoac <0|1> [ac_profile] [bat_profile]' to toggle Power Automation (e.g. autoac 1 Performance Quiet).");
            Console.WriteLine("Type 'safety <0|1>' to toggle Quiet Safety Monitor.");
            Console.WriteLine("Type 'diag' to export complete Diagnostics ZIP to Desktop.");
            Console.WriteLine("Type 'history' to print last 80 fan commands report.");
            Console.WriteLine("Type 'mux <0|1>' to set GPU Mode (0=Hybrid, 1=Discrete).");
            Console.WriteLine("Type 'power <0|1|2>' to set GPU Power (0=Base, 1=Extra, 2=Max).");
            Console.WriteLine("Type 'rgb <on|off> [hex1] [hex2] [hex3] [hex4]' to set RGB (e.g. rgb on FF0000 00FF00 0000FF FFFFFF).");
            Console.WriteLine("Type 'effect <breathing|colorcycle|wave|stop>' to test RGB animations.");
            Console.WriteLine("Type 'perf <quiet|default|performance>' to set Thermal Profile via PerformanceModeService.");
            Console.WriteLine("Type 'status' to view GPU, Lighting and Power limits status.");
            Console.WriteLine("Type 'wmi <hex_cmd> [hex_data1] [hex_data2] ...' to send raw WMI BIOS commands.");
            Console.WriteLine("Type 'dump' to read all EC registers from 0x00 to 0xFF.");
            Console.WriteLine();

            while (true)
            {
                Console.Write("\n> ");
                var line = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.ToLower() == "q") break;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var input = line.ToLower();
                
                try
                {
                    if (parts[0] == "status")
                    {
                        var mode = await gpuControlService.GetGpuModeAsync();
                        var power = await gpuControlService.GetGpuPowerAsync();
                        var kbdType = await lightingService.DetectKeyboardTypeAsync();
                        var light = await lightingService.GetLightingAsync();
                        var profile = await perfModeService.GetCurrentModeAsync();
                        
                        Console.WriteLine($"Active Profile: {profile}");
                        Console.WriteLine($"GPU Mode: {mode}");
                        Console.WriteLine($"GPU Power Level: {power}");
                        Console.WriteLine($"Keyboard Type: {kbdType}");
                        Console.WriteLine($"Backlight On: {light.backlightOn}");
                        Console.WriteLine($"Power Automation: Enabled={powerAutoService.IsEnabled}, AC={powerAutoService.OnAcProfile}, Bat={powerAutoService.OnBatProfile}");
                        Console.WriteLine($"Quiet Safety Monitor: Enabled={quietSafety.IsEnabled}");
                        Console.WriteLine($"Power Limit State: {powerLimitService.GetDiagnosticsSummary()}");
                        if (!string.IsNullOrEmpty(light.zoneColors))
                            Console.WriteLine($"Zone Colors (base64): {light.zoneColors}");
                    }
                    else if (parts[0] == "pl" && parts.Length == 4)
                    {
                        int pl1 = int.Parse(parts[1]);
                        int pl2 = int.Parse(parts[2]);
                        int tgp = int.Parse(parts[3]);
                        bool cpuOk = await powerLimitService.SetCpuPowerLimitsAsync(pl1, pl2);
                        bool gpuOk = await powerLimitService.SetGpuTgpAsync(tgp);
                        Console.WriteLine($"Power limits applied → CPU PL1/PL2: {cpuOk}, GPU TGP: {gpuOk}");
                    }
                    else if (parts[0] == "autoac" && parts.Length >= 2)
                    {
                        bool enable = parts[1] == "1";
                        powerAutoService.IsEnabled = enable;
                        if (parts.Length >= 4)
                        {
                            if (Enum.TryParse<ThermalProfile>(parts[2], true, out var acP)) powerAutoService.OnAcProfile = acP;
                            if (Enum.TryParse<ThermalProfile>(parts[3], true, out var batP)) powerAutoService.OnBatProfile = batP;
                        }
                        powerAutoService.SaveConfig();
                        if (enable) await powerAutoService.ForceApplyCurrentSourceAsync();
                        Console.WriteLine($"Power Automation set to: {enable} (AC={powerAutoService.OnAcProfile}, Bat={powerAutoService.OnBatProfile})");
                    }
                    else if (parts[0] == "safety" && parts.Length == 2)
                    {
                        bool enable = parts[1] == "1";
                        quietSafety.IsEnabled = enable;
                        Console.WriteLine($"Quiet Safety Monitor set to: {enable}");
                    }
                    else if (parts[0] == "diag")
                    {
                        Console.WriteLine("Generating Diagnostics ZIP...");
                        string zipPath = await diagnosticsService.ExportAsync();
                        Console.WriteLine($"Diagnostics ZIP saved to: {zipPath}");
                    }
                    else if (parts[0] == "history")
                    {
                        Console.WriteLine("Fan Command History Report:");
                        Console.WriteLine(fanCurveService.GetCommandHistoryReport());
                    }
                    else if (parts[0] == "mux" && parts.Length == 2)
                    {
                        int m = int.Parse(parts[1]);
                        var (success, rebootReq) = await gpuControlService.SetGpuModeAsync((GpuMode)m);
                        Console.WriteLine($"SetGpuModeAsync({(GpuMode)m}) -> Success: {success}, RebootRequired: {rebootReq}");
                    }
                    else if (parts[0] == "power" && parts.Length == 2)
                    {
                        int p = int.Parse(parts[1]);
                        bool success = await gpuControlService.SetGpuPowerAsync((GpuPowerLevel)p);
                        Console.WriteLine($"SetGpuPowerAsync({(GpuPowerLevel)p}) -> Success: {success}");
                    }
                    else if (parts[0] == "perf" && parts.Length == 2)
                    {
                        if (Enum.TryParse<ThermalProfile>(parts[1], true, out var mode))
                        {
                            bool success = await perfModeService.SetPerformanceModeAsync(mode);
                            Console.WriteLine($"PerformanceModeService.SetPerformanceModeAsync({mode}) -> Success: {success}");
                        }
                        else
                        {
                            Console.WriteLine("Invalid mode. Try: quiet, default, performance");
                        }
                    }
                    else if (parts[0] == "profile" && parts.Length == 2)
                    {
                        if (Enum.TryParse<ThermalProfile>(parts[1], true, out var profile))
                        {
                            bool success = await perfModeService.SetPerformanceModeAsync(profile);
                            Console.WriteLine($"SetPerformanceModeAsync({profile}) -> Success: {success}");
                        }
                        else 
                        {
                            Console.WriteLine("Invalid profile value");
                        }
                    }
                    else if (parts[0] == "rgb" && parts.Length >= 2)
                    {
                        bool on = parts[1] == "on";
                        string base64Colors = "";

                        if (on && parts.Length >= 3)
                        {
                            byte[] zones = new byte[12];
                            int zoneIdx = 0;
                            for (int i = 2; i < parts.Length && zoneIdx < 4; i++)
                            {
                                string hex = parts[i].PadLeft(6, '0');
                                try
                                {
                                    zones[zoneIdx * 3] = Convert.ToByte(hex.Substring(0, 2), 16);     // R
                                    zones[zoneIdx * 3 + 1] = Convert.ToByte(hex.Substring(2, 2), 16); // G
                                    zones[zoneIdx * 3 + 2] = Convert.ToByte(hex.Substring(4, 2), 16); // B
                                    zoneIdx++;
                                }
                                catch { }
                            }
                            
                            for (int i = zoneIdx; i < 4; i++)
                            {
                                zones[i * 3] = 255;
                                zones[i * 3 + 1] = 255;
                                zones[i * 3 + 2] = 255;
                            }
                            base64Colors = Convert.ToBase64String(zones);
                        }

                        bool success = await lightingService.SetLightingAsync(on, base64Colors);
                        Console.WriteLine($"SetLightingAsync({on}, {(string.IsNullOrEmpty(base64Colors) ? "Default" : "Custom")}) -> Success: {success}");
                    }
                    else if (parts[0] == "effect" && parts.Length >= 2)
                    {
                        string effectName = parts[1].ToLowerInvariant();
                        if (effectName == "breathing")
                        {
                            rgbEffectEngine.SetEffect(new OmenFlow.Hardware.Lighting.BreathingEffect(255, 0, 0, 0.5));
                            Console.WriteLine("Started Red Breathing Effect.");
                        }
                        else if (effectName == "colorcycle")
                        {
                            rgbEffectEngine.SetEffect(new OmenFlow.Hardware.Lighting.ColorCycleEffect(0.2));
                            Console.WriteLine("Started ColorCycle Effect.");
                        }
                        else if (effectName == "wave")
                        {
                            rgbEffectEngine.SetEffect(new OmenFlow.Hardware.Lighting.WaveEffect(0.5));
                            Console.WriteLine("Started Wave Effect.");
                        }
                        else if (effectName == "stop" || effectName == "static")
                        {
                            rgbEffectEngine.SetEffect(new OmenFlow.Hardware.Lighting.StaticEffect("AP8A////////////"));
                            Console.WriteLine("Stopped effect (reverted to Static White).");
                        }
                        else
                        {
                            Console.WriteLine($"Unknown effect: {effectName}. Try: breathing, colorcycle, wave, stop");
                        }
                    }
                    else if (parts[0] == "rpm")
                    {
                        var rpm = await fanControlService.GetFanRpmAsync();
                        var telemetry = sensorReader.Read(rpm.CpuFanRpm, rpm.GpuFanRpm);
                        Console.WriteLine($"CPU: {telemetry.CpuFanRpm} RPM, GPU: {telemetry.GpuFanRpm} RPM");
                    }
                    else if (parts[0] == "fan" && parts.Length == 2)
                    {
                        int pct = int.Parse(parts[1]);
                        bool success = await fanControlService.SetFanLevelAsync(pct);
                        Console.WriteLine($"SetFanLevelAsync({pct}%) -> Success: {success}");
                    }
                    else if (parts[0] == "maxon")
                    {
                        bool success = await fanControlService.SetMaxFanAsync(true);
                        Console.WriteLine($"SetMaxFanAsync(true) -> Success: {success}");
                    }
                    else if (parts[0] == "maxoff")
                    {
                        bool success = await fanControlService.SetMaxFanAsync(false);
                        Console.WriteLine($"SetMaxFanAsync(false) -> Success: {success}");
                    }
                    else if (parts[0] == "auto")
                    {
                        bool success = await fanControlService.RestoreAutoControlAsync();
                        Console.WriteLine($"RestoreAutoControlAsync() -> Success: {success}");
                    }
                    else if (input == "dump")
                    {
                        Console.WriteLine("Dumping EC Registers 0x00 to 0xFF...");
                        for (int i = 0; i < 256; i++)
                        {
                            try
                            {
                                byte val = await ecService.ReadByteAsync((byte)i);
                                if (val != 0 && val != 0xFF)
                                {
                                    Console.WriteLine($"0x{i:X2} = 0x{val:X2} ({val})");
                                }
                            }
                            catch { }
                        }
                        Console.WriteLine("Dump complete.");
                    }
                    else if (parts[0] == "read" && parts.Length == 2)
                    {
                        byte addr = Convert.ToByte(parts[1], 16);
                        byte val = await ecService.ReadByteAsync(addr);
                        Console.WriteLine($"Register 0x{addr:X2} = 0x{val:X2} ({val})");
                    }
                    else if (parts[0] == "write" && parts.Length == 3)
                    {
                        byte addr = Convert.ToByte(parts[1], 16);
                        byte val = Convert.ToByte(parts[2], 16);
                        await ecService.WriteByteAsync(addr, val);
                        Console.WriteLine($"Wrote 0x{val:X2} to 0x{addr:X2}");
                    }
                    else if (parts[0] == "wmi" && parts.Length >= 2)
                    {
                        byte cmd = Convert.ToByte(parts[1], 16);
                        byte[] data = new byte[parts.Length - 2];
                        for (int i = 0; i < data.Length; i++)
                        {
                            data[i] = Convert.ToByte(parts[i + 2], 16);
                        }
                        
                        var (ret, outData) = await biosService.SendCommandAsync(0x20008, cmd, data, 128);
                        
                        Console.WriteLine($"WMI Command 0x{cmd:X2} executed. Return Code: {ret}");
                        if (outData != null && outData.Length > 0)
                        {
                            Console.WriteLine($"Output: {BitConverter.ToString(outData).Replace("-", " ")}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Unknown command.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal Error: {ex.Message}");
        }
    }
}

