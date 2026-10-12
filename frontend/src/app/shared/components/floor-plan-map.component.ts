import { Component, ElementRef, computed, effect, input, output, signal, viewChild } from '@angular/core';
import { FloorElementType, FloorPlanElement } from '../models/api.models';

/** Pointer event translated to plan meters. `index` = element under the pointer, `handle` = its resize handle. */
export interface PlanPointer { kind: 'down' | 'move' | 'up'; x: number; y: number; index: number | null; handle: boolean; }
export interface PlanRect { x: number; y: number; width: number; height: number; }

const STYLE: Record<FloorElementType, { fill: string; stroke: string; dash?: string }> = {
  Room: { fill: 'rgba(148,163,184,0.10)', stroke: '#94a3b8' },
  Corridor: { fill: 'rgba(96,165,250,0.10)', stroke: '#60a5fa', dash: '4 3' },
  Wall: { fill: '#cbd5e1', stroke: '#cbd5e1' },
  Door: { fill: '#f59e0b', stroke: '#f59e0b' },
  Exit: { fill: 'rgba(34,197,94,0.55)', stroke: '#22c55e' },
  SafeZone: { fill: 'rgba(34,197,94,0.20)', stroke: '#22c55e' },
  DangerZone: { fill: 'rgba(239,68,68,0.10)', stroke: '#ef4444', dash: '5 3' },
};
const DRAW_ORDER: FloorElementType[] = ['Room', 'Corridor', 'SafeZone', 'DangerZone', 'Wall', 'Door', 'Exit'];
let nextUid = 0;

