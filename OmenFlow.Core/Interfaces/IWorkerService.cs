using System;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Models;

namespace OmenFlow.Core.Interfaces;

public interface IWorkerService
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    event EventHandler<WorkerTelemetry> TelemetryReceived;
    Task SendCommandAsync(string command, object? value = null);
}
