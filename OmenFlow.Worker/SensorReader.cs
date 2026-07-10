using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using OmenFlow.Core.Models;

namespace OmenFlow.Worker;

/// <summary>
/// LibreHardwareMonitor (LHM) Ã¼zerinden sistem metriklerini okur.
///
/// OmenFlow Hibrid Mimarisindeki RolÃ¼ (WmiBiosMonitor ile birlikte):
///   - CPU Load % (Total)
///   - GPU Load % (Core)
///   - CPU Package Power (W)
///   - GPU Power (W)
///   - RAM KullanÄ±mÄ± (Used GB / Total GB)
///
/// NOTLAR:
///   - SÄ±caklÄ±k ve Fan RPM birincil olarak HP WMI BIOS'tan okunur (WmiBiosMonitor).
///     LHM bu deÄŸerleri yalnÄ±zca WMI kullanÄ±lamadÄ±ÄŸÄ±nda veya sensÃ¶r kilitlendiÄŸinde saÄŸlar.
///   - IsMotherboardEnabled ve IsControllerEnabled kasÄ±tlÄ± olarak false bÄ±rakÄ±lmÄ±ÅŸtÄ±r:
///     Fan RPM'i WMI/EC doÄŸrudan okunduÄŸu iÃ§in LHM'nin bu sensÃ¶rleri taramasÄ± gereksizdir,
///     sadece baÅŸlangÄ±Ã§ sÃ¼resini ve CPU kullanÄ±mÄ±nÄ± artÄ±rÄ±rdÄ±.
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

            // AÃ§Ä±k: LHM Ã¼zerinden Fan RPM'leri okumak iÃ§in gerekli.
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
            OmenFlow.Core.Services.Logger.LogInfo($"[SensorReader] LHM aÃ§Ä±lamadÄ±: {ex.Message}");
        }

        _updateVisitor = new UpdateVisitor();
    }

    /// <summary>
    /// TÃ¼m LHM donanÄ±mÄ±nÄ± gÃ¼nceller ve metrikleri dÃ¶ndÃ¼rÃ¼r.
    /// Dahili fan RPM'leri 0 Ã§Ã¼nkÃ¼ WMI/EC doÄŸrudan enjekte edilir â€” yine de
    /// dolu WorkerTelemetry formatÄ±nda dÃ¶ndÃ¼rÃ¼lÃ¼r (geriye dÃ¶nÃ¼k uyumluluk).
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
                    OmenFlow.Core.Services.Logger.LogInfo($"[LHM] DonanÄ±m: {hw.Name} ({hw.HardwareType})");

                // Fan RPM sensÃ¶rlerini tara
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
                        cpuTemp  = ReadSensor(hw, SensorType.Temperature, s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Core Max", StringComparison.OrdinalIgnoreCase)) ?? 0f;
                        cpuLoad  = ReadSensor(hw, SensorType.Load,        s => s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)) ?? 0f;
                        cpuPower = ReadSensor(hw, SensorType.Power,
                            s => s.Name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase)
                              || s.Name.Contains("Package Power", StringComparison.OrdinalIgnoreCase)
                              || s.Name.Equals("Package", StringComparison.OrdinalIgnoreCase))
                            ?? ReadSensor(hw, SensorType.Power, _ => true) ?? 0f;
                        break;

                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        {
                            // Entegre GPU'larÄ±n (genelde Intel/AMD) harici (NVIDIA) GPU'yu ezmesini engelle
                            bool isIntegrated = hw.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) 
                                             || hw.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) 
                                             || hw.Name.Contains("UHD", StringComparison.OrdinalIgnoreCase)
                                             || hw.Name.Contains("Iris", StringComparison.OrdinalIgnoreCase);

                            // EÄŸer elimizde zaten aktif bir harici GPU Ã¶lÃ§Ã¼mÃ¼ varsa entegre GPU'yu atla
                            if (isIntegrated && gpuPower > 0)
                                break;

                            float tempVal  = ReadSensor(hw, SensorType.Temperature, s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)) ?? 0f;
                            float loadVal  = ReadSensor(hw, SensorType.Load,        s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)) ?? 0f;
                            float powerVal = ReadSensor(hw, SensorType.Power,
                                s => s.Name.Contains("GPU Power", StringComparison.OrdinalIgnoreCase)
                                  || s.Name.Contains("Board Power", StringComparison.OrdinalIgnoreCase))
                                ?? ReadSensor(hw, SensorType.Power, _ => true) ?? 0f;

                            // Harici GPU aktifse veya ÅŸu ana kadar hiÃ§bir veri alÄ±nmamÄ±ÅŸsa/sÄ±fÄ±rsa kaydet
                            if (hw.HardwareType == HardwareType.GpuNvidia || !isIntegrated || (gpuPower == 0 && gpuTemp == 0))
                            {
                                gpuTemp  = tempVal;
                                gpuLoad  = loadVal;
                                gpuPower = powerVal;
                            }
                        }
                        break;

                    case HardwareType.Memory:
                        if (hw.Name != "Virtual Memory")
                        {
                            float used  = ReadSensor(hw, SensorType.Data, s => s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase)) ?? 0f;
                            float avail = ReadSensor(hw, SensorType.Data, s => s.Name.Contains("Memory Available", StringComparison.OrdinalIgnoreCase)) ?? 0f;
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
            OmenFlow.Core.Services.Logger.LogInfo($"[SensorReader] Okuma hatasÄ±: {ex.Message}");
            return CreateEmpty();
        }
    }

    /// <summary>
    /// WmiBiosMonitor arka plan dÃ¶ngÃ¼sÃ¼ iÃ§in optimize edilmiÅŸ okuma.
    /// Tam Read() ile aynÄ±dÄ±r; fan RPM enjeksiyonu olmaksÄ±zÄ±n Ã§aÄŸrÄ±lÄ±r.
    /// </summary>
    public WorkerTelemetry ReadLightweight() => Read(0, 0);

    // â”€â”€ YardÄ±mcÄ±lar â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

