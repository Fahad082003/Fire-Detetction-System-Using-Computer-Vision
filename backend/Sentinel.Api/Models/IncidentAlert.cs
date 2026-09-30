namespace Sentinel.Api.Models;

public class IncidentAlert
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? CameraId { get; set; }
    public CameraFeed? Camera { get; set; }
    public string? SensorId { get; set; }
    public SensorNode? Sensor { get; set; }

    public IncidentSource Source { get; set; } = IncidentSource.Vision;
    public string Zone { get; set; } = string.Empty;

    /// <summary>Only ever escalates while the incident is open, so this is also the peak severity.</summary>
    public Severity Severity { get; set; } = Severity.Low;
    public bool FireDetected { get; set; }
    public Severity? FireSeverity { get; set; }
    public int HumanCount { get; set; }
    public int DangerCount { get; set; }
    public string Message { get; set; } = string.Empty;

    public bool IsResolved { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
