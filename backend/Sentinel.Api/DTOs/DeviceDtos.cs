using System.ComponentModel.DataAnnotations;
using Sentinel.Api.Models;

namespace Sentinel.Api.DTOs;

public record CameraFeedDto(
    string Id, string Name, FeedType FeedType, string StreamUrl, StreamFormat StreamFormat,
    CameraStatus Status, string LocationZone, DateTime UpdatedAt);

public record SensorNodeDto(
    string Id, SensorType Type, double CurrentValue, double Threshold, double CriticalThreshold,
    string LocationZone, string Unit, SensorState State, DateTime? LastReadingAt,
    IReadOnlyList<double>? History = null);

public record SensorReadingDto(double Value, DateTime RecordedAt);

public record SensorHistoryDto(string SensorId, string Unit, IReadOnlyList<SensorReadingDto> Readings);

public class SensorReadingRequest
{
    [Range(-100, 100_000)]
    public double Value { get; set; }

    public DateTime? RecordedAt { get; set; }
}

public class BulkTelemetryItem
{
    [Required, MaxLength(64)]
    public string SensorId { get; set; } = string.Empty;

    [Range(-100, 100_000)]
    public double Value { get; set; }
}

public class BulkTelemetryRequest
{
    [Required, MinLength(1), MaxLength(500)]
    public List<BulkTelemetryItem> Readings { get; set; } = [];
}

public record TelemetryError(string SensorId, string Error);

public record BulkTelemetryResult(IReadOnlyList<string> Updated, IReadOnlyList<TelemetryError> Errors);

public record SmartSprinklerDto(
    string Id, string Zone, bool IsActive, SprinklerMode Mode, DateTime? LastTriggeredAt, DateTime UpdatedAt);

/// <summary>Both fields optional. An empty body flips the relay.</summary>
public class ToggleSprinklerRequest
{
    public bool? IsActive { get; set; }
    public SprinklerMode? Mode { get; set; }
}

public class MasterSprinklerRequest
{
    [Required]
    public bool? IsActive { get; set; }

    [MaxLength(64)]
    public string? Zone { get; set; }
}

public record MasterSprinklerResult(IReadOnlyList<string> Changed, IReadOnlyList<SmartSprinklerDto> Sprinklers);
