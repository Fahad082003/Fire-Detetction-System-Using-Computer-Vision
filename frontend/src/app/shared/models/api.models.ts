/**
 * TypeScript mirrors of the ASP.NET Core DTOs (backend/Sentinel.Api/DTOs).
 * JSON is camelCase and enums are sent as strings.
 */
export type Severity = 'Low' | 'Medium' | 'High' | 'Critical';
export type FeedType = 'CCTV' | 'Drone';
export type StreamFormat = 'Simulated' | 'Mjpeg' | 'Hls' | 'Mp4';
export type CameraStatus = 'Online' | 'Degraded' | 'Offline';
export type SensorType = 'Smoke' | 'Temperature';
export type SensorState = 'Normal' | 'Warning' | 'Alarm' | 'Critical';
export type SprinklerMode = 'Auto' | 'Manual';
export type IncidentSource = 'Vision' | 'Sensor' | 'Manual' | 'CameraLost';
export type DetectionCategory = 'Human' | 'Fire' | 'Smoke' | 'Danger' | 'Vehicle' | 'Other';
export type ZoneLevel = 'Warning' | 'Danger' | 'Critical' | 'Safe';
export type SystemLevel = 'nominal' | 'elevated' | 'critical';

export interface UserDto { id: number; email: string; displayName: string; role: 'Admin' | 'Operator'; }
export interface LoginResponse { token: string; expiresAt: string; user: UserDto; }

export interface CameraFeed {
  id: string; name: string; feedType: FeedType; streamUrl: string; streamFormat: StreamFormat;
  status: CameraStatus; locationZone: string; updatedAt: string;
  calibration?: CameraCalibration | null;
}
/** The same 4 floor points in the camera frame (pixels, like detection boxes) and on the floor plan (meters). */
export interface CameraCalibration { floorPlanId: number; imagePoints: [number, number][]; planPoints: [number, number][]; }

export interface SensorNode {
  id: string; type: SensorType; currentValue: number; threshold: number; criticalThreshold: number;
  locationZone: string; unit: string; state: SensorState; lastReadingAt: string | null; history?: number[] | null;
}

export interface SmartSprinkler {
  id: string; zone: string; isActive: boolean; mode: SprinklerMode; lastTriggeredAt: string | null; updatedAt: string;
}

export interface IncidentAlert {
  id: number; timestamp: string; updatedAt: string; cameraId: string | null; cameraName: string | null;
  sensorId: string | null; source: IncidentSource; zone: string; severity: Severity; fireDetected: boolean;
  fireSeverity: Severity | null; humanCount: number; dangerCount: number; message: string;
  isResolved: boolean; resolvedAt: string | null;
}

export type IncidentAction = 'created' | 'escalated' | 'updated' | 'resolved';
export interface IncidentEvent { action: IncidentAction; incident: IncidentAlert; }

export interface SystemStatus { level: SystemLevel; openIncidents: number; activeSprinklers: number; }

export interface DashboardSnapshot {
  cameras: CameraFeed[]; sensors: SensorNode[]; sprinklers: SmartSprinkler[];
  incidents: IncidentAlert[]; system: SystemStatus; serverTime: string;
}

export interface Detection {
  label: string | null; category: DetectionCategory; confidence: number;
  bbox: [number, number, number, number]; trackId: string | null; atRisk: boolean;
  /** Set by the backend: danger zone the person's feet are inside. */
  dangerZone?: string | null;
  /** AI producer's evidence: confidence vs threshold, frames seen, box size. */
  explanation?: string | null;
}
export interface DangerZone { label: string | null; level: ZoneLevel; polygon: [number, number][]; }
export interface Heatmap { rows: number; cols: number; values: number[]; }

