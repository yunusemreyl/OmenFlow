namespace OmenFlow.Core.Interfaces;

public interface IHardwareMonitorService
{
    void Update();
    int GetCpuLoad();
    int GetCpuTemp();
    int GetGpuLoad();
    int GetGpuTemp();
    float GetCpuPower();
    float GetGpuPower();
    int GetRamUsagePercent();
    double GetRamUsedGb();
    double GetRamTotalGb();
}
