using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

/// <summary>
/// Background service that continuously evaluates active fan curves against current temperatures.
/// 
/// Inspired by OmenCore's stable fan control approach:
/// - EC write deduplication: prevents hammering EC with identical commands
/// - Watchdog safety: disables EC writes after consecutive failures
/// - Uses max(CPU, GPU) temperature for curve evaluation
/// - When curve evaluates to 0%, restores BIOS auto control instead of writing 0
/// - 3-second polling interval for responsive control
/// </summary>
public class FanCurveHostedService : BackgroundService, IFanCurveService
{
    private readonly IFanControlService _fanControlService;
    private volatile bool _immediateApplyRequested = false;
    private volatile bool _isMaxModeActive = false;
    private FanCurve? _activeCurve;

    public Func<WorkerTelemetry>? TelemetryProvider { get; set; }
    private volatile bool _safetyProtectionEnabled = true;
    private volatile bool _safetyMaxFanActive = false;

    // EC write deduplication — prevents hammering EC with identical commands
    // (OmenCore uses 15s window, we use 10s since our poll is 3s)
    private int _lastWrittenFanPercent = -1;
    private DateTime _lastWriteTime = DateTime.MinValue;
    private const double DeduplicationWindowSeconds = 10.0;

    // Watchdog: disable after consecutive failures to prevent EC overload
    private int _consecutiveFailures = 0;
    private const int MaxFailuresBeforeDisable = 3;
    private DateTime _disabledUntil = DateTime.MinValue;
    private const int DisableCooldownSeconds = 60;

    // Track whether we're in manual or auto mode to avoid unnecessary restore calls
    private bool _isInManualMode = false;

    public FanCurveHostedService(IFanControlService fanControlService)
    {
        _fanControlService = fanControlService;
    }

    public void SetThermalSafetyEnabled(bool enabled)
    {
        _safetyProtectionEnabled = enabled;
        Console.WriteLine($"[FanCurve] Thermal Safety Protection Enabled: {enabled}");
    }

    public Task ApplyCustomCurveAsync(FanCurve? curve)
    {
        Interlocked.Exchange(ref _activeCurve, curve);
        _immediateApplyRequested = true;
        
        if (curve == null && _isInManualMode)
        {
            _isInManualMode = false;
            _lastWrittenFanPercent = -1; // Dedup sıfırla
            Console.WriteLine("[FanCurve] Curve cleared. (Explicit mode change handled by IpcServer)");
        }
        
        return Task.CompletedTask;
    }

    public void SetMaxModeActive(bool isActive)
    {
        _isMaxModeActive = isActive;
        if (isActive) _immediateApplyRequested = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 3 saniyelik döngü — responsive ama EC'yi bunaltmayan denge
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(3000));

