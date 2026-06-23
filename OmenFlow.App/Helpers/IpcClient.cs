using System;
using System.IO;
using System.IO.Pipes;
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
}

public class IpcClient
{
    public event EventHandler<TelemetryData>? TelemetryReceived;

    private readonly CancellationTokenSource _cts = new();

    public void Connect()
    {
        _ = Task.Run(() => StartTelemetryLoopAsync(_cts.Token));
    }

    private async Task StartTelemetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", "OmenFlow_HardwareTelemetry", PipeDirection.In, PipeOptions.Asynchronous);
                await pipeClient.ConnectAsync(ct);

                using var reader = new StreamReader(pipeClient);
                while (!ct.IsCancellationRequested && pipeClient.IsConnected)
                {
                    string? line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        try
                        {
                            var telemetry = JsonSerializer.Deserialize<TelemetryData>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (telemetry != null)
                            {
                                TelemetryReceived?.Invoke(this, telemetry);
                            }
                        }
                        catch
                        {
                            // Ignore parse errors
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Disconnected or connection failed, retry after a short delay
                await Task.Delay(2000, ct);
            }
        }
    }

    public async Task SendCommandAsync(string command, object? value = null)
    {
        try
        {
            using var pipeClient = new NamedPipeClientStream(".", "OmenFlow_HardwareCommand", PipeDirection.Out, PipeOptions.Asynchronous);
            await pipeClient.ConnectAsync(1000); // 1 second timeout

            using var writer = new StreamWriter(pipeClient);
            writer.AutoFlush = true;

            var payload = new
            {
                Command = command,
                Value = value
            };

            string json = JsonSerializer.Serialize(payload);
            await writer.WriteLineAsync(json);
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
