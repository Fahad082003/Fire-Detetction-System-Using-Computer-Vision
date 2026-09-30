import { HttpErrorResponse } from '@angular/common/http';
import { ProblemDetails, Severity, SensorState, SystemLevel } from '../models/api.models';

export const SEVERITY_RANK: Record<Severity, number> = { Low: 0, Medium: 1, High: 2, Critical: 3 };

/** Same rule as backend DashboardService: any High+ incident or sensor alarm = critical. */
export function systemLevel(openSeverities: Severity[], sensorStates: SensorState[]): SystemLevel {
  const top = Math.max(-1, ...openSeverities.map((s) => SEVERITY_RANK[s]));
  if (top >= SEVERITY_RANK.High || sensorStates.some((s) => s === 'Alarm' || s === 'Critical')) return 'critical';
  if (top >= 0 || sensorStates.includes('Warning')) return 'elevated';
  return 'nominal';
}

export function relativeTime(value: string | null | undefined, now = Date.now()): string {
  if (!value) return 'never';
  const seconds = Math.round((now - new Date(value).getTime()) / 1000);
  if (seconds < 5) return 'just now';
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  return hours < 24 ? `${hours}h ago` : `${Math.round(hours / 24)}d ago`;
}

/** Human-readable message from an HttpClient error (ProblemDetails aware). */
export function errorMessage(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    if (err.status === 0) return 'Cannot reach the API server. Is the backend running?';
    const body = err.error as ProblemDetails | string | null;
    if (body && typeof body === 'object') {
      if (body.errors) return Object.entries(body.errors).map(([k, v]) => `${k}: ${v.join(' ')}`).join('; ');
      return body.detail || body.title || err.message;
    }
    return typeof body === 'string' && body ? body : err.message;
  }
  return err instanceof Error ? err.message : String(err);
}
