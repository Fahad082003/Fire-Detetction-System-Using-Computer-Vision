using Sentinel.Api.Configuration;
using Sentinel.Api.Models;

namespace Sentinel.Api.Services;

/// <summary>Pure threshold maths shared by the alert engine, DTO mapping and tests.</summary>
public static class SensorRules
{
    public static double CriticalThreshold(SensorNode sensor, AlertOptions options) =>
        sensor.Threshold * (sensor.Type == SensorType.Smoke ? options.SmokeCriticalMultiplier : options.TemperatureCriticalMultiplier);

    public static SensorState StateOf(SensorNode sensor, AlertOptions options)
    {
        var value = sensor.CurrentValue;
        if (value >= CriticalThreshold(sensor, options)) return SensorState.Critical;
        if (value >= sensor.Threshold) return SensorState.Alarm;
        if (value >= sensor.Threshold * options.WarningRatio) return SensorState.Warning;
        return SensorState.Normal;
    }
}