/** SVG floor-plan renderer shared by the editor and the dashboard evacuation panel. Units are meters. */
@Component({
  selector: 'app-floor-plan-map',
  template: `
    <div class="relative">
      <svg #svg [attr.viewBox]="viewBox()" preserveAspectRatio="xMidYMid meet"
           class="block h-[440px] w-full touch-none select-none rounded-lg border border-ink-700 bg-ink-950"
           [class.cursor-grab]="panEnabled()" [class.cursor-crosshair]="!panEnabled()"
           (wheel)="onWheel($event)" (pointerdown)="onDown($event)" (pointermove)="onMove($event)" (pointerup)="onUp($event)">
        <defs>
          <pattern [attr.id]="uid + '-grid'" width="1" height="1" patternUnits="userSpaceOnUse">
            <path d="M 1 0 L 0 0 0 1" fill="none" stroke="#1c2739" stroke-width="0.03" />
          </pattern>
          <marker [attr.id]="uid + '-arrow'" viewBox="0 0 10 10" refX="5" refY="5" markerWidth="4" markerHeight="4" orient="auto-start-reverse">
            <path d="M 0 0 L 10 5 L 0 10 z" fill="#22c55e" />
          </marker>
        </defs>
        <rect [attr.width]="width()" [attr.height]="height()" fill="#0a0f1a" />
        @if (imageUrl(); as url) {
          <image [attr.href]="url" x="0" y="0" [attr.width]="width()" [attr.height]="height()" preserveAspectRatio="none" opacity="0.35" />
        }
        <rect [attr.width]="width()" [attr.height]="height()" [attr.fill]="'url(#' + uid + '-grid)'" stroke="#2a3850" stroke-width="1" vector-effect="non-scaling-stroke" />

        @for (item of sorted(); track item.i) {
          <g [attr.data-index]="item.i" class="cursor-pointer">
            <rect [attr.x]="item.e.x" [attr.y]="item.e.y" [attr.width]="item.e.width" [attr.height]="item.e.height"
                  [attr.fill]="fill(item.e)" [attr.stroke]="stroke(item.e)" [attr.stroke-dasharray]="style(item.e).dash ?? null"
                  stroke-width="1.5" vector-effect="non-scaling-stroke" />
            @if (item.e.isBlocked && (item.e.type === 'Door' || item.e.type === 'Exit')) {
              <path [attr.d]="cross(item.e)" stroke="#ef4444" stroke-width="2" vector-effect="non-scaling-stroke" />
            }
            @if (item.e.name && item.e.type !== 'Wall') {
              <text [attr.x]="item.e.x + item.e.width / 2" [attr.y]="item.e.y + item.e.height / 2" [attr.font-size]="labelSize()"
                    text-anchor="middle" dominant-baseline="middle" [attr.fill]="style(item.e).stroke" class="pointer-events-none font-semibold">
                {{ item.e.type === 'Exit' ? 'EXIT ' + item.e.name : item.e.name }}
              </text>
            }
          </g>
        }

        @if (selectedElement(); as s) {
          <rect [attr.x]="s.x" [attr.y]="s.y" [attr.width]="s.width" [attr.height]="s.height" fill="none"
                stroke="#22d3ee" stroke-width="2" vector-effect="non-scaling-stroke" class="pointer-events-none" />
          <rect data-handle="1" [attr.x]="s.x + s.width - handleSize() / 2" [attr.y]="s.y + s.height - handleSize() / 2"
                [attr.width]="handleSize()" [attr.height]="handleSize()" fill="#22d3ee" class="cursor-nwse-resize" />
        }
        @if (preview(); as p) {
          <rect [attr.x]="p.x" [attr.y]="p.y" [attr.width]="p.width" [attr.height]="p.height" fill="rgba(34,211,238,0.12)"
                stroke="#22d3ee" stroke-dasharray="4 3" stroke-width="1.5" vector-effect="non-scaling-stroke" class="pointer-events-none" />
        }
        @if (routePoints(); as pts) {
          <polyline [attr.points]="pts" fill="none" stroke="#22c55e" [attr.stroke-width]="labelSize() * 0.6"
                    stroke-linejoin="round" stroke-linecap="round" [attr.marker-end]="'url(#' + uid + '-arrow)'" class="pointer-events-none" />
        }
        @if (start(); as st) {
          <circle [attr.cx]="st[0]" [attr.cy]="st[1]" [attr.r]="labelSize() * 0.8" fill="#22d3ee" stroke="#fff" stroke-width="1.5"
                  vector-effect="non-scaling-stroke" class="pointer-events-none" />
        }
      </svg>
      <div class="absolute right-2 top-2 flex gap-1">
        <button type="button" class="btn btn-sm" (click)="zoom(1 / 1.25)" title="Zoom in">+</button>
        <button type="button" class="btn btn-sm" (click)="zoom(1.25)" title="Zoom out">−</button>
        <button type="button" class="btn btn-sm" (click)="fit()" title="Fit to screen">Fit</button>
      </div>
    </div>
  `,
})
export class FloorPlanMapComponent {
  readonly width = input.required<number>();
  readonly height = input.required<number>();
  readonly elements = input.required<FloorPlanElement[]>();
  readonly imageUrl = input<string | null>(null);
  readonly route = input<[number, number][] | null>(null);
  readonly start = input<[number, number] | null>(null);
  readonly selected = input<number | null>(null);
  readonly preview = input<PlanRect | null>(null);
  /** Names of danger zones that are currently active (shown with a stronger red). */
  readonly activeDanger = input<string[]>([]);
  readonly panEnabled = input(false);
  readonly pointer = output<PlanPointer>();

  protected readonly uid = `fp${++nextUid}`;
  private readonly svg = viewChild.required<ElementRef<SVGSVGElement>>('svg');
  private readonly view = signal({ x: 0, y: 0, w: 1, h: 1 });
  private panFrom: { cx: number; cy: number; vx: number; vy: number } | null = null;

  protected readonly viewBox = computed(() => { const v = this.view(); return `${v.x} ${v.y} ${v.w} ${v.h}`; });
  protected readonly labelSize = computed(() => Math.max(this.width(), this.height()) / 60);
  protected readonly handleSize = computed(() => this.view().w / 60);
  protected readonly sorted = computed(() =>
    this.elements().map((e, i) => ({ e, i })).sort((a, b) => DRAW_ORDER.indexOf(a.e.type) - DRAW_ORDER.indexOf(b.e.type)));
  protected readonly selectedElement = computed(() => { const i = this.selected(); return i === null ? null : this.elements()[i] ?? null; });
  protected readonly routePoints = computed(() => this.route()?.map((p) => p.join(',')).join(' ') || null);

