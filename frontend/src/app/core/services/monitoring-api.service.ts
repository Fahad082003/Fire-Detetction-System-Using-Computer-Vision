import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  FloorPlan, IncidentAlert, IncidentLog, IncidentLogType, ManualDispatchRequest, MasterSprinklerResult, RouteResult,
  SaveFloorPlan, SensorHistory, SmartSprinkler, SprinklerMode,
} from '../../shared/models/api.models';

/** Every REST call the dashboard makes. Components never use HttpClient directly. */
@Injectable({ providedIn: 'root' })
export class MonitoringApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBaseUrl}/api`;

  // ---- sprinklers
  toggleSprinkler(id: string, body: { isActive?: boolean; mode?: SprinklerMode } = {}): Observable<SmartSprinkler> {
    return this.http.post<SmartSprinkler>(`${this.base}/iot/sprinklers/${encodeURIComponent(id)}/toggle`, body);
  }

  masterSprinklers(isActive: boolean, zone?: string): Observable<MasterSprinklerResult> {
    return this.http.post<MasterSprinklerResult>(`${this.base}/iot/sprinklers/master`, zone ? { isActive, zone } : { isActive });
  }

  // ---- incidents (the list itself arrives over SignalR)
  resolveIncident(id: number): Observable<IncidentAlert> {
    return this.http.post<IncidentAlert>(`${this.base}/incidents/${id}/resolve`, {});
  }

  dispatch(request: ManualDispatchRequest): Observable<IncidentAlert> {
    return this.http.post<IncidentAlert>(`${this.base}/incidents/dispatch`, request);
  }

  /** History log, newest first (requirement 5.3). */
  incidentLogs(incidentId?: number, type?: IncidentLogType): Observable<IncidentLog[]> {
    const q = new URLSearchParams({ limit: '500' });
    if (incidentId) q.set('incidentId', String(incidentId));
    if (type) q.set('type', type);
    return this.http.get<IncidentLog[]>(`${this.base}/incidents/logs?${q}`);
  }

  sensorHistory(sensorId: string, since: string): Observable<SensorHistory> {
    return this.http.get<SensorHistory>(`${this.base}/iot/sensors/${encodeURIComponent(sensorId)}/history?limit=500&since=${encodeURIComponent(since)}`);
  }

  // ---- floor plans
  floorPlans(): Observable<FloorPlan[]> {
    return this.http.get<FloorPlan[]>(`${this.base}/floorplans`);
  }

  createFloorPlan(plan: SaveFloorPlan): Observable<FloorPlan> {
    return this.http.post<FloorPlan>(`${this.base}/floorplans`, plan);
  }

  updateFloorPlan(id: number, plan: SaveFloorPlan): Observable<FloorPlan> {
    return this.http.put<FloorPlan>(`${this.base}/floorplans/${id}`, plan);
  }

  deleteFloorPlan(id: number): Observable<unknown> {
    return this.http.delete(`${this.base}/floorplans/${id}`);
  }

  uploadFloorPlanImage(id: number, file: File): Observable<FloorPlan> {
    const form = new FormData();
    form.append('file', file);
    return this.http.put<FloorPlan>(`${this.base}/floorplans/${id}/image`, form);
  }

  /** Fetched as a blob so the JWT interceptor applies (an <img src> could not send it). */
  floorPlanImage(id: number): Observable<Blob> {
    return this.http.get(`${this.base}/floorplans/${id}/image`, { responseType: 'blob' });
  }

  route(id: number, x: number, y: number): Observable<RouteResult> {
    return this.http.post<RouteResult>(`${this.base}/floorplans/${id}/route`, { x, y });
  }

  // ---- web push
  vapidPublicKey(): Observable<{ publicKey: string | null }> {
    return this.http.get<{ publicKey: string | null }>(`${this.base}/push/vapid-public-key`);
  }

  subscribePush(subscription: PushSubscriptionJSON): Observable<unknown> {
    return this.http.post(`${this.base}/push/subscribe`, subscription);
  }

  unsubscribePush(endpoint: string): Observable<unknown> {
    return this.http.post(`${this.base}/push/unsubscribe`, { endpoint });
  }

  /** MJPEG relay URL. <img> cannot send headers, so the JWT goes in the query string. */
  cameraStreamUrl(cameraId: string, token: string | null): string {
    return `${this.base}/cameras/${encodeURIComponent(cameraId)}/stream${token ? `?access_token=${encodeURIComponent(token)}` : ''}`;
  }
}
