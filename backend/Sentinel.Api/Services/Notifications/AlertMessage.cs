using Sentinel.Api.Models;

namespace Sentinel.Api.Services.Notifications;

/// <summary>Everything a channel needs to render an alert, detached from EF entities.</summary>
public record AlertMessage(
    int IncidentId, Severity Severity, string Zone, string Location, IncidentSource Source, bool FireDetected,
    Severity? FireSeverity, int HumanCount, int DangerCount, string Message, DateTime Timestamp,
    string SiteName, string DashboardUrl)
{
    public string Headline => Source switch
    {
        IncidentSource.Manual => "EMERGENCY DISPATCH",
        _ when FireDetected && DangerCount > 0 => "FIRE — HUMANS IN DANGER",
        _ when FireDetected => "FIRE DETECTED",
        _ when DangerCount > 0 => "HUMANS IN DANGER ZONE",
        IncidentSource.Sensor => "SENSOR SAFETY LIMIT EXCEEDED",
        _ => "INCIDENT",
    };

    public string Subject => $"[{SiteName}] {Severity.ToString().ToUpperInvariant()}: {Headline} — {Zone} (#{IncidentId})";

    /// <summary>Plain-text body shared by SMS and email (SMS is truncated by the channel).</summary>
    public string Text =>
        $"""
        [{SiteName}] {Severity.ToString().ToUpperInvariant()} - {Headline}
        Location: {Location}
        Humans in area: {HumanCount} | Humans in danger: {DangerCount}
        {(FireDetected ? $"Fire severity: {FireSeverity?.ToString() ?? "unknown"}" : "No fire reported")}
        {Message}
        Incident #{IncidentId} at {Timestamp:yyyy-MM-dd HH:mm:ss} UTC
        {DashboardUrl}
        """;

    public static AlertMessage From(IncidentAlert i, string siteName, string dashboardUrl) => new(
        i.Id, i.Severity, i.Zone,
        i.Camera is not null ? $"{i.Zone} ({i.Camera.Name})" : i.SensorId is not null ? $"{i.Zone} (sensor {i.SensorId})" : i.Zone,
        i.Source, i.FireDetected, i.FireSeverity, i.HumanCount, i.DangerCount, i.Message, i.Timestamp, siteName, dashboardUrl);
}

public record ChannelResult(bool Ok, int Delivered, int Failed, string Detail, bool Skipped = false)
{
    public static ChannelResult NotConfigured() => new(false, 0, 0, "not configured", Skipped: true);
}
