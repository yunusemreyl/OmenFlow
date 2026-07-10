using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow_App.Helpers;

public class TelemetryData
{
    public float CpuTemp     { get; set; }
    public float GpuTemp     { get; set; }
    public float CpuLoad     { get; set; }
    public float GpuLoad     { get; set; }
    public float CpuPower    { get; set; }
    public float GpuPower    { get; set; }
    public float RamUsedGb   { get; set; }
    public float RamTotalGb  { get; set; }
    public int   CpuFanRpm   { get; set; }
    public int   GpuFanRpm   { get; set; }
    public FanRpmState CpuFanState { get; set; } = FanRpmState.Unknown;
    public FanRpmState GpuFanState { get; set; } = FanRpmState.Unknown;
    public int   GpuMode       { get; set; }
    public int   GpuPowerLevel { get; set; }
    public int   ActiveProfile { get; set; }
    public int   KeyboardType  { get; set; }
    public bool  BacklightOn   { get; set; }
    public string ZoneColors   { get; set; } = "";
    public int   ActiveFanMode { get; set; }
    public int   GpuMaxTgp     { get; set; } = 150;
}

public class IpcClient
{
    // â”€â”€ Olaylar â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public event EventHandler<TelemetryData>? TelemetryReceived;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    // â”€â”€ Dahili Durum â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private DateTime _lastWorkerStartAttempt = DateTime.MinValue;
    private bool _isConnected = false;

    // Adaptif poll: baÄŸlantÄ± yokken backoff; baÄŸlÄ±yken normal.
    private int _normalPollMs    = 2000;
    private int _backoffPollMs   = 5000;
    private int _maxBackoffMs    = 30_000;
    private int _currentPollMs   = 2000;
    private int _failStreak      = 0;

    private const string WorkerUrl = "http://localhost:50312";

    // â”€â”€ BaÅŸlatma â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public void Connect()
    {
        EnsureWorkerRunning();
        _ = Task.Run(() => StartTelemetryLoopAsync(_cts.Token));
    }

    // â”€â”€ Worker BaÅŸlatma â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private void EnsureWorkerRunning()
    {
        try
        {
            if (System.Diagnostics.Process.GetProcessesByName("OmenFlow.Worker").Length > 0)
                return;

            if ((DateTime.Now - _lastWorkerStartAttempt).TotalSeconds < 10)
                return;

            _lastWorkerStartAttempt = DateTime.Now;

            string? workerPath = FindWorkerExe();
            if (workerPath == null)
            {
                OmenFlow.Core.Services.Logger.LogInfo("[IpcClient] OmenFlow.Worker.exe bulunamadÄ±.");
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName         = workerPath,
                WorkingDirectory = Path.GetDirectoryName(workerPath) ?? AppContext.BaseDirectory,
                UseShellExecute  = true,
                Verb             = "runas",
                WindowStyle      = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            System.Diagnostics.Process.Start(psi);
            OmenFlow.Core.Services.Logger.LogInfo($"[IpcClient] Worker baÅŸlatÄ±ldÄ±: {workerPath}");
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[IpcClient] Worker baÅŸlatma hatasÄ±: {ex.Message}");
        }
    }

    /// <summary>
    /// OmenFlow.Worker.exe'yi Ã¼Ã§ mantÄ±ksal kategoride arar:
    ///   1. Uygulama ile aynÄ± dizin (Ã¼retim kurulumu)
    ///   2. Debug build Ã§Ä±ktÄ±sÄ± (geliÅŸtirme ortamÄ±)
    ///   3. Program Files kurulum konumu
    /// </summary>
    private string? FindWorkerExe()
    {
        string baseDir = AppContext.BaseDirectory;
        const string exe = "OmenFlow.Worker.exe";
        const string tf  = "net10.0-windows";

        // 1. Ãœretim: aynÄ± dizin
        string local = Path.Combine(baseDir, exe);
        if (File.Exists(local)) return local;

        // 2. Debug build (geliÅŸtirici ortamÄ± â€” Ã§eÅŸitli proje derinliklerine gÃ¶re)
        foreach (int depth in new[] { 4, 5, 6 })
        {
            string relative = Path.GetFullPath(
                Path.Combine(baseDir, string.Concat(Enumerable.Repeat(@"..\", depth)),
                $@"OmenFlow.Worker\bin\Debug\{tf}\win-x64\{exe}"));
            if (File.Exists(relative)) return relative;

            string relativeRelease = Path.GetFullPath(
                Path.Combine(baseDir, string.Concat(Enumerable.Repeat(@"..\", depth)),
                $@"OmenFlow.Worker\bin\Release\{tf}\win-x64\{exe}"));
            if (File.Exists(relativeRelease)) return relativeRelease;
        }

        // 3. Program Files kurulumu
        string programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OmenFlow", exe);
        if (File.Exists(programFiles)) return programFiles;

        return null;
    }

    // â”€â”€ Telemetri DÃ¶ngÃ¼sÃ¼ (Adaptif Poll) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private async Task StartTelemetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string json = await _httpClient.GetStringAsync($"{WorkerUrl}/api/telemetry", ct);
                var telemetry = JsonSerializer.Deserialize<TelemetryData>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (telemetry != null)
                {
                    if (!_isConnected)
                    {
                        _isConnected = true;
                        _failStreak  = 0;
                        _currentPollMs = _normalPollMs;
                        Connected?.Invoke(this, EventArgs.Empty);
                    }
                    TelemetryReceived?.Invoke(this, telemetry);
                }
            }
            catch (Exception)
            {
                if (_isConnected)
                {
                    _isConnected = false;
                    Disconnected?.Invoke(this, EventArgs.Empty);
                }

                // Ãœstel backoff: 5s â†’ 10s â†’ 20s â†’ 30s (max)
                _failStreak++;
                _currentPollMs = Math.Min(_backoffPollMs * (1 << Math.Min(_failStreak - 1, 3)), _maxBackoffMs);

                EnsureWorkerRunning();
            }

            await Task.Delay(_currentPollMs, ct);
        }
    }

    // â”€â”€ Komut GÃ¶nderme â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public async Task<bool> SendCommandAsync(string command, object? value = null)
    {
        try
        {
            EnsureWorkerRunning();
            var json    = JsonSerializer.Serialize(new { Command = command, Value = value });
            using var content  = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"{WorkerUrl}/api/command", content);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[IpcClient] Komut hatasÄ± '{command}': {ex.Message}");
            return false;
        }
    }

    public async Task<string?> SendCommandWithResultAsync(string command, object? value = null)
    {
        try
        {
            EnsureWorkerRunning();
            var json    = JsonSerializer.Serialize(new { Command = command, Value = value });
            using var content  = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"{WorkerUrl}/api/command", content);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[IpcClient] Komut sonuÃ§ hatasÄ± '{command}': {ex.Message}");
            return null;
        }
    }

    public void Disconnect() => _cts.Cancel();
}

