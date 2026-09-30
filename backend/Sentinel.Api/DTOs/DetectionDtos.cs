using Sentinel.Api.Models;

namespace Sentinel.Api.DTOs;

/// <summary>
/// One frame of structured inference output from the external AI pipeline.
/// Validated by <see cref="Validators.DetectionPayloadValidator"/> (same rules for REST and SignalR).
/// </summary>
public class DetectionPayloadDto
{
    public string CameraId { get; set; } = string.Empty;
    public DateTime? Timestamp { get; set; }
    public int FrameWidth { get; set; } = 1920;
    public int FrameHeight { get; set; } = 1080;
    /// <summary>true = coordinates are 0..1 fractions of the frame; false = pixels.</summary>
    public bool Normalized { get; set; }
    public List<DetectionDto> Detections { get; set; } = [];
    public List<DangerZoneDto> DangerZones { get; set; } = [];
    public HeatmapDto? Heatmap { get; set; }
    public List<double[]> HumanCoordinates { get; set; } = [];
    public bool? FireDetected { get; set; }
    public Severity? FireSeverity { get; set; }
    public int? HumanCount { get; set; }
    public int? DangerCount { get; set; }
}

public class DetectionDto
{
    public string? Label { get; set; }
    public DetectionCategory Category { get; set; } = DetectionCategory.Other;
    public double Confidence { get; set; } = 1.0;
    /// <summary>[x, y, width, height].</summary>
    public double[] Bbox { get; set; } = [];
    public string? TrackId { get; set; }
    /// <summary>Human standing inside a danger zone.</summary>
    public bool AtRisk { get; set; }
}

public class DangerZoneDto
{
    public string? Label { get; set; }
    public ZoneLevel Level { get; set; } = ZoneLevel.Danger;
    /// <summary>List of [x, y] points (at least 3).</summary>
    public List<double[]> Polygon { get; set; } = [];
}

public class HeatmapDto
{
    public int Rows { get; set; }
    public int Cols { get; set; }
    /// <summary>Row-major intensities in 0..1; length must equal Rows * Cols.</summary>
    public double[] Values { get; set; } = [];
}

/// <summary>A validated frame with derived counts filled in — this is what dashboards receive.</summary>
public record DetectionFrameDto(
    string CameraId, DateTime Timestamp, int FrameWidth, int FrameHeight, bool Normalized,
    IReadOnlyList<DetectionDto> Detections, IReadOnlyList<DangerZoneDto> DangerZones, HeatmapDto? Heatmap,
    IReadOnlyList<double[]> HumanCoordinates, bool FireDetected, Severity? FireSeverity, int HumanCount, int DangerCount);

public class TelemetryMessageDto
{
    public string SensorId { get; set; } = string.Empty;
    public double Value { get; set; }
    public DateTime? RecordedAt { get; set; }
}
