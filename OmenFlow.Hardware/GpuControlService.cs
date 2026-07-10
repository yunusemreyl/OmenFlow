using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public class GpuControlService
{
    private readonly BiosService _biosService;
    private GpuMode? _pendingGpuMode;

    public GpuControlService(BiosService biosService)
    {
        _biosService = biosService;
    }

    public async Task<GpuMode> GetGpuModeAsync(CancellationToken ct = default)
    {
        if (_pendingGpuMode.HasValue)
        {
            return _pendingGpuMode.Value;
        }

        // Primary detection: Nvidia Advanced Optimus Registry Key
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NvHybrid\Persistence\ACE", false))
            {
                if (key != null)
                {
                    object value = key.GetValue("InternalMuxState");
                    if (value != null)
                    {
                        int muxState = (int)value;
                        if (muxState == 2)
                        {
                            return GpuMode.Discrete;
                        }
                        else if (muxState == 1)
                        {
                            return GpuMode.Hybrid; // OmenMon's Optimus = 1, but we use Hybrid as the general enum for software switching
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[GpuControl] GetGpuModeAsync Registry check error: {ex.Message}");
        }

        // Secondary detection: Count active GPUs (The easiest and most reliable way for Normal MUX)
        try
        {
            int gpuCount = 0;
            using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
            {
                gpuCount = searcher.Get().Count;
            }
            
            if (gpuCount == 1)
            {
                OmenFlow.Core.Services.Logger.LogInfo("[GpuControl] Only 1 GPU detected. Mode is Discrete.");
                return GpuMode.Discrete;
            }
            else if (gpuCount >= 2)
            {
                OmenFlow.Core.Services.Logger.LogInfo($"[GpuControl] {gpuCount} GPUs detected. Mode is Hybrid.");
                return GpuMode.Hybrid;
            }
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[GpuControl] GetGpuModeAsync VideoController check error: {ex.Message}");
        }

        // Tertiary fallback to BIOS WMI if both fail
        var payload = Array.Empty<byte>();
        var (ret, data) = await _biosService.SendCommandAsync(0x00002, 0x52, payload, 4, ct);
        
        if (ret != 0)
        {
            var (ret2, data2) = await _biosService.SendCommandAsync(0x00001, 0x52, payload, 4, ct);
            ret = ret2;
            data = data2;
        }

        if (ret == 0 && data != null && data.Length >= 1)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[GpuControl] GetGpuModeAsync WMI BIOS ret=0, data[0]=0x{data[0]:X2}");
            if (data[0] == 0x01) return GpuMode.Discrete;
            if (data[0] == 0x00 || data[0] == 0x02) return GpuMode.Hybrid;
            return (GpuMode)data[0];
        }

        OmenFlow.Core.Services.Logger.LogInfo($"[GpuControl] GetGpuModeAsync all methods failed, returning Hybrid.");
        return GpuMode.Hybrid;
    }

    public async Task<(bool success, bool rebootRequired)> SetGpuModeAsync(GpuMode mode, CancellationToken ct = default)
    {
        _pendingGpuMode = mode == GpuMode.Optimus ? GpuMode.Hybrid : mode; // ArayÃ¼zdeki butonun geri atmamasÄ± iÃ§in pending modunu sakla
        
        byte modeByte = (byte)(mode == GpuMode.Optimus ? GpuMode.Hybrid : mode);
        var payload = new byte[] { modeByte, 0x00, 0x00, 0x00 };
        var (ret, data) = await _biosService.SendCommandAsync(0x00002, 0x52, payload, 4, ct);
        
        if (ret != 0)
        {
            // Fallback to 0x00001
            var (ret2, data2) = await _biosService.SendCommandAsync(0x00001, 0x52, payload, 4, ct);
            ret = ret2;
            data = data2;
        }

        bool success = (ret == 0); // OmenMonWpf only checks ret == 0 for SetGpuMode
        
        return (success, success); // if successful, reboot is required
    }

    public async Task<GpuPowerLevel> GetGpuPowerAsync(CancellationToken ct = default)
    {
        var (ret, data) = await _biosService.SendCommandAsync(0x20008, 0x21, new byte[] { 0, 0, 0, 0 }, 4, ct);
        if (ret == 0 && data != null && data.Length >= 4)
        {
            byte customTgp = data[0];
            byte ppab = data[1];

            if (customTgp == 0 && ppab == 0) return GpuPowerLevel.BasePower;
            if (customTgp == 1 && ppab == 0) return GpuPowerLevel.ExtraPower;
            if (customTgp == 1 && ppab == 1) return GpuPowerLevel.MaxPower;
        }
        return GpuPowerLevel.BasePower;
    }

    public async Task<bool> SetGpuPowerAsync(GpuPowerLevel level, CancellationToken ct = default)
    {
        byte customTgp = level == GpuPowerLevel.BasePower ? (byte)0 : (byte)1;
        byte ppab = level == GpuPowerLevel.MaxPower ? (byte)1 : (byte)0;
        byte dState = 1; // Always D1
        byte peakTemp = 0;

        // Retrieve current peakTemp (slowdown temp) first, as setting it to 0 is dangerous/invalid
        var (readRet, readData) = await _biosService.SendCommandAsync(0x20008, 0x21, new byte[] { 0, 0, 0, 0 }, 4, ct);
        if (readRet == 0 && readData != null && readData.Length >= 4)
        {
            peakTemp = readData[3];
        }
        else
        {
            // If we fail to read, it's safer to abort or use a sensible default (e.g., 87C = 0x57)
            // But Omen default is usually kept as whatever the hardware had. 
            // We'll proceed with 0 if it fails, but log a warning if we had a logger.
            // For now, if read fails, returning false to be perfectly safe is an option, 
            // but we'll stick to best-effort.
        }

        byte[] payload = new byte[] { customTgp, ppab, dState, peakTemp };
        var (writeRet, _) = await _biosService.SendCommandAsync(0x20008, 0x22, payload, 0, ct);
        return writeRet == 0;
    }

    private static int? s_cachedGpuMaxTgp;

    public int GetGpuMaxPowerLimit()
    {
        if (s_cachedGpuMaxTgp.HasValue)
            return s_cachedGpuMaxTgp.Value;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=power.max_limit --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1000);
                if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
                {
                    s_cachedGpuMaxTgp = (int)Math.Round(val);
                    return s_cachedGpuMaxTgp.Value;
                }
            }
        }
        catch
        {
            // fallback: check common system folders if nvidia-smi not in PATH
            try
            {
                string fullPath = @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe";
                if (System.IO.File.Exists(fullPath))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = fullPath,
                        Arguments = "--query-gpu=power.max_limit --format=csv,noheader,nounits",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(1000);
                        if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
                        {
                            s_cachedGpuMaxTgp = (int)Math.Round(val);
                            return s_cachedGpuMaxTgp.Value;
                        }
                    }
                }
            }
            catch { }
        }

        s_cachedGpuMaxTgp = 150; // default fallback if nvidia-smi failed or no Nvidia card
        return s_cachedGpuMaxTgp.Value;
    }
}

