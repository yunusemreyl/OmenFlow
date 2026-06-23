using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using System.Text.Json;

using OmenFlow.Core.Models;

namespace OmenFlow.Worker;

public class SensorReader : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor;
    private bool _hardwareLogged = false; // Log hardware list only once

    public SensorReader()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = false,
            IsStorageEnabled = false
        };
        try
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SensorReader] Failed to open computer: {ex}");
        }
        _updateVisitor = new UpdateVisitor();
    }

    public WorkerTelemetry Read(int cpuFanRpm, int gpuFanRpm)
    {
        try
        {
            _computer.Accept(_updateVisitor);

            float cpuTemp = 0f;
            float cpuLoad = 0f;
            float cpuPower = 0f;
            float gpuTemp = 0f;
            float gpuLoad = 0f;
            float gpuPower = 0f;
            float ramUsed = 0f;
            float ramTotal = 0f;

            var allHardware = GetAllHardware(_computer);

            foreach (var hardware in allHardware)
            {
                if (!_hardwareLogged)
                    Console.WriteLine($"[LHM] Found Hardware: {hardware.Name} ({hardware.HardwareType})");
                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    var tempSensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("Package") || s.Name.Contains("Core Max")));
                    if (tempSensor?.Value != null) cpuTemp = tempSensor.Value.Value;

                    var loadSensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Total"));
                    if (loadSensor?.Value != null) cpuLoad = loadSensor.Value.Value;

                    var powerSensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name == "CPU Package")
                                   ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name == "Package Power")
                                   ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name == "Package")
                                   ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power);
                    if (powerSensor?.Value != null) cpuPower = powerSensor.Value.Value;
                }
                else if (hardware.HardwareType == HardwareType.GpuNvidia || hardware.HardwareType == HardwareType.GpuAmd || hardware.HardwareType == HardwareType.GpuIntel)
                {
                    var tempSensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Core"));
                    if (tempSensor?.Value != null) gpuTemp = tempSensor.Value.Value;

                    var loadSensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Core"));
                    if (loadSensor?.Value != null) gpuLoad = loadSensor.Value.Value;

                    var powerSensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name == "GPU Power")
                                   ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name == "Board Power")
                                   ?? hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power);
                    if (powerSensor?.Value != null) gpuPower = powerSensor.Value.Value;
                }
                else if (hardware.HardwareType == HardwareType.Memory)
                {
                    // Filter out "Virtual Memory" to only get physical memory
                    if (hardware.Name != "Virtual Memory")
                    {
                        var usedSensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name.Contains("Memory Used"));
                        if (usedSensor?.Value != null) ramUsed = usedSensor.Value.Value;

                        var availSensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name.Contains("Memory Available"));
                        if (availSensor?.Value != null && usedSensor?.Value != null)
                            ramTotal = ramUsed + availSensor.Value.Value;
                    }
                }
                else if (hardware.HardwareType == HardwareType.Motherboard || hardware.HardwareType == HardwareType.SuperIO)
                {
                    var fans = hardware.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
                    foreach (var f in fans) Console.WriteLine($"[LHM Debug]   Found Fan: {f.Name} = {f.Value}");

                    var cpuFanSensor = fans.FirstOrDefault(s => s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)) ?? fans.FirstOrDefault(s => s.Name.Contains("Fan #1"));
                    if (cpuFanSensor?.Value != null && cpuFanSensor.Value.Value > 0) cpuFanRpm = (int)cpuFanSensor.Value.Value;

                    var gpuFanSensor = fans.FirstOrDefault(s => s.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("System", StringComparison.OrdinalIgnoreCase)) ?? fans.FirstOrDefault(s => s.Name.Contains("Fan #2"));
                    if (gpuFanSensor?.Value != null && gpuFanSensor.Value.Value > 0) gpuFanRpm = (int)gpuFanSensor.Value.Value;
                }
                else if (hardware.HardwareType == HardwareType.EmbeddedController)
                {
                    var fans = hardware.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
                    foreach (var f in fans) Console.WriteLine($"[LHM Debug]   Found Fan: {f.Name} = {f.Value}");

                    var cpuFanSensor = fans.FirstOrDefault(s => s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)) ?? fans.FirstOrDefault();
                    if (cpuFanSensor?.Value != null && cpuFanSensor.Value.Value > 0) cpuFanRpm = (int)cpuFanSensor.Value.Value;

                    var gpuFanSensor = fans.FirstOrDefault(s => s.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)) ?? fans.Skip(1).FirstOrDefault();
                    if (gpuFanSensor?.Value != null && gpuFanSensor.Value.Value > 0) gpuFanRpm = (int)gpuFanSensor.Value.Value;
                }
            }

            var data = new WorkerTelemetry(cpuTemp, gpuTemp, cpuLoad, gpuLoad, cpuPower, gpuPower, ramUsed, ramTotal, cpuFanRpm, gpuFanRpm);
            _hardwareLogged = true; // Suppress repeated hardware enumeration logs
            Console.WriteLine($"READ SENSORS: {data}");
            return data;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SENSOR READ ERROR: {ex}");
            return new WorkerTelemetry(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0, 0);
        }
    }

    public void Dispose()
    {
        _computer.Close();
    }

    private System.Collections.Generic.IEnumerable<IHardware> GetAllHardware(IComputer computer)
    {
        var list = new System.Collections.Generic.List<IHardware>();
        foreach (var hw in computer.Hardware)
        {
            list.Add(hw);
            list.AddRange(GetSubHardware(hw));
        }
        return list;
    }

    private System.Collections.Generic.IEnumerable<IHardware> GetSubHardware(IHardware hw)
    {
        var list = new System.Collections.Generic.List<IHardware>();
        foreach (var sub in hw.SubHardware)
        {
            list.Add(sub);
            list.AddRange(GetSubHardware(sub));
        }
        return list;
    }
}

public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer)
    {
        computer.Traverse(this);
    }
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware subHardware in hardware.SubHardware)
            subHardware.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
