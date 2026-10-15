import { DecimalPipe } from '@angular/common';
import { Component, DestroyRef, OnDestroy, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { MonitoringApiService } from '../../../core/services/monitoring-api.service';
import { SignalRService } from '../../../core/services/signalr.service';
import { FloorPlanMapComponent, PlanPointer } from '../../../shared/components/floor-plan-map.component';
import { DetectionFrame, FireRisk, FloorPlan, RouteResult } from '../../../shared/models/api.models';
import { errorMessage } from '../../../shared/utils/format';
import { homography, project } from '../../../shared/utils/homography';

/** Same as the camera tile: a camera that sent nothing for this long no longer places people. */
const PEOPLE_EXPIRE_MS = 4500;
const round1 = (v: number) => Math.round(v * 10) / 10;
/** Heatmap colours by risk level: 1 low (yellow), 2 medium (orange), 3 high (red), 4 burning (dark red). */
const RISK_RGBA = ['', 'rgba(250,204,21,0.35)', 'rgba(249,115,22,0.5)', 'rgba(239,68,68,0.6)', 'rgba(185,28,28,0.85)'];
export const RISK_CLASS: Record<string, string> = { Burning: 'bg-red-700', High: 'bg-red-500', Medium: 'bg-orange-500', Low: 'bg-yellow-400' };

/**
 * Dashboard evacuation view (requirements 2.4, 6.4, 5.2): pick a floor plan, click the person's KNOWN position
 * (or a live person dot from a calibrated camera), and the backend returns the safest route to the nearest reachable
 * exit plus an estimated walking time. The route is recalculated when the set of zones with open incidents changes.
 */
@Component({
  selector: 'app-evacuation-panel',
  imports: [FloorPlanMapComponent, DecimalPipe, RouterLink],
  templateUrl: './evacuation-panel.component.html',
})
export class EvacuationPanelComponent implements OnDestroy {
  private readonly api = inject(MonitoringApiService);
  private readonly live = inject(SignalRService);

  protected readonly plans = signal<FloorPlan[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly planId = signal<number | null>(null);
  protected readonly imageUrl = signal<string | null>(null);
  protected readonly start = signal<[number, number] | null>(null);
  protected readonly result = signal<RouteResult | null>(null);
  protected readonly routing = signal(false);
  protected readonly riskClass = RISK_CLASS;
  /** Rule-based estimate of the rooms the fire may reach next (GET /api/floorplans/{id}/risk). */
  protected readonly risk = signal<FireRisk | null>(null);
  protected readonly riskAreas = computed(() => this.risk()?.areas.filter((a) => a.level !== 'None') ?? []);
  /** The risk grid painted as a small image (one pixel per cell); the SVG stretches it over the plan. */
  protected readonly riskLayer = computed(() => {
    const r = this.risk();
    if (!r?.active) return null;
    const canvas = document.createElement('canvas');
    canvas.width = r.cols;
    canvas.height = r.rows;
    const ctx = canvas.getContext('2d');
    if (!ctx) return null; // no canvas support (e.g. unit tests): the room list below still shows the risk
    r.cells.forEach((v, i) => { if (v > 0) { ctx.fillStyle = RISK_RGBA[v]; ctx.fillRect(i % r.cols, Math.floor(i / r.cols), 1, 1); } });
    return { url: canvas.toDataURL(), width: r.cols * r.cellMeters, height: r.rows * r.cellMeters };
  });
  /** People seen by calibrated cameras, mapped from their feet (box bottom-centre) onto this plan. */
  protected readonly people = signal<{ x: number; y: number }[]>([]);
  private readonly seen = new Map<string, { at: number; points: { x: number; y: number }[] }>();
  /** Camera id → frame-to-plan transform, for cameras calibrated against the selected plan. */
  private readonly transforms = computed(() => {
    const map = new Map<string, number[]>();
    for (const c of this.live.cameras()) {
      const cal = c.calibration;
      const h = cal && cal.floorPlanId === this.planId() ? homography(cal.imagePoints, cal.planPoints) : null;
      if (h) map.set(c.id, h);
    }
    return map;
  });

  protected readonly plan = computed(() => this.plans().find((p) => p.id === this.planId()) ?? null);
  protected readonly safeZones = computed(() => this.plan()?.elements.filter((e) => e.type === 'SafeZone') ?? []);
  /** Changes only when the set of zones with open incidents changes (not on every incident update). */
  private readonly openZonesKey = computed(() => [...new Set(this.live.openIncidents().map((i) => i.zone))].sort().join('|'));

  constructor() {
    this.api.floorPlans().subscribe({
      next: (plans) => { this.plans.set(plans); this.loading.set(false); if (plans[0]) this.choose(plans[0].id); },
      error: (err) => { this.loading.set(false); this.error.set(errorMessage(err)); },
    });
    // Incidents opening or closing change the active danger zones: refresh the risk estimate and the route.
    effect(() => { this.openZonesKey(); untracked(() => { this.loadRisk(); if (this.start()) this.calculate(); }); });
    this.live.detection$.pipe(takeUntilDestroyed()).subscribe((f) => this.placePeople(f));
    const timer = setInterval(() => this.refreshPeople(), 1000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  ngOnDestroy(): void {
    this.setImage(null);
  }

  protected choose(id: number): void {
    this.planId.set(id);
    this.start.set(null);
    this.result.set(null);
    this.routing.set(false);
    this.risk.set(null);
    this.loadRisk();
    this.seen.clear();
    this.people.set([]);
    this.setImage(null);
    // Late responses for a previously chosen plan are dropped (user switched plans meanwhile).
    if (this.plan()?.hasImage) this.api.floorPlanImage(id).subscribe({ next: (b) => { if (this.planId() === id) this.setImage(URL.createObjectURL(b)); }, error: () => undefined });
  }

  protected onPointer(p: PlanPointer): void {
    if (p.kind !== 'down') return;
    const person = this.people().find((q) => Math.hypot(q.x - p.x, q.y - p.y) < 1); // clicked a live person dot
    this.start.set(person ? [round1(person.x), round1(person.y)] : [round1(p.x), round1(p.y)]);
    this.calculate();
  }

  private calculate(): void {
    const id = this.planId(), s = this.start();
    if (!id || !s) return;
    this.routing.set(true);
    this.api.route(id, s[0], s[1]).subscribe({
      next: (r) => { if (this.planId() !== id) return; this.result.set(r); this.routing.set(false); this.error.set(null); },
      error: (err) => { if (this.planId() !== id) return; this.result.set(null); this.routing.set(false); this.error.set(errorMessage(err)); },
    });
  }

  private loadRisk(): void {
    const id = this.planId();
    if (!id) return;
    this.api.floorPlanRisk(id).subscribe({ next: (r) => { if (this.planId() === id) this.risk.set(r); }, error: () => undefined });
  }

  private placePeople(f: DetectionFrame): void {
    const h = this.transforms().get(f.cameraId), plan = this.plan();
    if (!h || !plan) return;
    const sx = f.normalized ? f.frameWidth : 1, sy = f.normalized ? f.frameHeight : 1;
    const points = f.detections.filter((d) => d.category === 'Human')
      .map((d) => project(h, [(d.bbox[0] + d.bbox[2] / 2) * sx, (d.bbox[1] + d.bbox[3]) * sy]))
      .filter(([x, y]) => x >= 0 && y >= 0 && x <= plan.widthMeters && y <= plan.heightMeters) // outside the calibrated floor
      .map(([x, y]) => ({ x, y }));
    this.seen.set(f.cameraId, { at: Date.now(), points });
    this.refreshPeople();
  }

  private refreshPeople(): void {
    const now = Date.now();
    for (const [id, v] of this.seen) if (now - v.at > PEOPLE_EXPIRE_MS) this.seen.delete(id);
    this.people.set([...this.seen.values()].flatMap((v) => v.points));
  }

  private setImage(url: string | null): void {
    const old = this.imageUrl();
    if (old) URL.revokeObjectURL(old);
    this.imageUrl.set(url);
  }
}