  constructor() {
    effect(() => { this.width(); this.height(); this.fit(); }); // refit when the plan size changes
  }

  protected style(e: FloorPlanElement) { return STYLE[e.type]; }

  protected fill(e: FloorPlanElement): string {
    if (e.type === 'DangerZone' && this.isActive(e)) return 'rgba(239,68,68,0.38)';
    if (e.isBlocked && (e.type === 'Door' || e.type === 'Exit')) return 'rgba(239,68,68,0.45)';
    return STYLE[e.type].fill;
  }

  protected stroke(e: FloorPlanElement): string {
    return e.isBlocked && (e.type === 'Door' || e.type === 'Exit') ? '#ef4444' : STYLE[e.type].stroke;
  }

  protected cross(e: FloorPlanElement): string {
    return `M ${e.x} ${e.y} L ${e.x + e.width} ${e.y + e.height} M ${e.x + e.width} ${e.y} L ${e.x} ${e.y + e.height}`;
  }

  private isActive(e: FloorPlanElement): boolean {
    return e.isActive || this.activeDanger().includes(e.name);
  }

  fit(): void {
    const pad = Math.max(this.width(), this.height()) * 0.03;
    this.view.set({ x: -pad, y: -pad, w: this.width() + 2 * pad, h: this.height() + 2 * pad });
  }

  zoom(factor: number, cx?: number, cy?: number): void {
    const v = this.view();
    const max = Math.max(this.width(), this.height()) * 4;
    const w = Math.min(max, Math.max(1, v.w * factor));
    const f = w / v.w;
    const x0 = cx ?? v.x + v.w / 2, y0 = cy ?? v.y + v.h / 2;
    this.view.set({ x: x0 - (x0 - v.x) * f, y: y0 - (y0 - v.y) * f, w, h: v.h * f });
  }

  protected onWheel(ev: WheelEvent): void {
    ev.preventDefault();
    const p = this.toPlan(ev);
    this.zoom(ev.deltaY > 0 ? 1.15 : 1 / 1.15, p.x, p.y);
  }

  protected onDown(ev: PointerEvent): void {
    this.svg().nativeElement.setPointerCapture(ev.pointerId);
    if (this.panEnabled() || ev.button === 1) {
      const v = this.view();
      this.panFrom = { cx: ev.clientX, cy: ev.clientY, vx: v.x, vy: v.y };
      return;
    }
    this.emit('down', ev);
  }

  protected onMove(ev: PointerEvent): void {
    if (this.panFrom) {
      const scale = this.view().w / this.svg().nativeElement.clientWidth;
      const from = this.panFrom;
      this.view.update((v) => ({ ...v, x: from.vx - (ev.clientX - from.cx) * scale, y: from.vy - (ev.clientY - from.cy) * scale }));
      return;
    }
    this.emit('move', ev);
  }

  protected onUp(ev: PointerEvent): void {
    if (this.panFrom) { this.panFrom = null; return; }
    this.emit('up', ev);
  }

  private emit(kind: PlanPointer['kind'], ev: PointerEvent): void {
    const p = this.toPlan(ev);
    const target = ev.target as Element;
    const index = target.closest('[data-index]')?.getAttribute('data-index');
    this.pointer.emit({ kind, x: p.x, y: p.y, index: index == null ? null : Number(index), handle: !!target.closest('[data-handle]') });
  }

  private toPlan(ev: MouseEvent): { x: number; y: number } {
    const svg = this.svg().nativeElement;
    const m = svg.getScreenCTM();
    if (!m) return { x: 0, y: 0 };
    const p = new DOMPoint(ev.clientX, ev.clientY).matrixTransform(m.inverse());
    return { x: p.x, y: p.y };
  }
}
