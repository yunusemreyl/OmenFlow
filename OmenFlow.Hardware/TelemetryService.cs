using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public class TelemetryService : ITelemetryService, IDisposable
{
    private readonly IFanControlService _fanControlService;
    private readonly IHardwareMonitorService _hardwareMonitorService;
    private Timer? _timer;
    private ThermalProfile _currentProfile = ThermalProfile.Default;

    public event EventHandler<HardwareTelemetryMessage>? TelemetryUpdated;

    public TelemetryService(IFanControlService fanControlService, IHardwareMonitorService hardwareMonitorService)
    {
        _fanControlService = fanControlService;
        _hardwareMonitorService = hardwareMonitorService;
    }

    public void Start(TimeSpan interval)
    {
        _timer?.Dispose();
        _timer = new Timer(OnTick, null, TimeSpan.Zero, interval);
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, 0);
    }

    private async void OnTick(object? state)
    {
        try
        {
            _hardwareMonitorService.Update();
            var cpuTemp = await _fanControlService.GetCpuTemperatureAsync();
            var gpuTemp = _hardwareMonitorService.GetGpuTemp();
            var cpuLoad = _hardwareMonitorService.GetCpuLoad();
            var gpuLoad = _hardwareMonitorService.GetGpuLoad();
            var cpuPower = _hardwareMonitorService.GetCpuPower();
            var gpuPower = _hardwareMonitorService.GetGpuPower();
            var ramUsagePercent = _hardwareMonitorService.GetRamUsagePercent();
            var ramUsedGb = _hardwareMonitorService.GetRamUsedGb();
            var ramTotalGb = _hardwareMonitorService.GetRamTotalGb();

            var (cpuRpm, gpuRpm) = await _fanControlService.GetFanRpmAsync();
            var activeProfile = _currentProfile;

            var message = new HardwareTelemetryMessage(
                cpuRpm, 
                gpuRpm, 
                cpuTemp, 
                gpuTemp, 
                cpuLoad,
                gpuLoad,
                cpuPower,
                gpuPower,
                ramUsagePercent,
                ramUsedGb,
                ramTotalGb,
                activeProfile);

            TelemetryUpdated?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[ERROR] Telemetry error: {ex}");
        }
    }

    public void UpdateActiveProfile(ThermalProfile profile)
    {
        _currentProfile = profile;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}

