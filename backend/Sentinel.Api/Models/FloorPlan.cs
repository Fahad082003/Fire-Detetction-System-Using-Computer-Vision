namespace Sentinel.Api.Models;

/// <summary>One building floor. Coordinates of all elements are in meters, origin top-left.</summary>
public class FloorPlan
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Building { get; set; } = string.Empty;
    public int FloorNumber { get; set; }
    public double WidthMeters { get; set; }
    public double HeightMeters { get; set; }
    /// <summary>Uploaded PNG/JPG reference image (file name only; stored outside wwwroot).</summary>
    public string? ImageFileName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<FloorPlanElement> Elements { get; set; } = new List<FloorPlanElement>();
}

/// <summary>An axis-aligned rectangle on a floor plan: room, corridor, wall, door, exit, safe zone or danger zone.</summary>
public class FloorPlanElement
{
    public int Id { get; set; }
    public int FloorPlanId { get; set; }
    public FloorPlan? FloorPlan { get; set; }
    public FloorElementType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    /// <summary>Doors/exits: closed or jammed, so routes may not pass.</summary>
    public bool IsBlocked { get; set; }
    /// <summary>Danger zones: manually marked as currently dangerous.</summary>
    public bool IsActive { get; set; }
    /// <summary>Danger zones: camera/sensor zone name (e.g. "Ground Floor - Warehouse"); an open incident there makes it active.</summary>
    public string? Zone { get; set; }
}
