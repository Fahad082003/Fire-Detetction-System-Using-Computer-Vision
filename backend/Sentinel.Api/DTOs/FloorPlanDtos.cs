using System.ComponentModel.DataAnnotations;
using Sentinel.Api.Models;

namespace Sentinel.Api.DTOs;

public class FloorPlanElementDto
{
    public int Id { get; set; }
    public FloorElementType Type { get; set; }
    [MaxLength(80)] public string Name { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsBlocked { get; set; }
    public bool IsActive { get; set; }
    [MaxLength(64)] public string? Zone { get; set; }
}

/// <summary>Create/update body. Elements replace the plan's current elements.</summary>
public class SaveFloorPlanRequest
{
    [Required, MaxLength(120)] public string Name { get; set; } = string.Empty;
    [MaxLength(120)] public string? Building { get; set; }
    [Range(-10, 200)] public int FloorNumber { get; set; }
    [Range(1, 1000)] public double WidthMeters { get; set; }
    [Range(1, 1000)] public double HeightMeters { get; set; }
    [MaxLength(2000)] public List<FloorPlanElementDto> Elements { get; set; } = [];
}

public record FloorPlanDto(
    int Id, string Name, string Building, int FloorNumber, double WidthMeters, double HeightMeters,
    bool HasImage, DateTime UpdatedAt, IReadOnlyList<FloorPlanElementDto> Elements);

/// <summary>Known start point of a person, in plan meters.</summary>
public class RouteRequest
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>Estimated route to the nearest reachable exit. EstimatedSeconds = DistanceMeters / WalkingSpeedMetersPerSecond.</summary>
/// <summary>Rule-based fire-risk estimate for one floor (see FireRiskEstimator). Cells: -1 outside, 0 none, 1 low, 2 medium, 3 high, 4 burning.</summary>
public record FireRiskDto(bool Active, string Message, int Cols, int Rows, double CellMeters, int[] Cells,
    IReadOnlyList<AreaRiskDto> Areas, IReadOnlyList<string> ActiveDangerZones);

/// <summary>Risk of one room or corridor: Burning, High, Medium, Low or None, and its distance from the fire through doors.</summary>
public record AreaRiskDto(string Name, FloorElementType Type, string Level, double? DistanceMeters);

public record RouteResultDto(
    bool Found, string Message, IReadOnlyList<double[]> Path, double DistanceMeters, double EstimatedSeconds,
    double WalkingSpeedMetersPerSecond, string? ExitName, bool StartInDangerZone, IReadOnlyList<string> ActiveDangerZones,
    bool ClearanceViolated, double? MinDangerGapMeters, double RequiredClearanceMeters);

public record IncidentLogDto(
    long Id, int? IncidentId, IncidentLogType Type, DateTime Timestamp, string Zone, string Details,
    double? FireAreaPercent, Severity? FireSeverity, int? HumanCount, int? DangerCount,
    string? PersonTrackId, double? PersonX, double? PersonY, string? DangerZone, bool Simulated);
