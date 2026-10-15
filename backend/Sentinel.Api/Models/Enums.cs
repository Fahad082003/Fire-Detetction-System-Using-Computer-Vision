namespace Sentinel.Api.Models;

public enum FeedType { CCTV, Drone }

public enum StreamFormat { Simulated, Mjpeg, Hls, Mp4 }

public enum CameraStatus { Online, Degraded, Offline }

public enum SensorType { Smoke, Temperature }

public enum SensorState { Normal, Warning, Alarm, Critical }

public enum SprinklerMode { Auto, Manual }

/// <summary>Ordered: comparisons such as <c>severity &gt;= Severity.High</c> are meaningful.</summary>
public enum Severity { Low = 0, Medium = 1, High = 2, Critical = 3 }

public enum IncidentSource { Vision, Sensor, Manual, CameraLost }

public enum UserRole { Admin, Operator }

public enum DetectionCategory { Human, Fire, Smoke, Danger, Vehicle, Other }

public enum ZoneLevel { Warning, Danger, Critical, Safe }

public enum FloorElementType { Room, Corridor, Wall, Door, Exit, SafeZone, DangerZone }

public enum IncidentLogType { Opened, Escalated, Resolved, FireMeasurement, PersonInDanger, EvacuationRoute }
