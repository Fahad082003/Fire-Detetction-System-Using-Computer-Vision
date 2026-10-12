import { DecimalPipe } from '@angular/common';
import { Component, OnDestroy, computed, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MonitoringApiService } from '../../../core/services/monitoring-api.service';
import { SignalRService } from '../../../core/services/signalr.service';
import { FloorPlanMapComponent, PlanPointer } from '../../../shared/components/floor-plan-map.component';
import { FloorPlan, RouteResult } from '../../../shared/models/api.models';
import { errorMessage } from '../../../shared/utils/format';

/**
 * Dashboard evacuation view (requirements 2.4, 6.4, 5.2): pick a floor plan, click the person's KNOWN position,
 * and the backend returns the safest route to the nearest reachable exit plus an estimated walking time.
 * The route is recalculated when the set of zones with open incidents changes.
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
  }

  ngOnDestroy(): void {
    this.setImage(null);
  }

  protected choose(id: number): void {
    this.planId.set(id);
    this.start.set(null);
    this.result.set(null);
    this.setImage(null);
    if (this.plan()?.hasImage) this.api.floorPlanImage(id).subscribe({ next: (b) => this.setImage(URL.createObjectURL(b)), error: () => undefined });
  }

  protected onPointer(p: PlanPointer): void {
    if (p.kind !== 'down') return;
    this.start.set([Math.round(p.x * 10) / 10, Math.round(p.y * 10) / 10]);
    this.calculate();
  }

  private calculate(): void {
    const id = this.planId(), s = this.start();
    if (!id || !s) return;
    this.routing.set(true);
    this.api.route(id, s[0], s[1]).subscribe({
      next: (r) => { this.result.set(r); this.routing.set(false); this.error.set(null); },
      error: (err) => { this.result.set(null); this.routing.set(false); this.error.set(errorMessage(err)); },
    });
  }

  private setImage(url: string | null): void {
    const old = this.imageUrl();
    if (old) URL.revokeObjectURL(old);
    this.imageUrl.set(url);
  }
}
