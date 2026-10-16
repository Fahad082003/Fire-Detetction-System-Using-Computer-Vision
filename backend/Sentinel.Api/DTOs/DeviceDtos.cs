using System.ComponentModel.DataAnnotations;
using Sentinel.Api.Models;

namespace Sentinel.Api.DTOs;

public record CameraFeedDto(
    string Id, string Name, FeedType FeedType, string StreamUrl, StreamFormat StreamFormat,
    CameraStatus Status, string LocationZone, DateTime UpdatedAt, CameraCalibrationDto? Calibration = null);

/// <summary>
/// Camera ↔ floor-plan link (homography): the same 4 floor points seen in the camera frame (same units as the
/// detection boxes, i.e. frame pixels) and on the plan (meters). The dashboard uses it to place detected people on the plan.
/// </summary>
public record CameraCalibrationDto(int FloorPlanId, double[][] ImagePoints, double[][] PlanPoints);

/// <summary>Admin: add or edit a camera. Id only on create (it is what the AI service posts as cameraId).</summary>
public class SaveCameraRequest
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public FeedType FeedType { get; set; } = FeedType.CCTV;
    /// <summary>rtsp:// / http(s):// link, or empty for a simulated feed. A masked link (rtsp://***@…) keeps the saved one.</summary>
    public string? StreamUrl { get; set; }
    public StreamFormat StreamFormat { get; set; } = StreamFormat.Simulated;
    public string LocationZone { get; set; } = string.Empty;
}

public record SensorNodeDto(
    string Id, SensorType Type, double CurrentValue, double Threshold, double CriticalThreshold,
    string LocationZone, string Unit, SensorState State, DateTime? LastReadingAt,
    IReadOnlyList<double>? History = null);

public record SensorReadingDto(double Value, DateTime RecordedAt);

public record SensorHistoryDto(string SensorId, string Unit, IReadOnlyList<SensorReadingDto> Readings);

public class SensorReadingRequest
{
    public double Value { get; set; }

    public DateTime? RecordedAt { get; set; }
}

public class BulkTelemetryItem
{
    [Required, MaxLength(64)]
    public string SensorId { get; set; } = string.Empty;

    public double Value { get; set; }
}

public class BulkTelemetryRequest
{
    [Required, MinLength(1), MaxLength(500)]
    public List<BulkTelemetryItem> Readings { get; set; } = [];

    /// <summary>Test data (e.g. the simulator): labelled SIMULATED and never opens sprinklers by itself.</summary>
    public bool Simulated { get; set; }
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
