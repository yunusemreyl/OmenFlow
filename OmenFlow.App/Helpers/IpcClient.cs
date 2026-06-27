using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmenFlow_App.Helpers;

public class TelemetryData
{
    public float CpuTemp { get; set; }
    public float GpuTemp { get; set; }
    public float CpuLoad { get; set; }
    public float GpuLoad { get; set; }
    public float CpuPower { get; set; }
    public float GpuPower { get; set; }
    public float RamUsedGb { get; set; }
    public float RamTotalGb { get; set; }
    public int CpuFanRpm { get; set; }
    public int GpuFanRpm { get; set; }
    public int GpuMode { get; set; }
    public int GpuPowerLevel { get; set; }
    public int ActiveProfile { get; set; }
    public int KeyboardType { get; set; }
    public bool BacklightOn { get; set; }
    public string ZoneColors { get; set; } = "";
    public int ActiveFanMode { get; set; }
}

public class IpcClient
{
    public event EventHandler<TelemetryData>? TelemetryReceived;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private DateTime _lastWorkerStartAttempt = DateTime.MinValue;
    private bool _isConnected = false;

    public void Connect()
    {
        EnsureWorkerRunning();
        _ = Task.Run(() => StartTelemetryLoopAsync(_cts.Token));
    }

    private void EnsureWorkerRunning()
    {
        try
        {
            if (System.Diagnostics.Process.GetProcessesByName("OmenFlow.Worker").Length > 0)
            {
                return; // Already running
            }

            if ((DateTime.Now - _lastWorkerStartAttempt).TotalSeconds < 10)
            {
                return; // Wait at least 10 seconds between attempts
            }

            _lastWorkerStartAttempt = DateTime.Now;

            string baseDir = AppContext.BaseDirectory;
            string[] possiblePaths = new[]
            {
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\OmenFlow.Worker\bin\Debug\net8.0-windows\win-x64\OmenFlow.Worker.exe")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\OmenFlow.Worker\bin\Debug\net8.0-windows\OmenFlow.Worker.exe")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\OmenFlow.Worker\bin\Debug\net8.0-windows\win-x64\OmenFlow.Worker.exe")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\OmenFlow.Worker\bin\Release\net8.0-windows\win-x64\OmenFlow.Worker.exe")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\OmenFlow.Worker\bin\Debug\net8.0-windows\OmenFlow.Worker.exe")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\..\OmenFlow.Worker\bin\Debug\net8.0-windows\win-x64\OmenFlow.Worker.exe")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\..\OmenFlow.Worker\bin\Release\net8.0-windows\win-x64\OmenFlow.Worker.exe")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\..\OmenFlow.Worker\bin\Debug\net8.0-windows\OmenFlow.Worker.exe")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\..\..\OmenFlow.Worker\bin\Debug\net8.0-windows\win-x64\OmenFlow.Worker.exe")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\..\..\OmenFlow.Worker\bin\Release\net8.0-windows\win-x64\OmenFlow.Worker.exe")),
                Path.Combine(baseDir, "OmenFlow.Worker.exe"),
                @"C:\Users\yeyil\Documents\OmenFlow\OmenFlow\OmenFlow.Worker\bin\Debug\net8.0-windows\win-x64\OmenFlow.Worker.exe",
                @"C:\Users\yeyil\Documents\OmenFlow\OmenFlow\OmenFlow.Worker\bin\Release\net8.0-windows\win-x64\OmenFlow.Worker.exe"
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        WorkingDirectory = Path.GetDirectoryName(path) ?? baseDir,
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                    };
                    System.Diagnostics.Process.Start(psi);
                    System.Diagnostics.Debug.WriteLine($"[IpcClient] Started worker from: {path}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IpcClient] Error starting worker: {ex.Message}");
        }
    }

    private async Task StartTelemetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string json = await _httpClient.GetStringAsync("http://localhost:50312/api/telemetry", ct);
                var telemetry = JsonSerializer.Deserialize<TelemetryData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (telemetry != null)
                {
                    if (!_isConnected)
                    {
                        _isConnected = true;
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
                EnsureWorkerRunning();
            }
            await Task.Delay(2000, ct);
        }
    }

    public async Task SendCommandAsync(string command, object? value = null)
    {
        try
        {
            EnsureWorkerRunning();
            
            var payload = new
            {
                Command = command,
                Value = value
            };

            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            using var response = await _httpClient.PostAsync("http://localhost:50312/api/command", content);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IpcClient] Error sending command '{command}': {ex.Message}");
        }
    }

    public void Disconnect()
    {
        _cts.Cancel();
    }
}
