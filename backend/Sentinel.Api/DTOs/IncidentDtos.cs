using System.ComponentModel.DataAnnotations;
using Sentinel.Api.Models;

namespace Sentinel.Api.DTOs;

public record IncidentAlertDto(
    int Id, DateTime Timestamp, DateTime UpdatedAt, string? CameraId, string? CameraName, string? SensorId,
    IncidentSource Source, string Zone, Severity Severity, bool FireDetected, Severity? FireSeverity,
    int HumanCount, int DangerCount, string Message, bool IsResolved, DateTime? ResolvedAt);

/// <summary>Real-time envelope: what happened to the incident ("created", "escalated", "updated", "resolved").</summary>
public record IncidentEventDto(string Action, IncidentAlertDto Incident);

public class ManualDispatchRequest
{
    [Required, MaxLength(64)]
    public string Zone { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Message { get; set; }

    public Severity Severity { get; set; } = Severity.Critical;

    public bool ActivateSprinklers { get; set; }
}

public record SystemStatusDto(string Level, int OpenIncidents, int ActiveSprinklers);

public record DashboardSnapshotDto(
    IReadOnlyList<CameraFeedDto> Cameras,
    IReadOnlyList<SensorNodeDto> Sensors,
    IReadOnlyList<SmartSprinklerDto> Sprinklers,
    IReadOnlyList<IncidentAlertDto> Incidents,
    SystemStatusDto System,
    DateTime ServerTime);

/// <summary>Human-readable line for the dashboard activity feed.</summary>
public record ActivityDto(string Level, string Message, string? Zone, DateTime Timestamp);

public record NotificationResultDto(
    int IncidentId, string Channel, bool Ok, bool Skipped, int Delivered, int Failed, string Detail, DateTime Timestamp);
