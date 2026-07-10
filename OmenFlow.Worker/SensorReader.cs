using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using OmenFlow.Core.Models;

namespace OmenFlow.Worker;

/// <summary>
/// LibreHardwareMonitor (LHM) üzerinden sistem metriklerini okur.
///
/// OmenFlow Hibrid Mimarisindeki Rolü (WmiBiosMonitor ile birlikte):
///   - CPU Load % (Total)
///   - GPU Load % (Core)
///   - CPU Package Power (W)
///   - GPU Power (W)
///   - RAM Kullanımı (Used GB / Total GB)
///
/// NOTLAR:
///   - Sıcaklık ve Fan RPM birincil olarak HP WMI BIOS'tan okunur (WmiBiosMonitor).
///     LHM bu değerleri yalnızca WMI kullanılamadığında veya sensör kilitlendiğinde sağlar.
///   - IsMotherboardEnabled ve IsControllerEnabled kasıtlı olarak false bırakılmıştır:
///     Fan RPM'i WMI/EC doğrudan okunduğu için LHM'nin bu sensörleri taraması gereksizdir,
///     sadece başlangıç süresini ve CPU kullanımını artırırdı.
/// </summary>
public class SensorReader : IDisposable
{
    private const int FanTransitionHoldReads = 3;

    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor;
    private bool _hardwareLogged = false;
    private int _cpuFanZeroStreak = 0;
    private int _gpuFanZeroStreak = 0;
    private int _cpuFanLastNonZeroRpm = 0;
    private int _gpuFanLastNonZeroRpm = 0;
    private FanRpmState _cpuFanState = FanRpmState.Unknown;
    private FanRpmState _gpuFanState = FanRpmState.Unknown;

