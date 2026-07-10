using OmenFlow.Core.Models;

namespace OmenFlow_App.Helpers;

public static class TelemetryDisplayHelper
{
    public static string FormatFanRpm(int rpm, FanRpmState state)
    {
        if (state == FanRpmState.Stable || state == FanRpmState.TransitionHold)
        {
            int roundedRpm = (int)(System.Math.Round(rpm / 100.0) * 100);
            return $"{roundedRpm} RPM";
        }

        return state switch
        {
            FanRpmState.Unavailable => "RPM kullanılamıyor",
            FanRpmState.Unknown => "-- RPM",
            FanRpmState.IdleStopped => "0 RPM",
            _ => $"{rpm} RPM"
        };
    }
}