/** A validated AI frame as broadcast by the hub ("detection" event). */
export interface DetectionFrame {
  cameraId: string; timestamp: string; frameWidth: number; frameHeight: number; normalized: boolean;
  detections: Detection[]; dangerZones: DangerZone[]; heatmap: Heatmap | null; humanCoordinates: [number, number][];
  fireDetected: boolean; fireSeverity: Severity | null; humanCount: number; dangerCount: number;
  /** Producer marked this frame as test data (e.g. the simulator). */
  simulated: boolean;
  smokeDetected: boolean;
  /** Model(s) that produced the frame, e.g. "person:yolox_tiny fire_smoke:missing"; null = producer did not say. */
  model: string | null;
}

export interface AiModelStatus {
  name: string; file: string; license: string | null; categories: string[]; threshold: number; loaded: boolean; error: string | null;
  /** Manifest note, e.g. that a model is a CPU pilot that has not met the acceptance criteria. */
  note?: string | null;
}
export interface AiCameraStatus {
  cameraId: string; kind: 'stream' | 'webcam' | 'file'; source: string; simulated: boolean; state: string;
  error: string | null; framesSent: number; postFailures: number;
}
/** GET /api/ai/status. `service` is the sentinel-ai /health body (null when offline). */
export interface AiStatus {
  configured: boolean; online: boolean; error: string | null;
  service: {
    status: 'ok' | 'degraded' | 'no-model'; version: string; models: AiModelStatus[]; capabilities: string[];
    inference: { count: number; avgMs: number | null; lastMs: number | null }; cameras: AiCameraStatus[];
  } | null;
}
/** POST /api/ai/analyze: real model output for one uploaded test image (never opens an incident). */
export interface AiAnalysis { frame: DetectionFrame; inferenceMs: number; }

export type ActivityLevel = 'info' | 'success' | 'warning' | 'critical' | 'notify';
export interface ActivityEntry { level: ActivityLevel; message: string; zone: string | null; timestamp: string; }

export interface NotificationResult {
  incidentId: number; channel: string; ok: boolean; skipped: boolean;
  delivered: number; failed: number; detail: string; timestamp: string;
}

export interface MasterSprinklerResult { changed: string[]; sprinklers: SmartSprinkler[]; }

export interface ManualDispatchRequest { zone: string; message?: string; severity: Severity; activateSprinklers: boolean; }

/** ASP.NET Core ProblemDetails / ValidationProblemDetails error body. */
export interface ProblemDetails { title?: string; detail?: string; status?: number; errors?: Record<string, string[]>; }

export type IncidentLogType = 'Opened' | 'Escalated' | 'Resolved' | 'FireMeasurement' | 'PersonInDanger' | 'EvacuationRoute';
export interface IncidentLog {
  id: number; incidentId: number | null; type: IncidentLogType; timestamp: string; zone: string; details: string;
  fireAreaPercent: number | null; fireSeverity: Severity | null; humanCount: number | null; dangerCount: number | null;
  personTrackId: string | null; personX: number | null; personY: number | null; dangerZone: string | null; simulated: boolean;
}

export type FloorElementType = 'Room' | 'Corridor' | 'Wall' | 'Door' | 'Exit' | 'SafeZone' | 'DangerZone';
/** Axis-aligned rectangle in plan meters (origin top-left). */
export interface FloorPlanElement {
  id?: number; type: FloorElementType; name: string; x: number; y: number; width: number; height: number;
  isBlocked: boolean; isActive: boolean; zone: string | null;
}
export interface FloorPlan {
  id: number; name: string; building: string; floorNumber: number; widthMeters: number; heightMeters: number;
  hasImage: boolean; updatedAt: string; elements: FloorPlanElement[];
}
export type SaveFloorPlan = Omit<FloorPlan, 'id' | 'hasImage' | 'updatedAt'>;
export interface RouteResult {
  found: boolean; message: string; path: [number, number][]; distanceMeters: number; estimatedSeconds: number;
  walkingSpeedMetersPerSecond: number; exitName: string | null; startInDangerZone: boolean; activeDangerZones: string[];
  /** true = no route keeps the required clearance from active danger zones; the route shown is NOT safe. */
  clearanceViolated: boolean; minDangerGapMeters: number | null; requiredClearanceMeters: number;
}
export interface SensorHistory { sensorId: string; unit: string; readings: { value: number; recordedAt: string }[]; }
