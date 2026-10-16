import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MonitoringApiService } from '../../core/services/monitoring-api.service';
import { SignalRService } from '../../core/services/signalr.service';
import { ConfirmService, ToastService } from '../../core/services/ui.service';
import { FloorPlanMapComponent, PlanPointer, PlanRect } from '../../shared/components/floor-plan-map.component';
import { CameraFeed, FloorElementType, FloorPlan, FloorPlanElement } from '../../shared/models/api.models';
import { errorMessage } from '../../shared/utils/format';

type Tool = 'select' | 'pan' | FloorElementType;

export const ELEMENT_TYPES: { type: FloorElementType; label: string }[] = [
  { type: 'Room', label: 'Room' }, { type: 'Corridor', label: 'Corridor' }, { type: 'Wall', label: 'Wall' },
  { type: 'Door', label: 'Door' }, { type: 'Exit', label: 'Exit' }, { type: 'SafeZone', label: 'Safe zone' },
  { type: 'DangerZone', label: 'Danger zone' },
];
const SNAP = 0.1;
const MIN_SIZE = 0.2;
const MAX_IMAGE_BYTES = 5 * 1024 * 1024;
/** Walls and doors are drawn as lines: the short side gets this fixed thickness (doors overlap the 0.2 m wall). */
const THICKNESS: Partial<Record<FloorElementType, number>> = { Wall: 0.2, Door: 0.4 };

const snap = (v: number) => Math.round(v / SNAP) * SNAP;
const round = (v: number) => Math.round(v * 100) / 100;

/** Rectangle dragged from a to b, snapped, clamped to the plan; walls/doors become thin bars along the drag direction. */
export function drawnRect(type: FloorElementType, a: { x: number; y: number }, b: { x: number; y: number }, w: number, h: number): PlanRect | null {
  const clamp = (v: number, max: number) => Math.min(max, Math.max(0, snap(v)));
  let x = Math.min(clamp(a.x, w), clamp(b.x, w)), y = Math.min(clamp(a.y, h), clamp(b.y, h));
  let width = Math.abs(clamp(b.x, w) - clamp(a.x, w)), height = Math.abs(clamp(b.y, h) - clamp(a.y, h));
  const t = THICKNESS[type];
  if (t) {
    if (width >= height) { height = t; y = Math.min(h - t, Math.max(0, clamp(a.y, h) - t / 2)); }
    else { width = t; x = Math.min(w - t, Math.max(0, clamp(a.x, w) - t / 2)); }
  }
  if (width < MIN_SIZE || height < MIN_SIZE) return null;
  return { x: round(x), y: round(y), width: round(width), height: round(height) };
}

/**
 * Floor-plan manager: list/create/delete plans, upload a PNG/JPG reference image, and draw/edit rooms, corridors,
 * walls, doors, exits, safe zones and danger zones. Saving sends the whole element list (PUT /api/floorplans/{id}).
 */
