namespace Sentinel.Api.Models;

public class SmartSprinkler
{
    /// <summary>Relay identifier, e.g. "spr-b1".</summary>
    public string Id { get; set; } = string.Empty;
    public string Zone { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    /// <summary>Auto relays are opened by the alert engine; Manual relays only by operators.</summary>
    public SprinklerMode Mode { get; set; } = SprinklerMode.Auto;
    public DateTime? LastTriggeredAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
