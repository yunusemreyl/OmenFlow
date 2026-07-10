using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Worker;

/// <summary>
/// Hibrid telemetri kÃ¶prÃ¼sÃ¼. OmenCore mimarisinden ilham alÄ±narak geliÅŸtirildi.
///
/// Okuma kaynaklarÄ± (Ã¶ncelik sÄ±rasÄ±yla):
///   1. SÄ±caklÄ±k + Fan RPM â†’ HP WMI BIOS (doÄŸrudan ACPI, sÃ¼rÃ¼cÃ¼ gerektirmez)
///   2. CPU/GPU YÃ¼k%, CPU Package Power, RAM â†’ LibreHardwareMonitor (LHM)
///
/// Freeze Protection:
///   WMI CPU sÄ±caklÄ±ÄŸÄ± N dÃ¶ngÃ¼ boyunca aynÄ± kalÄ±rsa sensÃ¶r kilitli demektir.
///   Bu durumda LHM'den sÄ±caklÄ±k okunur ve WMI kilitlenme bayraÄŸÄ± set edilir.
///   WMI tekrar farklÄ± deÄŸer dÃ¶nÃ¼nce bayrak kaldÄ±rÄ±lÄ±r.
///
/// Kaynak Optimizasyonu:
///   - Merkezi PeriodicTimer arka planda 2 saniyede bir gÃ¼ncelleme yapar.
///   - TÃ¼m tÃ¼keten servisler (FanCurveHostedService, QuietSafetyMonitor vb.)
///     doÄŸrudan LHM'ye deÄŸil bu sÄ±nÄ±fÄ±n Ã¶nbelleÄŸine baÅŸvurur.
///   - LHM'nin pahalÄ± Computer.Accept() Ã§aÄŸrÄ±sÄ± bu dÃ¶ngÃ¼de tek seferde yapÄ±lÄ±r.
/// </summary>
public sealed class WmiBiosMonitor : IDisposable
{
    // â”€â”€ BaÄŸÄ±mlÄ±lÄ±klar â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private readonly IBiosService _biosService;
    private readonly SensorReader _lhm;
    private readonly IFanControlService? _fanControlService;

    // â”€â”€ Telemetri Ã–nbelleÄŸi â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private volatile WorkerTelemetry? _cached;
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private readonly object _updateLock = new();

    // â”€â”€ Merkezi Arka Plan DÃ¶ngÃ¼sÃ¼ â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private readonly PeriodicTimer _bgTimer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _bgTask;
    private const int BackgroundIntervalMs = 2000;

    // â”€â”€ WMI Freeze Protection â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private int _lastWmiCpuTemp = 0;
    private int _consecutiveIdenticalCpuReads = 0;
    private bool _cpuTempFrozen = false;
    private const int CpuFreezeThreshold = 10;

    // â”€â”€ WMI SÄ±caklÄ±k EriÅŸilebilirlik Takibi â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private int _wmiFailStreak = 0;
    private const int WmiDisableAfterFailures = 5;
    private bool _wmiAvailable = true;

