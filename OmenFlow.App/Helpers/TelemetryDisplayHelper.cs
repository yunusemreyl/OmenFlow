using OmenFlow.Core.Models;

namespace OmenFlow_App.Helpers;

public static class TelemetryDisplayHelper
{
    public static string FormatFanRpm(int rpm, FanRpmState state)
    {
        return state switch
        {
            FanRpmState.Unavailable => "RPM kullanılamıyor",
            FanRpmState.Unknown => "-- RPM",
            FanRpmState.IdleStopped => "0 RPM",
            _ => $"{rpm} RPM"
        };
    }
}