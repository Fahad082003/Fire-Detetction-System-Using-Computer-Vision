namespace Sentinel.Api.Models;

/// <summary>History entry: incident lifecycle, fire measurements, person-in-danger alerts and evacuation route estimates.</summary>
public class IncidentLog
{
    public long Id { get; set; }
    public int? IncidentId { get; set; }
    public IncidentAlert? Incident { get; set; }
    public IncidentLogType Type { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Zone { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;

    /// <summary>Area of the AI fire bounding boxes as % of the camera frame (not a physical fire size).</summary>
    public double? FireAreaPercent { get; set; }
    public Severity? FireSeverity { get; set; }
    public int? HumanCount { get; set; }
    public int? DangerCount { get; set; }

    public string? PersonTrackId { get; set; }
    /// <summary>Person foot point in camera-frame coordinates (pixels, or 0..1 when the frame was normalized).</summary>
    public double? PersonX { get; set; }
    public double? PersonY { get; set; }
    public string? DangerZone { get; set; }

    /// <summary>True when the producer marked its data as simulated (e.g. Sentinel.Simulator).</summary>
    public bool Simulated { get; set; }
}
