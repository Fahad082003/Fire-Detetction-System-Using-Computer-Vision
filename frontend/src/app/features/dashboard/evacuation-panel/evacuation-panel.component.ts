import { DecimalPipe } from '@angular/common';
import { Component, DestroyRef, OnDestroy, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { MonitoringApiService } from '../../../core/services/monitoring-api.service';
import { SignalRService } from '../../../core/services/signalr.service';
import { FloorPlanMapComponent, PlanPointer } from '../../../shared/components/floor-plan-map.component';
import { DetectionFrame, FloorPlan, RouteResult } from '../../../shared/models/api.models';
import { errorMessage } from '../../../shared/utils/format';
import { homography, project } from '../../../shared/utils/homography';

/** Same as the camera tile: a camera that sent nothing for this long no longer places people. */
const PEOPLE_EXPIRE_MS = 4500;
const round1 = (v: number) => Math.round(v * 10) / 10;

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
    effect(() => { this.openZonesKey(); untracked(() => { if (this.start()) this.calculate(); }); });
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
