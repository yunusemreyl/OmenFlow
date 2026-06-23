using System;
using OmenFlow.Core.Models;

namespace OmenFlow.Core.Interfaces;

public interface ITelemetryService
{
    /// <summary>
    /// Starts polling hardware at the given interval and publishes updates.
    /// </summary>
    void Start(TimeSpan interval);
    
    /// <summary>
    /// Stops polling hardware.
    /// </summary>
    void Stop();
    
    /// <summary>
    /// Event fired when telemetry data is updated.
    /// </summary>
    event EventHandler<HardwareTelemetryMessage> TelemetryUpdated;
}