    public SensorReader()
    {
        _computer = new Computer
        {
            IsCpuEnabled    = true,
            IsGpuEnabled    = true,
            IsMemoryEnabled = true,

            // Açık: LHM üzerinden Fan RPM'leri okumak için gerekli.
            IsMotherboardEnabled  = true,
            IsControllerEnabled   = true,
            IsNetworkEnabled      = false,
            IsStorageEnabled      = false
        };

        try
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SensorReader] LHM açılamadı: {ex.Message}");
        }

        _updateVisitor = new UpdateVisitor();
    }

    /// <summary>
    /// Tüm LHM donanımını günceller ve metrikleri döndürür.
    /// Dahili fan RPM'leri 0 çünkü WMI/EC doğrudan enjekte edilir — yine de
    /// dolu WorkerTelemetry formatında döndürülür (geriye dönük uyumluluk).
    /// </summary>
    public WorkerTelemetry Read(int cpuFanRpm = 0, int gpuFanRpm = 0)
    {
        try
        {
            _computer.Accept(_updateVisitor);

            float cpuTemp = 0f, cpuLoad = 0f, cpuPower = 0f;
            float gpuTemp = 0f, gpuLoad = 0f, gpuPower = 0f;
            float ramUsed = 0f, ramTotal = 0f;

            int lhmCpuFanRpm = 0;
            int lhmGpuFanRpm = 0;

            foreach (var hw in GetAllHardware(_computer))
            {
                if (!_hardwareLogged)
                    Console.WriteLine($"[LHM] Donanım: {hw.Name} ({hw.HardwareType})");

                // Fan RPM sensörlerini tara
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType == SensorType.Fan)
                    {
                        string name = s.Name.ToLowerInvariant();
                        if (name.Contains("cpu") || name.Contains("fan #1") || name.Contains("1"))
                        {
                            lhmCpuFanRpm = (int)(s.Value ?? 0f);
                        }
                        else if (name.Contains("gpu") || name.Contains("fan #2") || name.Contains("2"))
                        {
                            lhmGpuFanRpm = (int)(s.Value ?? 0f);
                        }
                    }
                }

                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        cpuTemp  = ReadSensor(hw, SensorType.Temperature, s => s.Name.Contains("Package") || s.Name.Contains("Core Max")) ?? 0f;
                        cpuLoad  = ReadSensor(hw, SensorType.Load,        s => s.Name.Contains("Total")) ?? 0f;
                        cpuPower = ReadSensor(hw, SensorType.Power,
                            s => s.Name is "CPU Package" or "Package Power" or "Package")
                            ?? ReadSensor(hw, SensorType.Power, _ => true) ?? 0f;
                        break;

                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        gpuTemp  = ReadSensor(hw, SensorType.Temperature, s => s.Name.Contains("Core")) ?? 0f;
                        gpuLoad  = ReadSensor(hw, SensorType.Load,        s => s.Name.Contains("Core")) ?? 0f;
                        gpuPower = ReadSensor(hw, SensorType.Power,
                            s => s.Name is "GPU Power" or "Board Power")
                            ?? ReadSensor(hw, SensorType.Power, _ => true) ?? 0f;
                        break;

                    case HardwareType.Memory:
                        if (hw.Name != "Virtual Memory")
                        {
                            float used  = ReadSensor(hw, SensorType.Data, s => s.Name.Contains("Memory Used")) ?? 0f;
                            float avail = ReadSensor(hw, SensorType.Data, s => s.Name.Contains("Memory Available")) ?? 0f;
                            if (used > 0) { ramUsed = used; ramTotal = used + avail; }
                        }
                        break;
                }
            }

            _hardwareLogged = true;

            if (cpuFanRpm == 0) cpuFanRpm = lhmCpuFanRpm;
            if (gpuFanRpm == 0) gpuFanRpm = lhmGpuFanRpm;

            cpuFanRpm = ResolveFanRpm(cpuFanRpm, cpuTemp, ref _cpuFanZeroStreak, ref _cpuFanLastNonZeroRpm, ref _cpuFanState);
            gpuFanRpm = ResolveFanRpm(gpuFanRpm, gpuTemp, ref _gpuFanZeroStreak, ref _gpuFanLastNonZeroRpm, ref _gpuFanState);

            return new WorkerTelemetry(cpuTemp, gpuTemp, cpuLoad, gpuLoad, cpuPower, gpuPower,
                                       ramUsed, ramTotal, cpuFanRpm, gpuFanRpm, _cpuFanState, _gpuFanState);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SensorReader] Okuma hatası: {ex.Message}");
            return CreateEmpty();
        }
    }

    /// <summary>
    /// WmiBiosMonitor arka plan döngüsü için optimize edilmiş okuma.
    /// Tam Read() ile aynıdır; fan RPM enjeksiyonu olmaksızın çağrılır.
    /// </summary>
    public WorkerTelemetry ReadLightweight() => Read(0, 0);

    // ── Yardımcılar ──────────────────────────────────────────────────────

    private static float? ReadSensor(IHardware hw, SensorType type, Func<ISensor, bool> predicate)
    {
        var sensor = hw.Sensors.FirstOrDefault(s => s.SensorType == type && predicate(s));
        return sensor?.Value;
    }

    private static int ResolveFanRpm(int currentRpm, float tempC,
        ref int zeroStreak, ref int lastNonZeroRpm, ref FanRpmState state)
    {
        if (currentRpm > 0)
        {
            zeroStreak      = 0;
            lastNonZeroRpm  = currentRpm;
            state           = FanRpmState.Stable;
            return currentRpm;
        }

        if (lastNonZeroRpm > 0 && zeroStreak < FanTransitionHoldReads)
        {
            zeroStreak++;
            state = FanRpmState.TransitionHold;
            return lastNonZeroRpm;
        }

        zeroStreak++;
        state = FanRpmState.IdleStopped;
        return 0;
    }

    private IEnumerable<IHardware> GetAllHardware(IComputer computer)
    {
        var list = new System.Collections.Generic.List<IHardware>();
        foreach (var hw in computer.Hardware)
        {
            list.Add(hw);
            list.AddRange(GetSubHardware(hw));
        }
        return list;
    }

    private IEnumerable<IHardware> GetSubHardware(IHardware hw)
    {
        var list = new System.Collections.Generic.List<IHardware>();
        foreach (var sub in hw.SubHardware)
        {
            list.Add(sub);
            list.AddRange(GetSubHardware(sub));
        }
        return list;
    }

    private static WorkerTelemetry CreateEmpty() =>
        new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0, 0, FanRpmState.Unknown, FanRpmState.Unknown);

    public void Dispose()
    {
        _computer.Close();
    }
}

public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware) sub.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
