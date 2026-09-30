namespace Sentinel.Api.Hubs;

/// <summary>Names of server → client SignalR events. The Angular SignalRService listens for these exact strings.</summary>
public static class HubEvents
{
    public const string Snapshot = "snapshot";
    public const string Detection = "detection";
    public const string Telemetry = "telemetry";
    public const string Sprinkler = "sprinkler";
    public const string Incident = "incident";
    public const string Notification = "notification";
    public const string Activity = "activity";
}