    public WmiBiosMonitor(IBiosService biosService, SensorReader lhm, IFanControlService? fanControlService = null)
    {
        _biosService = biosService;
        _lhm = lhm;
        _fanControlService = fanControlService;

        // BaÅŸlangÄ±Ã§ telemetrisi olarak boÅŸ deÄŸer (0) ile baÅŸla
        _cached = new WorkerTelemetry(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0, 0,
            FanRpmState.Unknown, FanRpmState.Unknown);

        _bgTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(BackgroundIntervalMs));
        _bgTask = Task.Run(BackgroundUpdateLoopAsync);
    }

    // â”€â”€ Ã–nbellekten AnlÄ±k Okuma (Sync) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    /// <summary>
    /// Mevcut en gÃ¼ncel telemetriyi Ã¶nbellekten dÃ¶ndÃ¼rÃ¼r.
    /// WMI/LHM sorgusu yapmaz â€” arka plan dÃ¶ngÃ¼sÃ¼ bunu halleder.
    /// FanCurveHostedService ve QuietSafetyMonitor bu metodu kullanÄ±r.
    /// </summary>
    public WorkerTelemetry Read(int wmiCpuFanRpm = 0, int wmiGpuFanRpm = 0)
    {
        var c = _cached;
        if (c == null) return CreateEmpty();

        // EÄŸer caller WMI'dan taze RPM okuduysa, Ã¶nbellekteki deÄŸeri geÃ§ersiz kÄ±l
        if (wmiCpuFanRpm > 0 || wmiGpuFanRpm > 0)
        {
            return c with
            {
                CpuFanRpm = wmiCpuFanRpm > 0 ? wmiCpuFanRpm : c.CpuFanRpm,
                GpuFanRpm = wmiGpuFanRpm > 0 ? wmiGpuFanRpm : c.GpuFanRpm
            };
        }

        return c;
    }

    // â”€â”€ Merkezi Arka Plan GÃ¼ncelleme DÃ¶ngÃ¼sÃ¼ â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private async Task BackgroundUpdateLoopAsync()
    {
        while (await _bgTimer.WaitForNextTickAsync(_cts.Token))
        {
            try
            {
                var updated = await BuildTelemetryAsync();
                lock (_updateLock)
                {
                    _cached = updated;
                    _lastUpdateUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                OmenFlow.Core.Services.Logger.LogInfo($"[WmiBiosMonitor] Background update error: {ex.Message}");
            }
        }
    }

    private async Task<WorkerTelemetry> BuildTelemetryAsync()
    {
        int cpuTemp = 0, gpuTemp = 0;

        // â”€â”€ Kaynak 1: HP WMI BIOS â€” SÄ±caklÄ±k â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        if (_wmiAvailable)
        {
            try
            {
                var temps = await _biosService.GetBothTemperaturesAsync();
                if (temps.HasValue && (temps.Value.cpuTemp > 0 || temps.Value.gpuTemp > 0))
                {
                    cpuTemp = temps.Value.cpuTemp;
                    gpuTemp = temps.Value.gpuTemp;

                    // Freeze Protection: aynÄ± CPU sÄ±caklÄ±ÄŸÄ± Ã§ok defa gelirse kilitlenme var
                    if (cpuTemp > 0 && cpuTemp == _lastWmiCpuTemp)
                    {
                        _consecutiveIdenticalCpuReads++;
                        if (_consecutiveIdenticalCpuReads > CpuFreezeThreshold && !_cpuTempFrozen)
                        {
                            _cpuTempFrozen = true;
                            OmenFlow.Core.Services.Logger.LogInfo($"[WmiBiosMonitor] âš ï¸ CPU sÄ±caklÄ±ÄŸÄ± {cpuTemp}Â°C'de donmuÅŸ gÃ¶rÃ¼nÃ¼yor â€” LHM yedek devreye alÄ±ndÄ±.");
                        }
                    }
                    else
                    {
                        _consecutiveIdenticalCpuReads = 0;
                        if (_cpuTempFrozen)
                        {
                            _cpuTempFrozen = false;
                            OmenFlow.Core.Services.Logger.LogInfo("[WmiBiosMonitor] âœ“ CPU sÄ±caklÄ±ÄŸÄ± normale dÃ¶ndÃ¼ â€” WMI birincil kaynak tekrar aktif.");
                        }
                    }

                    _lastWmiCpuTemp = cpuTemp;
                    _wmiFailStreak = 0;
                }
                else
                {
                    _wmiFailStreak++;
                    if (_wmiFailStreak >= WmiDisableAfterFailures)
                    {
                        _wmiAvailable = false;
                        OmenFlow.Core.Services.Logger.LogInfo($"[WmiBiosMonitor] âš ï¸ WMI sÄ±caklÄ±k okuma {_wmiFailStreak} kez baÅŸarÄ±sÄ±z â€” LHM'ye geÃ§ildi.");
                    }
                }
            }
            catch (Exception ex)
            {
                _wmiFailStreak++;
                OmenFlow.Core.Services.Logger.LogInfo($"[WmiBiosMonitor] WMI sÄ±caklÄ±k hatasÄ±: {ex.Message}");
            }
        }

        // â”€â”€ Kaynak 2: LibreHardwareMonitor â€” YÃ¼k, GÃ¼Ã§, RAM â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // LHM her zaman CPU/GPU YÃ¼k%, RAM ve GÃ¼Ã§ deÄŸerleri iÃ§in kullanÄ±lÄ±r.

        // Ã–nce fan RPM'lerini fanControlService'ten sorgula
        int cpuFanRpm = 0;
        int gpuFanRpm = 0;
        if (_fanControlService != null)
        {
            try
            {
                var rpms = await _fanControlService.GetFanRpmAsync();
                cpuFanRpm = rpms.CpuFanRpm;
                gpuFanRpm = rpms.GpuFanRpm;
            }
            catch (Exception ex)
            {
                OmenFlow.Core.Services.Logger.LogInfo($"[WmiBiosMonitor] Fan RPM okuma hatasÄ±: {ex.Message}");
            }
        }

        // LHM'den diÄŸer verileri oku (fan RPM'leri ile birlikte durum tespiti yapÄ±lÄ±r)
        var lhm = _lhm.Read(cpuFanRpm, gpuFanRpm);

        // SÄ±caklÄ±k kaynaÄŸÄ±: WMI donmuÅŸsa veya WMI eriÅŸilemezse â†’ LHM
        if (_cpuTempFrozen || !_wmiAvailable || cpuTemp == 0)
            cpuTemp = (int)lhm.CpuTemp;
        if (!_wmiAvailable || gpuTemp == 0)
            gpuTemp = (int)lhm.GpuTemp;

        return new WorkerTelemetry(
            CpuTemp:   cpuTemp,
            GpuTemp:   gpuTemp,
            CpuLoad:   lhm.CpuLoad,
            GpuLoad:   lhm.GpuLoad,
            CpuPower:  lhm.CpuPower,
            GpuPower:  lhm.GpuPower,
            RamUsedGb: lhm.RamUsedGb,
            RamTotalGb:lhm.RamTotalGb,
            CpuFanRpm: lhm.CpuFanRpm,
            GpuFanRpm: lhm.GpuFanRpm,
            CpuFanState: lhm.CpuFanState,
            GpuFanState: lhm.GpuFanState
        );
    }

    private static WorkerTelemetry CreateEmpty() =>
        new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0, 0, FanRpmState.Unknown, FanRpmState.Unknown);

    // â”€â”€ TanÄ±lama â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public DateTime LastUpdateUtc => _lastUpdateUtc;
    public bool IsWmiAvailable => _wmiAvailable;
    public bool IsCpuTempFrozen => _cpuTempFrozen;

    public void Dispose()
    {
        _cts.Cancel();
        _bgTimer.Dispose();
        _cts.Dispose();
    }
}

