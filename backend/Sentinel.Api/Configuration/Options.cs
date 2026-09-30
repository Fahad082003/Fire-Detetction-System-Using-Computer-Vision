namespace Sentinel.Api.Configuration;

/// <summary>Alert-engine tuning. Section: "Alerts".</summary>
public class AlertOptions
{
    public const string Section = "Alerts";
    public int CooldownSeconds { get; set; } = 120;
    public int IncidentMergeWindowSeconds { get; set; } = 90;
    public int VisionEvaluationIntervalSeconds { get; set; } = 2;
    public double SmokeCriticalMultiplier { get; set; } = 1.5;
    public double TemperatureCriticalMultiplier { get; set; } = 1.25;
    public double WarningRatio { get; set; } = 0.8;
    public int SensorHistoryPoints { get; set; } = 60;
    public int SensorHistoryRetentionHours { get; set; } = 48;
    public string SiteName { get; set; } = "Sentinel HQ";
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

public static class OptionsExtensions
{
    public static string[] SplitList(this string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
