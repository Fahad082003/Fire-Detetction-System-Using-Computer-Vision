using Sentinel.Api.Configuration;
using Sentinel.Api.Models;
using Sentinel.Api.Services;

namespace Sentinel.Api.DTOs;

/// <summary>Entity → DTO conversions. Entities never leave the API directly.</summary>
public static class Mapping
{
    public static string MaskCredentials(string url) =>
        System.Text.RegularExpressions.Regex.Replace(url, "(?<=://)[^/@]+@", "***@");

    /// <summary>Stream credentials (rtsp://user:pass@…) are masked unless the caller is the AI service / a device.</summary>
    public static CameraFeedDto ToDto(this CameraFeed c, bool revealCredentials = false) =>
        new(c.Id, c.Name, c.FeedType, revealCredentials ? c.StreamUrl : MaskCredentials(c.StreamUrl), c.StreamFormat, c.Status, c.LocationZone, c.UpdatedAt,
            c.Calibration is null ? null : System.Text.Json.JsonSerializer.Deserialize<CameraCalibrationDto>(c.Calibration));

    public static SensorNodeDto ToDto(this SensorNode s, AlertOptions options, IReadOnlyList<double>? history = null) =>
        new(s.Id, s.Type, s.CurrentValue, s.Threshold, Math.Round(SensorRules.CriticalThreshold(s, options), 2),
            s.LocationZone, s.Unit, SensorRules.StateOf(s, options), s.LastReadingAt, history);

    public static SmartSprinklerDto ToDto(this SmartSprinkler s) =>
        new(s.Id, s.Zone, s.IsActive, s.Mode, s.LastTriggeredAt, s.UpdatedAt);

    public static IncidentAlertDto ToDto(this IncidentAlert i) =>
        new(i.Id, i.Timestamp, i.UpdatedAt, i.CameraId, i.Camera?.Name, i.SensorId, i.Source, i.Zone, i.Severity,
            i.FireDetected, i.FireSeverity, i.HumanCount, i.DangerCount, i.Message, i.IsResolved, i.ResolvedAt);

    public static UserDto ToDto(this AppUser u) => new(u.Id, u.Email, u.DisplayName, u.Role);

    public static IncidentLogDto ToDto(this IncidentLog l) =>
        new(l.Id, l.IncidentId, l.Type, l.Timestamp, l.Zone, l.Details, l.FireAreaPercent, l.FireSeverity, l.HumanCount,
            l.DangerCount, l.PersonTrackId, l.PersonX, l.PersonY, l.DangerZone, l.Simulated);

    public static FloorPlanDto ToDto(this FloorPlan p) =>
        new(p.Id, p.Name, p.Building, p.FloorNumber, p.WidthMeters, p.HeightMeters, p.ImageFileName is not null, p.UpdatedAt,
            p.Elements.OrderBy(e => e.Id).Select(e => new FloorPlanElementDto
            {
                Id = e.Id, Type = e.Type, Name = e.Name, X = e.X, Y = e.Y, Width = e.Width, Height = e.Height,
                IsBlocked = e.IsBlocked, IsActive = e.IsActive, Zone = e.Zone,
            }).ToList());
}
