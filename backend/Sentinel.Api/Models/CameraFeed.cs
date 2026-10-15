namespace Sentinel.Api.Models;

public class CameraFeed
{
    /// <summary>Stable identifier used by the AI pipeline, e.g. "cam-warehouse".</summary>
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public FeedType FeedType { get; set; } = FeedType.CCTV;
    public string StreamUrl { get; set; } = string.Empty;
    public StreamFormat StreamFormat { get; set; } = StreamFormat.Simulated;
    public CameraStatus Status { get; set; } = CameraStatus.Online;
    public string LocationZone { get; set; } = string.Empty;
    /// <summary>JSON <see cref="DTOs.CameraCalibrationDto"/>: links this camera's frame to a floor plan; null = not calibrated.</summary>
    public string? Calibration { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<IncidentAlert> Incidents { get; set; } = new List<IncidentAlert>();
}
