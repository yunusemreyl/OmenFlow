using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Worker;

/// <summary>
/// Hibrid telemetri köprüsü. OmenCore mimarisinden ilham alınarak geliştirildi.
///
/// Okuma kaynakları (öncelik sırasıyla):
///   1. Sıcaklık + Fan RPM → HP WMI BIOS (doğrudan ACPI, sürücü gerektirmez)
///   2. CPU/GPU Yük%, CPU Package Power, RAM → LibreHardwareMonitor (LHM)
///
/// Freeze Protection:
///   WMI CPU sıcaklığı N döngü boyunca aynı kalırsa sensör kilitli demektir.
///   Bu durumda LHM'den sıcaklık okunur ve WMI kilitlenme bayrağı set edilir.
///   WMI tekrar farklı değer dönünce bayrak kaldırılır.
///
/// Kaynak Optimizasyonu:
///   - Merkezi PeriodicTimer arka planda 2 saniyede bir güncelleme yapar.
///   - Tüm tüketen servisler (FanCurveHostedService, QuietSafetyMonitor vb.)
///     doğrudan LHM'ye değil bu sınıfın önbelleğine başvurur.
///   - LHM'nin pahalı Computer.Accept() çağrısı bu döngüde tek seferde yapılır.
/// </summary>
public sealed class WmiBiosMonitor : IDisposable
{
    // ── Bağımlılıklar ──────────────────────────────────────────────────────
    private readonly IBiosService _biosService;
    private readonly SensorReader _lhm;

    // ── Telemetri Önbelleği ────────────────────────────────────────────────
    private volatile WorkerTelemetry? _cached;
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private readonly object _updateLock = new();

    // ── Merkezi Arka Plan Döngüsü ──────────────────────────────────────────
    private readonly PeriodicTimer _bgTimer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _bgTask;
    private const int BackgroundIntervalMs = 2000;

    // ── WMI Freeze Protection ──────────────────────────────────────────────
    private int _lastWmiCpuTemp = 0;
    private int _consecutiveIdenticalCpuReads = 0;
    private bool _cpuTempFrozen = false;
    private const int CpuFreezeThreshold = 10;

    // ── WMI Sıcaklık Erişilebilirlik Takibi ────────────────────────────────
    private int _wmiFailStreak = 0;
    private const int WmiDisableAfterFailures = 5;
    private bool _wmiAvailable = false;

    public WmiBiosMonitor(IBiosService biosService, SensorReader lhm)
    {
        _biosService = biosService;
        _lhm = lhm;

        // Başlangıç telemetrisi olarak boş değer (0) ile başla
        _cached = new WorkerTelemetry(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0, 0,
            FanRpmState.Unknown, FanRpmState.Unknown);

        _bgTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(BackgroundIntervalMs));
        _bgTask = Task.Run(BackgroundUpdateLoopAsync);
    }

    // ── Önbellekten Anlık Okuma (Sync) ────────────────────────────────────
    /// <summary>
    /// Mevcut en güncel telemetriyi önbellekten döndürür.
    /// WMI/LHM sorgusu yapmaz — arka plan döngüsü bunu halleder.
    /// FanCurveHostedService ve QuietSafetyMonitor bu metodu kullanır.
    /// </summary>
    public WorkerTelemetry Read(int wmiCpuFanRpm = 0, int wmiGpuFanRpm = 0)
    {
        var c = _cached;
        if (c == null) return CreateEmpty();

        // Eğer caller WMI'dan taze RPM okuduysa, önbellekteki değeri geçersiz kıl
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

    // ── Merkezi Arka Plan Güncelleme Döngüsü ──────────────────────────────
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
                Console.WriteLine($"[WmiBiosMonitor] Background update error: {ex.Message}");
            }
        }
    }

    private async Task<WorkerTelemetry> BuildTelemetryAsync()
    {
        int cpuTemp = 0, gpuTemp = 0;

        // ── Kaynak 1: HP WMI BIOS — Sıcaklık ─────────────────────────────
        if (_wmiAvailable)
        {
            try
            {
                var temps = await _biosService.GetBothTemperaturesAsync();
                if (temps.HasValue && (temps.Value.cpuTemp > 0 || temps.Value.gpuTemp > 0))
                {
                    cpuTemp = temps.Value.cpuTemp;
                    gpuTemp = temps.Value.gpuTemp;

                    // Freeze Protection: aynı CPU sıcaklığı çok defa gelirse kilitlenme var
                    if (cpuTemp > 0 && cpuTemp == _lastWmiCpuTemp)
                    {
                        _consecutiveIdenticalCpuReads++;
                        if (_consecutiveIdenticalCpuReads > CpuFreezeThreshold && !_cpuTempFrozen)
                        {
                            _cpuTempFrozen = true;
                            Console.WriteLine($"[WmiBiosMonitor] ⚠️ CPU sıcaklığı {cpuTemp}°C'de donmuş görünüyor — LHM yedek devreye alındı.");
                        }
                    }
                    else
                    {
                        _consecutiveIdenticalCpuReads = 0;
                        if (_cpuTempFrozen)
                        {
                            _cpuTempFrozen = false;
                            Console.WriteLine("[WmiBiosMonitor] ✓ CPU sıcaklığı normale döndü — WMI birincil kaynak tekrar aktif.");
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
                        Console.WriteLine($"[WmiBiosMonitor] ⚠️ WMI sıcaklık okuma {_wmiFailStreak} kez başarısız — LHM'ye geçildi.");
                    }
                }
            }
            catch (Exception ex)
            {
                _wmiFailStreak++;
                Console.WriteLine($"[WmiBiosMonitor] WMI sıcaklık hatası: {ex.Message}");
            }
        }

        // ── Kaynak 2: LibreHardwareMonitor — Yük, Güç, RAM ───────────────
        // LHM her zaman CPU Load%, GPU Load%, CPU Power, GPU Power, RAM için
        // kullanılır. Sadece sıcaklık için WMI önceliklidir.
        var lhm = _lhm.ReadLightweight();

        // Sıcaklık kaynağı: WMI donmuşsa veya WMI erişilemezse → LHM
        if (_cpuTempFrozen || !_wmiAvailable || cpuTemp == 0)
            cpuTemp = (int)lhm.CpuTemp;
        if (!_wmiAvailable || gpuTemp == 0)
            gpuTemp = (int)lhm.GpuTemp;

        // Fan RPM'i de LHM önbelleğinden al (WmiBiosMonitor üzerinden RPM okuma
        // genellikle FanControlService üzerinden yapılır ve caller tarafından enjekte edilir)
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

    // ── Tanılama ──────────────────────────────────────────────────────────
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
