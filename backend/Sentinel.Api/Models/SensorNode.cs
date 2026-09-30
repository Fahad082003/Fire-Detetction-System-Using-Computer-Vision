namespace Sentinel.Api.Models;

public class SensorNode
{
    /// <summary>Stable identifier used by IoT gateways, e.g. "temp-b1".</summary>
    public string Id { get; set; } = string.Empty;
    public SensorType Type { get; set; }
    public double CurrentValue { get; set; }
    public double Threshold { get; set; }
    public string LocationZone { get; set; } = string.Empty;
    public DateTime? LastReadingAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SensorReading> Readings { get; set; } = new List<SensorReading>();
    public ICollection<IncidentAlert> Incidents { get; set; } = new List<IncidentAlert>();

    public string Unit => Type == SensorType.Smoke ? "ppm" : "°C";
}

public class SensorReading
{
    public long Id { get; set; }
    public string SensorId { get; set; } = string.Empty;
    public SensorNode? Sensor { get; set; }
    public double Value { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
