using System;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public class HardwareMonitorService : IHardwareMonitorService, IDisposable
{
    private volatile WorkerTelemetry? _latest;

    public HardwareMonitorService(IWorkerService workerService)
    {
        workerService.TelemetryReceived += OnTelemetryReceived;
    }

    private void OnTelemetryReceived(object? sender, WorkerTelemetry telemetry)
    {
        _latest = telemetry;
    }

    public void Update()
    {
        // No-op, data arrives asynchronously via Named Pipe
    }

    public int GetCpuLoad()
    {
        return (int)(_latest?.CpuLoad ?? 0f);
    }

    public int GetCpuTemp()
    {
        return (int)(_latest?.CpuTemp ?? 0f);
    }

    public float GetCpuPower()
    {
        return _latest?.CpuPower ?? 0f;
    }

    public int GetGpuLoad()
    {
        return (int)(_latest?.GpuLoad ?? 0f);
    }

    public int GetGpuTemp()
    {
        return (int)(_latest?.GpuTemp ?? 0f);
    }

    public float GetGpuPower()
    {
        return _latest?.GpuPower ?? 0f;
    }

    public int GetRamUsagePercent()
    {
        var used = _latest?.RamUsedGb ?? 0f;
        var total = _latest?.RamTotalGb ?? 0f;
        if (total == 0) return 0;
        return (int)((used / total) * 100);
    }

    public double GetRamUsedGb()
    {
        return _latest?.RamUsedGb ?? 0d;
    }

    public double GetRamTotalGb()
    {
        return _latest?.RamTotalGb ?? 0d;
    }

    public void Dispose()
    {
        // workerService.TelemetryReceived unsubscribe omitted for brevity, 
        // normally injected as singleton so it lives as long as the app
    }
}
