namespace Sentinel.Api.Configuration;

/// <summary>Alert-engine tuning. Section: "Alerts".</summary>
public class AlertOptions
{
    public const string Section = "Alerts";
    public int CooldownSeconds { get; set; } = 120;
    public int IncidentMergeWindowSeconds { get; set; } = 90;
    /// <summary>A camera that sent frames and then stays silent this long raises a "camera lost" alert.</summary>
    public int CameraLostSeconds { get; set; } = 10;
    public int VisionEvaluationIntervalSeconds { get; set; } = 2;
    /// <summary>Let simulated/test AI frames (e.g. recorded video) switch sprinkler relays. Off: test input never actuates equipment.</summary>
    public bool AutoSprinklerOnSimulatedData { get; set; }
    public double SmokeCriticalMultiplier { get; set; } = 1.5;
    public double TemperatureCriticalMultiplier { get; set; } = 1.25;
    public double WarningRatio { get; set; } = 0.8;
    public int SensorHistoryPoints { get; set; } = 60;
    public int SensorHistoryRetentionHours { get; set; } = 48;
    public string SiteName { get; set; } = "FireVision AI";
    public string DashboardUrl { get; set; } = "http://localhost:4200/";
}

/// <summary>Shared secret for AI pipelines / IoT gateways. Section: "Device".</summary>
public class DeviceOptions
{
    public const string Section = "Device";
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>Section: "Jwt".</summary>
public class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "Sentinel.Api";
    public string Audience { get; set; } = "Sentinel.Web";
    public string SigningKey { get; set; } = string.Empty;
    public int ExpiryMinutes { get; set; } = 480;
}

/// <summary>First-run data. Section: "Seed".</summary>
public class SeedOptions
{
    public const string Section = "Seed";
    public bool DemoData { get; set; }
    public string AdminEmail { get; set; } = "admin@sentinel.local";
    public string AdminPassword { get; set; } = string.Empty;
}

/// <summary>Section: "Notifications".</summary>
public class NotificationOptions
{
    public const string Section = "Notifications";
    public SmsOptions Sms { get; set; } = new();
    public EmailOptions Email { get; set; } = new();
    public WebPushOptions WebPush { get; set; } = new();
}

public class SmsOptions
{
    public string TwilioAccountSid { get; set; } = string.Empty;
    public string TwilioAuthToken { get; set; } = string.Empty;
    public string FromNumber { get; set; } = string.Empty;
    /// <summary>Comma-separated E.164 numbers, e.g. "+923001234567,+923331234567".</summary>
    public string Recipients { get; set; } = string.Empty;
}

public class EmailOptions
{
    /// <summary>Empty host = "console mode": emails are written to the application log.</summary>
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string From { get; set; } = "alerts@sentinel.local";
    /// <summary>Comma-separated addresses.</summary>
    public string Recipients { get; set; } = string.Empty;
}

public class WebPushOptions
{
    public string PublicKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public string Subject { get; set; } = "mailto:ops@sentinel.local";
}

/// <summary>The sentinel-ai inference service. Section: "AiService".</summary>
public class AiServiceOptions
{
    public const string Section = "AiService";
    /// <summary>Base URL of the sentinel-ai inference service; empty = AI service not configured.</summary>
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>Upper bound for one inference call (CPU inference on old hardware can take several seconds).</summary>
    public int TimeoutSeconds { get; set; } = 20;
    public long MaxImageBytes { get; set; } = 5 * 1024 * 1024;
}

/// <summary>Floor plans and evacuation estimates. Section: "FloorPlans".</summary>
public class FloorPlanOptions
{
    public const string Section = "FloorPlans";
    /// <summary>Assumed average walking speed for evacuation estimates (1.2 m/s = normal adult walk).</summary>
    public double WalkingSpeedMetersPerSecond { get; set; } = 1.2;
    /// <summary>Minimum distance (m) a route should keep from active danger zones; closer routes are flagged as not safe.</summary>
    public double DangerClearanceMeters { get; set; } = 1.0;
    /// <summary>Uploaded floor-plan images, relative to the API content root (kept out of wwwroot).</summary>
    public string ImageDirectory { get; set; } = "App_Data/floorplans";
    public long MaxImageBytes { get; set; } = 5 * 1024 * 1024;
}

public static class OptionsExtensions
{
    public static string[] SplitList(this string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