@Component({
  selector: 'app-floor-plans',
  imports: [ReactiveFormsModule, FloorPlanMapComponent],
  templateUrl: './floor-plans.component.html',
  host: { '(window:keydown)': 'onKey($event)' },
})
export class FloorPlansComponent implements OnInit, OnDestroy {
  private readonly api = inject(MonitoringApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  protected readonly live = inject(SignalRService);

  protected readonly types = ELEMENT_TYPES;
  protected readonly plans = signal<FloorPlan[]>([]);
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly current = signal<FloorPlan | null>(null);
  protected readonly elements = signal<FloorPlanElement[]>([]);
  protected readonly selected = signal<number | null>(null);
  protected readonly tool = signal<Tool>('select');
  protected readonly preview = signal<PlanRect | null>(null);
  protected readonly imageUrl = signal<string | null>(null);
  protected readonly dirty = signal(false);
  protected readonly busy = signal(false);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    building: ['', Validators.maxLength(120)],
    floorNumber: [0, [Validators.required, Validators.min(-10), Validators.max(200)]],
    widthMeters: [30, [Validators.required, Validators.min(1), Validators.max(1000)]],
    heightMeters: [20, [Validators.required, Validators.min(1), Validators.max(1000)]],
  });
  private readonly formValue = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  protected readonly planWidth = computed(() => Math.max(1, Number(this.formValue().widthMeters) || 1));
  protected readonly planHeight = computed(() => Math.max(1, Number(this.formValue().heightMeters) || 1));
  protected readonly selectedElement = computed(() => { const i = this.selected(); return i === null ? null : this.elements()[i]; });
  protected readonly groups = computed(() => {
    const map = new Map<string, FloorPlan[]>();
    for (const p of this.plans()) map.set(p.building || 'No building', [...(map.get(p.building || 'No building') ?? []), p]);
    return [...map.entries()].map(([building, plans]) => ({ building, plans: plans.sort((a, b) => a.floorNumber - b.floorNumber) }));
  });

  // ---- camera calibration: 4 floor points in the camera frame (px) and on this plan (m)
  protected readonly calibCamera = signal<CameraFeed | null>(null);
  protected readonly calibImage = signal<[number, number][]>([]);
  protected readonly calibPlan = signal<[number, number][]>([]);
  /** Row whose plan point the next click on the plan fills in. */
  protected readonly calibPick = signal<number | null>(null);

  private drag: { mode: 'draw' | 'move' | 'resize'; start: { x: number; y: number }; offset: { x: number; y: number } } | null = null;

  ngOnInit(): void {
    this.load();
    this.form.valueChanges.subscribe(() => { if (this.form.dirty) this.dirty.set(true); });
  }

  ngOnDestroy(): void {
    this.setImage(null);
  }

  protected load(openId?: number): void {
    this.loading.set(true);
    this.api.floorPlans().subscribe({
      next: (plans) => {
        this.plans.set(plans);
        this.loading.set(false);
        this.loadError.set(null);
        const target = plans.find((p) => p.id === (openId ?? this.current()?.id)) ?? plans[0];
        if (target) this.open(target, true); else this.current.set(null);
      },
      error: (err) => { this.loading.set(false); this.loadError.set(errorMessage(err)); },
    });
  }

  protected async select(plan: FloorPlan): Promise<void> {
    if (plan.id === this.current()?.id) return;
    if (this.dirty() && !(await this.confirm.ask('Discard unsaved changes?', `Changes to "${this.current()?.name}" are not saved.`, 'Discard'))) return;
    this.open(plan, true);
  }

  private open(plan: FloorPlan, reloadImage: boolean): void {
    this.current.set(plan);
    this.form.reset({ name: plan.name, building: plan.building, floorNumber: plan.floorNumber, widthMeters: plan.widthMeters, heightMeters: plan.heightMeters });
    this.elements.set(plan.elements.map((e) => ({ ...e })));
    this.selected.set(null);
    this.dirty.set(false);
    if (!reloadImage) return;
    this.setImage(null);
    // Drop a late image of a plan the user already left.
    if (plan.hasImage) this.api.floorPlanImage(plan.id).subscribe({ next: (blob) => { if (this.current()?.id === plan.id) this.setImage(URL.createObjectURL(blob)); }, error: () => undefined });
  }

  private setImage(url: string | null): void {
    const old = this.imageUrl();
    if (old) URL.revokeObjectURL(old);
    this.imageUrl.set(url);
  }

  protected async create(): Promise<void> {
    if (this.dirty() && !(await this.confirm.ask('Discard unsaved changes?', `Changes to "${this.current()?.name}" are not saved.`, 'Discard'))) return;
    this.busy.set(true);
    this.api.createFloorPlan({ name: 'New floor plan', building: '', floorNumber: 0, widthMeters: 30, heightMeters: 20, elements: [] }).subscribe({
      next: (p) => { this.busy.set(false); this.toast.show('Floor plan created', p.name, 'success', 3000); this.load(p.id); },
      error: (err) => { this.busy.set(false); this.toast.show('Could not create floor plan', errorMessage(err), 'error'); },
    });
  }

  protected save(): void {
    const plan = this.current();
    if (!plan) return;
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.api.updateFloorPlan(plan.id, { ...this.form.getRawValue(), elements: this.elements() }).subscribe({
      next: (saved) => {
        this.busy.set(false);
        this.plans.update((list) => list.map((p) => (p.id === saved.id ? saved : p)));
        this.open(saved, false);
        this.toast.show('Floor plan saved', `${saved.elements.length} element(s)`, 'success', 3000);
      },
      error: (err) => { this.busy.set(false); this.toast.show('Save failed', errorMessage(err), 'error'); },
    });
  }

  protected async remove(): Promise<void> {
    const plan = this.current();
    if (!plan || !(await this.confirm.ask(`Delete "${plan.name}"?`, 'The floor plan, its elements and its image are deleted permanently.', 'Delete'))) return;
    this.api.deleteFloorPlan(plan.id).subscribe({
      next: () => { this.current.set(null); this.toast.show('Floor plan deleted', plan.name, 'success', 3000); this.load(); },
      error: (err) => this.toast.show('Delete failed', errorMessage(err), 'error'),
    });
  }

  protected upload(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    const plan = this.current();
    if (!file || !plan) return;
    if (!['image/png', 'image/jpeg'].includes(file.type)) { this.toast.show('Unsupported file', 'Choose a PNG or JPG image.', 'warning'); return; }
    if (file.size > MAX_IMAGE_BYTES) { this.toast.show('Image too large', 'Maximum size is 5 MB.', 'warning'); return; }
    this.busy.set(true);
    this.api.uploadFloorPlanImage(plan.id, file).subscribe({
      next: (p) => {
        this.busy.set(false);
        this.current.set({ ...plan, hasImage: true });
        this.setImage(URL.createObjectURL(file));
        this.plans.update((list) => list.map((x) => (x.id === p.id ? { ...x, hasImage: true } : x)));
        this.toast.show('Image uploaded', 'Trace walls, rooms and exits on top of it. The image itself is not analysed.', 'success');
      },
      error: (err) => { this.busy.set(false); this.toast.show('Upload failed', errorMessage(err), 'error'); },
    });
  }

  // ---------------------------------------------------------------- drawing / editing
  protected onPointer(p: PlanPointer): void {
    const pick = this.calibPick();
    if (pick !== null) {
      if (p.kind === 'down') { this.setCalib('plan', pick, [round(p.x), round(p.y)]); this.calibPick.set(null); }
      return;
    }
    const tool = this.tool();
    // The cyan corner resizes with any tool: right after drawing, the draw tool is still active.
    if (tool === 'select' || (p.kind === 'down' && p.handle) || this.drag?.mode === 'resize') return this.onSelectPointer(p);
    if (tool === 'pan') return;
    if (p.kind === 'down') { this.drag = { mode: 'draw', start: p, offset: { x: 0, y: 0 } }; return; }
    if (!this.drag || this.drag.mode !== 'draw') return;
    const rect = drawnRect(tool, this.drag.start, p, this.planWidth(), this.planHeight());
    if (p.kind === 'move') { this.preview.set(rect); return; }
    this.drag = null;
    this.preview.set(null);
    if (p.kind !== 'up' || !rect) return;
    const count = this.elements().filter((e) => e.type === tool).length + 1;
    const label = ELEMENT_TYPES.find((t) => t.type === tool)!.label;
    this.elements.update((list) => [...list, { type: tool, name: tool === 'Wall' ? '' : `${label} ${count}`, ...rect, isBlocked: false, isActive: false, zone: null }]);
    this.selected.set(this.elements().length - 1);
    this.dirty.set(true);
  }

  private onSelectPointer(p: PlanPointer): void {
    const i = this.selected();
    if (p.kind === 'down') {
      if (p.handle && i !== null) { this.drag = { mode: 'resize', start: p, offset: { x: 0, y: 0 } }; return; }
      this.selected.set(p.index);
      const e = p.index === null ? null : this.elements()[p.index];
      this.drag = e ? { mode: 'move', start: p, offset: { x: p.x - e.x, y: p.y - e.y } } : null;
      return;
    }
    if (!this.drag || i === null) return;
    if (p.kind === 'up') { this.drag = null; return; }
    const e = this.elements()[i];
    if (this.drag.mode === 'move') {
      this.update({
        x: round(Math.min(this.planWidth() - e.width, Math.max(0, snap(p.x - this.drag.offset.x)))),
        y: round(Math.min(this.planHeight() - e.height, Math.max(0, snap(p.y - this.drag.offset.y)))),
      });
    } else {
      this.update({
        width: round(Math.max(MIN_SIZE, Math.min(this.planWidth() - e.x, snap(p.x - e.x)))),
        height: round(Math.max(MIN_SIZE, Math.min(this.planHeight() - e.y, snap(p.y - e.y)))),
      });
    }
  }

  /** Patch the selected element (from dragging or the properties form). */
  protected update(patch: Partial<FloorPlanElement>): void {
    const i = this.selected();
    if (i === null) return;
    this.elements.update((list) => list.map((e, k) => (k === i ? { ...e, ...patch } : e)));
    this.dirty.set(true);
  }

  protected numberInput(field: 'x' | 'y' | 'width' | 'height', event: Event): void {
    const v = Number((event.target as HTMLInputElement).value);
    if (Number.isFinite(v)) this.update({ [field]: round(Math.max(field === 'width' || field === 'height' ? MIN_SIZE : 0, v)) });
  }

  protected text(field: 'name' | 'zone', event: Event): void {
    const v = (event.target as HTMLInputElement).value;
    this.update(field === 'zone' ? { zone: v.trim() || null } : { name: v });
  }

  protected check(field: 'isBlocked' | 'isActive', event: Event): void {
    this.update({ [field]: (event.target as HTMLInputElement).checked });
  }

  protected deleteSelected(): void {
    const i = this.selected();
    if (i === null) return;
    this.elements.update((list) => list.filter((_, k) => k !== i));
    this.selected.set(null);
    this.dirty.set(true);
  }

  protected onKey(ev: KeyboardEvent): void {
    const typing = ev.target instanceof HTMLInputElement || ev.target instanceof HTMLTextAreaElement || ev.target instanceof HTMLSelectElement;
    if (!typing && (ev.key === 'Delete' || ev.key === 'Backspace')) this.deleteSelected();
    if (!typing && ev.key === 'Escape') { this.selected.set(null); this.tool.set('select'); }
  }

  // ---------------------------------------------------------------- camera calibration
  protected chooseCamera(id: string): void {
    const cam = this.live.cameras().find((c) => c.id === id) ?? null;
    const cal = cam?.calibration?.floorPlanId === this.current()?.id ? cam?.calibration : null;
    this.calibCamera.set(cam);
    this.calibImage.set(cal ? cal.imagePoints.map((p) => [...p] as [number, number]) : [[0, 0], [0, 0], [0, 0], [0, 0]]);
    this.calibPlan.set(cal ? cal.planPoints.map((p) => [...p] as [number, number]) : [[0, 0], [0, 0], [0, 0], [0, 0]]);
    this.calibPick.set(null);
  }

  protected setCalib(kind: 'image' | 'plan', row: number, point: [number, number]): void {
    (kind === 'image' ? this.calibImage : this.calibPlan).update((pts) => pts.map((p, i) => (i === row ? point : p)));
  }

  protected calibInput(kind: 'image' | 'plan', row: number, axis: 0 | 1, event: Event): void {
    const p = [...(kind === 'image' ? this.calibImage() : this.calibPlan())[row]] as [number, number];
    p[axis] = Number((event.target as HTMLInputElement).value);
    this.setCalib(kind, row, p);
  }

  protected saveCalibration(): void {
    const cam = this.calibCamera(), plan = this.current();
    if (!cam || !plan) return;
    this.api.saveCalibration(cam.id, { floorPlanId: plan.id, imagePoints: this.calibImage(), planPoints: this.calibPlan() }).subscribe({
      next: (saved) => { this.updateCamera(saved); this.toast.show('Camera calibrated', `${saved.name} now places people on "${plan.name}".`, 'success', 3000); },
      error: (err) => this.toast.show('Calibration not saved', errorMessage(err), 'error'),
    });
  }

  protected clearCalibration(): void {
    const cam = this.calibCamera();
    if (!cam) return;
    this.api.clearCalibration(cam.id).subscribe({
      next: (saved) => { this.updateCamera(saved); this.toast.show('Calibration removed', saved.name, 'success', 3000); },
      error: (err) => this.toast.show('Calibration not removed', errorMessage(err), 'error'),
    });
  }

  // shortcut: only this browser's camera list is updated; other open dashboards see the change after a reconnect or reload.
  private updateCamera(saved: CameraFeed): void {
    this.live.cameras.update((list) => list.map((c) => (c.id === saved.id ? saved : c)));
    this.calibCamera.set(saved);
  }

  protected typeLabel(type: FloorElementType): string {
    return ELEMENT_TYPES.find((t) => t.type === type)?.label ?? type;
  }
}