        try
        {
            do
            {
                // ===== THERMAL SAFETY HYSTERESIS (95°C -> MAX FAN -> 50°C) =====
                float maxSafeTemp = 0f;
                if (TelemetryProvider != null)
                {
                    var telemetry = TelemetryProvider();
                    maxSafeTemp = Math.Max(telemetry.CpuTemp, telemetry.GpuTemp);
                }
                if (maxSafeTemp == 0)
                {
                    maxSafeTemp = await _fanControlService.GetCpuTemperatureAsync(stoppingToken);
                }

                if (_safetyProtectionEnabled)
                {
                    if (!_safetyMaxFanActive && maxSafeTemp >= 95)
                    {
                        Console.WriteLine($"[ThermalSafety] ⚠ CRITICAL TEMPERATURE DETECTED ({maxSafeTemp}°C >= 95°C)! Activating emergency MAX FAN protection.");
                        _safetyMaxFanActive = true;
                        await _fanControlService.SetMaxFanAsync(true, stoppingToken);
                        continue;
                    }
                    else if (_safetyMaxFanActive)
                    {
                        if (maxSafeTemp <= 50 && maxSafeTemp > 0)
                        {
                            Console.WriteLine($"[ThermalSafety] ✓ Temperature cooled down to safe levels ({maxSafeTemp}°C <= 50°C). Restoring normal fan operation.");
                            _safetyMaxFanActive = false;
                            await _fanControlService.SetMaxFanAsync(false, stoppingToken);
                            // Normal akışa devam etsin
                        }
                        else
                        {
                            Console.WriteLine($"[ThermalSafety] Emergency MAX FAN active. Current Max Temp: {maxSafeTemp}°C (Waiting to drop <= 50°C)");
                            await _fanControlService.SetMaxFanAsync(true, stoppingToken);
                            continue; // Normal eğriyi veya diğer modları atla!
                        }
                    }
                }
                else if (_safetyMaxFanActive)
                {
                    Console.WriteLine("[ThermalSafety] Thermal Safety Protection disabled by user. Cancelling emergency MAX FAN.");
                    _safetyMaxFanActive = false;
                    await _fanControlService.SetMaxFanAsync(false, stoppingToken);
                }

                if (_immediateApplyRequested)
                {
                    _immediateApplyRequested = false;
                    // İmmediatte isteklerde dedup sıfırla, hemen uygulansın
                    _lastWrittenFanPercent = -1;
                }

                var currentCurve = _activeCurve;
                if (currentCurve != null)
                {
                    await ApplyActiveCurveAsync(currentCurve, stoppingToken);
                }
                else if (_isMaxModeActive)
                {
                    await _fanControlService.SetMaxFanAsync(true, stoppingToken);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
        finally
        {
            await RevertToDefaultBiosControlAsync();
        }
    }

    private async Task ApplyActiveCurveAsync(FanCurve curve, CancellationToken cancellationToken)
    {
        // Watchdog: EC yazma geçici olarak devre dışıysa, atla
        if (DateTime.UtcNow < _disabledUntil)
        {
            Console.WriteLine($"[FanCurve] EC writes disabled by watchdog until {_disabledUntil:HH:mm:ss}");
            return;
        }

        // Hardware WMI readback is the authoritative source; telemetry is used to widen
        // the control signal when the GPU is hotter than the CPU.
        int cpuTempC = await _fanControlService.GetCpuTemperatureAsync(cancellationToken);
        int maxTemp = cpuTempC;

        var telemetry = TelemetryProvider?.Invoke();
        if (telemetry != null)
        {
            maxTemp = Math.Max(maxTemp, (int)Math.Round(telemetry.CpuTemp));
            maxTemp = Math.Max(maxTemp, (int)Math.Round(telemetry.GpuTemp));
        }

        // Sentinel (0) döndüyse atla
        if (maxTemp == 0) return;

        byte targetFanLevel = CalculateTargetSpeed(curve, maxTemp);

        // ===== EC WRITE DEDUPLICATION =====
        // OmenCore'dan ilham: aynı değeri kısa sürede tekrar yazmayı engelle
        // EC'yi (Embedded Controller) bunaltmak ACPI timeout → sahte batarya kritik → sistem kapanma'ya neden olur
        var now = DateTime.UtcNow;
        if (targetFanLevel == _lastWrittenFanPercent &&
            (now - _lastWriteTime).TotalSeconds < DeduplicationWindowSeconds)
        {
            // Aynı değer yakın zamanda yazıldı, atla
            return;
        }

        // ===== FAN %0 → BIOS AUTO RESTORE =====
        // Eğri %0 diyorsa, fanı manuel olarak durdurmak yerine BIOS'un kendi kontrolüne bırak
        if (targetFanLevel == 0)
        {
            if (_isInManualMode)
            {
                Console.WriteLine($"[FanCurve] Temp={maxTemp}°C → Fan=0% → Restoring BIOS auto control");
                await _fanControlService.RestoreAutoControlAsync(cancellationToken);
                _isInManualMode = false;
                _lastWrittenFanPercent = 0;
                _lastWriteTime = now;
            }
            return;
        }

        // ===== APPLY FAN LEVEL =====
        try
        {
            Console.WriteLine($"[FanCurve] Temp={maxTemp}°C → Fan={targetFanLevel}%");
            await _fanControlService.SetFanLevelAsync(targetFanLevel, cancellationToken);
            _isInManualMode = true;
            _lastWrittenFanPercent = targetFanLevel;
            _lastWriteTime = now;
            _consecutiveFailures = 0; // Başarılı yazma, watchdog sıfırla
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            Console.WriteLine($"[FanCurve] EC write failed (count={_consecutiveFailures}): {ex.Message}");

            if (_consecutiveFailures >= MaxFailuresBeforeDisable)
            {
                _disabledUntil = DateTime.UtcNow.AddSeconds(DisableCooldownSeconds);
                Console.WriteLine($"[FanCurve] ⚠ EC writes disabled for {DisableCooldownSeconds}s after {_consecutiveFailures} consecutive failures");
            }
        }
    }

    /// <summary>
    /// Linear interpolation ile sıcaklığa göre fan hızı hesapla.
    /// OmenCore'un EvaluateCurve mantığıyla aynı.
    /// </summary>
    private byte CalculateTargetSpeed(FanCurve curve, int currentTemp)
    {
        var points = curve.Points;
        if (points == null || points.Count == 0) return 0;

        // Sıralama güvenliği
        var sorted = points.OrderBy(p => p.TemperatureCelsius).ToList();

        // En düşük noktanın altında
        if (currentTemp <= sorted[0].TemperatureCelsius)
            return (byte)sorted[0].FanSpeedPercent;

        // En yüksek noktanın üstünde
        if (currentTemp >= sorted[^1].TemperatureCelsius)
            return (byte)sorted[^1].FanSpeedPercent;

        // İki nokta arasında lineer interpolasyon
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var p1 = sorted[i];
            var p2 = sorted[i + 1];

            if (currentTemp >= p1.TemperatureCelsius && currentTemp <= p2.TemperatureCelsius)
            {
                double tempRange = p2.TemperatureCelsius - p1.TemperatureCelsius;
                
                // Division by zero koruması (OmenCore: if (Math.Abs(t2-t1) < 0.1) return p1)
                if (tempRange < 1) return (byte)p1.FanSpeedPercent;
                
                double speedRange = p2.FanSpeedPercent - p1.FanSpeedPercent;
                double tempOffset = currentTemp - p1.TemperatureCelsius;
                double ratio = tempOffset / tempRange;
                double interpolatedSpeed = p1.FanSpeedPercent + (ratio * speedRange);
                
                return (byte)Math.Clamp((int)Math.Round(interpolatedSpeed), 0, 100);
            }
        }

        return (byte)sorted[^1].FanSpeedPercent;
    }

    private Task RevertToDefaultBiosControlAsync()
    {
        if (_isInManualMode)
        {
            Console.WriteLine("[FanCurve] Service stopping → restoring BIOS auto control");
            return _fanControlService.RestoreAutoControlAsync();
        }
        return Task.CompletedTask;
    }
}
