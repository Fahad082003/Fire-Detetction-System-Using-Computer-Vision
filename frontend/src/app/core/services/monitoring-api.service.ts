import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  DashboardSnapshot, IncidentAlert, ManualDispatchRequest, MasterSprinklerResult, SensorNode, SmartSprinkler, SprinklerMode,
} from '../../shared/models/api.models';

/** Every REST call the dashboard makes. Components never use HttpClient directly. */
@Injectable({ providedIn: 'root' })
export class MonitoringApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBaseUrl}/api`;

  snapshot(): Observable<DashboardSnapshot> {
    return this.http.get<DashboardSnapshot>(`${this.base}/dashboard/snapshot`);
  }

  // ---- sensors / IoT
  postReading(sensorId: string, value: number): Observable<SensorNode> {
    return this.http.post<SensorNode>(`${this.base}/iot/sensors/${encodeURIComponent(sensorId)}/readings`, { value });
  }

  toggleSprinkler(id: string, body: { isActive?: boolean; mode?: SprinklerMode } = {}): Observable<SmartSprinkler> {
    return this.http.post<SmartSprinkler>(`${this.base}/iot/sprinklers/${encodeURIComponent(id)}/toggle`, body);
  }

  masterSprinklers(isActive: boolean, zone?: string): Observable<MasterSprinklerResult> {
    return this.http.post<MasterSprinklerResult>(`${this.base}/iot/sprinklers/master`, zone ? { isActive, zone } : { isActive });
  }

  // ---- incidents
  incidents(resolved?: boolean, limit = 100): Observable<IncidentAlert[]> {
    let params = new HttpParams().set('limit', limit);
    if (resolved !== undefined) params = params.set('resolved', resolved);
    return this.http.get<IncidentAlert[]>(`${this.base}/incidents`, { params });
  }

  resolveIncident(id: number): Observable<IncidentAlert> {
    return this.http.post<IncidentAlert>(`${this.base}/incidents/${id}/resolve`, {});
  }

  dispatch(request: ManualDispatchRequest): Observable<IncidentAlert> {
    return this.http.post<IncidentAlert>(`${this.base}/incidents/dispatch`, request);
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
