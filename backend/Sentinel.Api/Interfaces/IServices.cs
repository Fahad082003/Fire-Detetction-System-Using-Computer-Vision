using Sentinel.Api.DTOs;
using Sentinel.Api.Models;

namespace Sentinel.Api.Interfaces;

public interface ICameraService
{
    Task<IReadOnlyList<CameraFeedDto>> GetAllAsync(CancellationToken ct = default, bool revealCredentials = false);
    Task<CameraFeedDto> CreateAsync(SaveCameraRequest request, CancellationToken ct = default);
    Task<CameraFeedDto> UpdateAsync(string id, SaveCameraRequest request, CancellationToken ct = default);
    /// <summary>Incidents keep their history; their camera link becomes empty.</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<CameraFeed> GetEntityAsync(string id, CancellationToken ct = default);
    /// <summary>Saves (or with null clears) the camera ↔ floor-plan calibration.</summary>
    Task<CameraFeedDto> SaveCalibrationAsync(string id, CameraCalibrationDto? calibration, CancellationToken ct = default);
}

public interface ISensorService
{
    Task<IReadOnlyList<SensorNodeDto>> GetAllAsync(CancellationToken ct = default);
    Task<SensorNodeDto> GetAsync(string id, CancellationToken ct = default);
    Task<SensorHistoryDto> GetHistoryAsync(string id, int limit, DateTime? since, CancellationToken ct = default);
    Task<SensorNodeDto> IngestReadingAsync(string id, double value, DateTime? recordedAt, CancellationToken ct = default, bool simulated = false);
    Task<BulkTelemetryResult> IngestBulkAsync(IEnumerable<BulkTelemetryItem> readings, bool simulated = false, CancellationToken ct = default);
}

public interface ISprinklerService
{
    Task<IReadOnlyList<SmartSprinklerDto>> GetAllAsync(CancellationToken ct = default);
    Task<SmartSprinklerDto> ToggleAsync(string id, ToggleSprinklerRequest request, string actor, CancellationToken ct = default);
    Task<MasterSprinklerResult> SetAllAsync(bool isActive, string? zone, string actor, string reason, CancellationToken ct = default);
    /// <summary>Opens every Auto-mode relay in the zone that is currently closed.</summary>
    Task<IReadOnlyList<string>> AutoActivateZoneAsync(string zone, string reason, CancellationToken ct = default);
}

public interface IIncidentService
{
    Task<IReadOnlyList<IncidentAlertDto>> ListAsync(bool? resolved, Severity? minSeverity, int limit, CancellationToken ct = default);
    Task<IncidentAlertDto> GetAsync(int id, CancellationToken ct = default);
    Task<IncidentAlertDto> ResolveAsync(int id, string actor, CancellationToken ct = default);
    Task<IncidentAlertDto> DispatchAsync(ManualDispatchRequest request, string actor, CancellationToken ct = default);
    Task<IReadOnlyList<IncidentLogDto>> ListLogsAsync(int? incidentId, IncidentLogType? type, int limit, CancellationToken ct = default);
}

public interface IDetectionService
{
    /// <summary>Validate → broadcast overlay → run the alert routine. Returns the validated frame and any incident id.</summary>
    Task<(DetectionFrameDto Frame, int? IncidentId)> ProcessAsync(DetectionPayloadDto payload, CancellationToken ct = default);
}

public interface IAlertEngine
{
    Task<IncidentAlert?> EvaluateDetectionAsync(DetectionFrameDto frame, CancellationToken ct = default);
    Task EvaluateSensorAsync(SensorNode sensor, SensorState previousState, bool simulated = false, CancellationToken ct = default);
    /// <summary>A camera that was sending frames went silent (see CameraWatchdog).</summary>
    Task CameraLostAsync(string cameraId, int silentSeconds, CancellationToken ct = default);
    Task PublishIncidentAsync(IncidentAlert incident, string action, bool emergency, bool forceNotify = false,
        CancellationToken ct = default, bool simulated = false);
    /// <summary>Saves a history entry and broadcasts it (SignalR "incidentLog").</summary>
    Task AddLogAsync(IncidentLog log, CancellationToken ct = default);
}

public interface IDashboardService
{
    Task<DashboardSnapshotDto> GetSnapshotAsync(CancellationToken ct = default);
}

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<UserDto> GetUserAsync(int id, CancellationToken ct = default);
}

public interface ITokenService
{
    (string Token, DateTime ExpiresAt) CreateToken(AppUser user);
}

public interface IPushSubscriptionService
{
    string? PublicKey { get; }
    Task SubscribeAsync(PushSubscriptionRequest request, int? userId, string userAgent, CancellationToken ct = default);
    Task UnsubscribeAsync(string endpoint, CancellationToken ct = default);
}

public interface IFloorPlanService
{
    Task<IReadOnlyList<FloorPlanDto>> ListAsync(CancellationToken ct = default);
    Task<FloorPlanDto> GetAsync(int id, CancellationToken ct = default);
    Task<FloorPlanDto> CreateAsync(SaveFloorPlanRequest request, CancellationToken ct = default);
    Task<FloorPlanDto> UpdateAsync(int id, SaveFloorPlanRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<FloorPlanDto> SaveImageAsync(int id, IFormFile file, CancellationToken ct = default);
    Task<(string Path, string ContentType)> GetImageAsync(int id, CancellationToken ct = default);
    Task<RouteResultDto> RouteAsync(int id, RouteRequest request, CancellationToken ct = default);
    /// <summary>Which rooms the fire may reach next and a risk heatmap (rule-based estimate).</summary>
    Task<FireRiskDto> RiskAsync(int id, CancellationToken ct = default);
}

/// <summary>Server → Angular real-time events (SignalR).</summary>
public interface IRealtimeNotifier
{
    Task DetectionAsync(DetectionFrameDto frame);
    Task TelemetryAsync(SensorNodeDto sensor);
    Task SprinklerAsync(SmartSprinklerDto sprinkler);
    Task IncidentAsync(IncidentEventDto evt);
    Task NotificationAsync(NotificationResultDto result);
    Task ActivityAsync(string level, string message, string? zone = null);
    Task IncidentLogAsync(IncidentLogDto log);
}
