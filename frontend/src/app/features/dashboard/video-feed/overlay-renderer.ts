import { Injectable } from '@angular/core';
import { DangerZone, DetectionFrame, Heatmap } from '../../../shared/models/api.models';

/** Box colours: Fire = red, Humans = green, humans inside a danger zone = pulsing amber. */
export const OVERLAY_COLORS = {
  fire: '#ef4444',
  human: '#22c55e',
  humanAtRisk: '#f59e0b',
  smoke: '#94a3b8',
  danger: '#f43f5e',
  vehicle: '#a78bfa',
  other: '#e2e8f0',
} as const;
const ZONE_RGB: Record<DangerZone['level'], string> = { Warning: '245,158,11', Danger: '239,68,68', Critical: '220,38,38', Safe: '34,197,94' };
const LABEL_FONT = '600 11px "JetBrains Mono", Consolas, monospace';
export const STALE_MS = 1500;
const EXPIRE_MS = 4500;
const EASING_RATE = 14;

export interface Rect { x: number; y: number; w: number; h: number; }

/** Rect occupied by media drawn with `object-fit: contain` inside a stage of size W×H (letterboxing). */
export function contentRect(stageW: number, stageH: number, srcW: number, srcH: number): Rect {
  if (!srcW || !srcH) return { x: 0, y: 0, w: stageW, h: stageH };
  const scale = Math.min(stageW / srcW, stageH / srcH);
  const w = srcW * scale;
  const h = srcH * scale;
  return { x: (stageW - w) / 2, y: (stageH - h) / 2, w, h };
}

/** [x,y,w,h] in frame pixels (or 0..1 when normalized) → 0..1 fractions of the frame. */
export function toUnitBox(bbox: readonly number[], frame: Pick<DetectionFrame, 'normalized' | 'frameWidth' | 'frameHeight'>): number[] {
  if (frame.normalized) return [...bbox];
  return [bbox[0] / frame.frameWidth, bbox[1] / frame.frameHeight, bbox[2] / frame.frameWidth, bbox[3] / frame.frameHeight];
}

function rgba(hex: string, a: number): string {
  const n = parseInt(hex.slice(1), 16);
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${a.toFixed(2)})`;
}

function heatColor(v: number): [number, number, number] {
  if (v < 0.5) { const t = v / 0.5; return [250, Math.round(204 - 40 * t), Math.round(21 * (1 - t))]; }
  if (v < 0.85) { const t = (v - 0.5) / 0.35; return [Math.round(250 - 11 * t), Math.round(164 - 96 * t), Math.round(68 * t)]; }
  const t = (v - 0.85) / 0.15;
  return [Math.round(239 + 16 * t), Math.round(68 + 170 * t), Math.round(68 + 150 * t)];
}

interface Track { box: number[]; target: number[]; det: DetectionFrame['detections'][number]; born: number; }

/**
 * Draws one camera's AI overlay on a <canvas> stacked over the <video>/<img>.
 * Boxes glide smoothly between frames (matched by trackId) and fade out when the AI stream goes quiet.
 */
export class OverlayRenderer {
  showHeatmap = true;
  media: HTMLVideoElement | HTMLImageElement | null = null;

  private readonly ctx: CanvasRenderingContext2D;
  private frame: DetectionFrame | null = null;
  private receivedAt = 0;
  private tracks = new Map<string, Track>();
  private heatmap: HTMLCanvasElement | null = null;
  private cssW = 0;
  private cssH = 0;
  private dirtySize = true;
  private visible = true;
  private blank = true;
  private stale = true;
  private readonly resizeObserver = new ResizeObserver(() => (this.dirtySize = true));
  private readonly intersectionObserver = new IntersectionObserver(([e]) => (this.visible = e.isIntersecting));

  constructor(
    private readonly stage: HTMLElement,
    private readonly canvas: HTMLCanvasElement,
    private readonly onStale: (stale: boolean) => void,
  ) {
    this.ctx = canvas.getContext('2d')!;
    this.resizeObserver.observe(stage);
    this.intersectionObserver.observe(stage);
  }

  destroy(): void {
    this.resizeObserver.disconnect();
    this.intersectionObserver.disconnect();
  }

  update(frame: DetectionFrame): void {
    const now = performance.now();
    this.frame = frame;
    this.receivedAt = now;
    const seen = new Set<string>();
    frame.detections.forEach((det, i) => {
      const key = det.trackId ? `t:${det.trackId}` : `${det.category}:${i}`;
      seen.add(key);
      const target = toUnitBox(det.bbox, frame);
      const track = this.tracks.get(key);
      if (track) { track.target = target; track.det = det; }
      else this.tracks.set(key, { box: [...target], target, det, born: now });
    });
    for (const key of this.tracks.keys()) if (!seen.has(key)) this.tracks.delete(key);
    this.heatmap = frame.heatmap ? this.buildHeatmap(frame.heatmap) : null;
  }

  /** Called by OverlayLoopService on every animation frame. */
  draw(now: number, ease: number): void {
    if (!this.visible) return;
    if (this.dirtySize) this.resize();
    const { ctx, cssW: W, cssH: H, frame } = this;

    if (!frame) {
      if (!this.blank) { ctx.clearRect(0, 0, W, H); this.blank = true; }
      this.setStale(true);
      return;
    }
    const age = now - this.receivedAt;
    if (age > EXPIRE_MS) {
      this.frame = null;
      this.tracks.clear();
      this.heatmap = null;
      ctx.clearRect(0, 0, W, H);
      this.blank = true;
      this.setStale(true);
      return;
    }
    this.setStale(age > STALE_MS);
    ctx.clearRect(0, 0, W, H);
    this.blank = false;

    const alpha = age > STALE_MS ? Math.max(0, 1 - (age - STALE_MS) / (EXPIRE_MS - STALE_MS)) : 1;
    const m = this.media as (HTMLVideoElement & HTMLImageElement) | null;
    const srcW = m?.videoWidth || m?.naturalWidth || (frame.normalized ? 0 : frame.frameWidth);
    const srcH = m?.videoHeight || m?.naturalHeight || (frame.normalized ? 0 : frame.frameHeight);
    const rect = contentRect(W, H, srcW, srcH);
    const pulse = 0.5 + 0.5 * Math.sin(now / 170);

    if (this.showHeatmap && this.heatmap) {
      ctx.globalAlpha = alpha * 0.55;
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = 'high';
      ctx.drawImage(this.heatmap, rect.x, rect.y, rect.w, rect.h);
    }
    ctx.globalAlpha = alpha;
    for (const zone of frame.dangerZones) this.drawZone(zone, frame, rect, now, pulse);
    for (const track of this.tracks.values()) {
      for (let i = 0; i < 4; i++) track.box[i] += (track.target[i] - track.box[i]) * ease;
      this.drawBox(track, rect, pulse, now);
    }
    if (frame.humanCoordinates.length) this.drawPoints(frame, rect);
    ctx.globalAlpha = 1;
  }

  // ------------------------------------------------------------------ internals
  private resize(): void {
    const r = this.stage.getBoundingClientRect();
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    this.cssW = r.width;
    this.cssH = r.height;
    this.canvas.width = Math.max(1, Math.round(r.width * dpr));
    this.canvas.height = Math.max(1, Math.round(r.height * dpr));
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    this.dirtySize = false;
    this.blank = false;
  }

  private setStale(stale: boolean): void {
    if (this.stale === stale) return;
    this.stale = stale;
    this.onStale(stale);
  }

  private toPx([x, y]: readonly number[], frame: DetectionFrame, r: Rect): [number, number] {
    const ux = frame.normalized ? x : x / frame.frameWidth;
    const uy = frame.normalized ? y : y / frame.frameHeight;
    return [r.x + ux * r.w, r.y + uy * r.h];
  }

  private drawZone(zone: DangerZone, frame: DetectionFrame, r: Rect, now: number, pulse: number): void {
    const { ctx } = this;
    const pts = zone.polygon.map((p) => this.toPx(p, frame, r));
    const rgb = ZONE_RGB[zone.level] ?? ZONE_RGB.Danger;
    ctx.beginPath();
    pts.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
    ctx.closePath();
    // Safe zones: steady green fill + solid border, so they never read as a (pulsing, dashed) danger zone.
    const safe = zone.level === 'Safe';
    ctx.fillStyle = `rgba(${rgb},${safe ? '0.12' : (0.1 + pulse * 0.1).toFixed(3)})`;
    ctx.fill();
    ctx.setLineDash(safe ? [] : [8, 6]);
    ctx.lineDashOffset = -now / 40;
    ctx.lineWidth = 1.5;
    ctx.strokeStyle = `rgba(${rgb},0.9)`;
    ctx.stroke();
    ctx.setLineDash([]);
    if (zone.label) {
      const top = pts.reduce((best, p) => (p[1] < best[1] ? p : best), pts[0]);
      this.drawTag(zone.label.toUpperCase(), top[0], top[1] - 4, `rgb(${rgb})`, '#fff', 'above');
    }
  }

  private drawBox(track: Track, r: Rect, pulse: number, now: number): void {
    const { ctx } = this;
    const det = track.det;
    const [ux, uy, uw, uh] = track.box;
    const x = r.x + ux * r.w, y = r.y + uy * r.h, w = uw * r.w, h = uh * r.h;
    if (w < 1 || h < 1) return;

    const isHuman = det.category === 'Human';
    const atRisk = isHuman && det.atRisk;
    const isFire = det.category === 'Fire';
    const color =
      isFire ? OVERLAY_COLORS.fire
      : atRisk ? OVERLAY_COLORS.humanAtRisk
      : isHuman ? OVERLAY_COLORS.human
      : det.category === 'Smoke' ? OVERLAY_COLORS.smoke
      : det.category === 'Danger' ? OVERLAY_COLORS.danger
      : det.category === 'Vehicle' ? OVERLAY_COLORS.vehicle
      : OVERLAY_COLORS.other;
    const emphasis = isFire || atRisk || det.category === 'Danger';

    if (isFire || atRisk) { ctx.fillStyle = rgba(color, 0.1 + pulse * 0.15); ctx.fillRect(x, y, w, h); }
    else if (det.category === 'Smoke') { ctx.fillStyle = rgba(color, 0.1); ctx.fillRect(x, y, w, h); }

    ctx.lineWidth = 1;
    ctx.strokeStyle = rgba(color, emphasis ? 0.75 : 0.45);
    ctx.strokeRect(x + 0.5, y + 0.5, w, h);

    // corner brackets (pulse thicker for fire / humans in danger)
    const len = Math.max(6, Math.min(18, Math.min(w, h) * 0.25));
    ctx.lineWidth = emphasis ? 2 + pulse * 1.5 : 2;
    ctx.strokeStyle = color;
    ctx.beginPath();
    ctx.moveTo(x, y + len); ctx.lineTo(x, y); ctx.lineTo(x + len, y);
    ctx.moveTo(x + w - len, y); ctx.lineTo(x + w, y); ctx.lineTo(x + w, y + len);
    ctx.moveTo(x + w, y + h - len); ctx.lineTo(x + w, y + h); ctx.lineTo(x + w - len, y + h);
    ctx.moveTo(x + len, y + h); ctx.lineTo(x, y + h); ctx.lineTo(x, y + h - len);
    ctx.stroke();

    const age = now - track.born;
    if (age < 600) { // brief ring when a new object appears
      const grow = (age / 600) * 8;
      ctx.lineWidth = 1;
      ctx.strokeStyle = rgba(color, 1 - age / 600);
      ctx.strokeRect(x - grow, y - grow, w + grow * 2, h + grow * 2);
    }

    const name = (det.label || det.category).toUpperCase();
    const label = `${atRisk ? 'IN DANGER · ' : ''}${name} ${Math.round((det.confidence ?? 1) * 100)}%${det.trackId ? ` #${det.trackId}` : ''}`;
    this.drawTag(label, x, y, color, '#04070d', y - 18 < r.y ? 'inside' : 'above');
  }

  private drawTag(text: string, x: number, y: number, bg: string, fg: string, placement: 'above' | 'inside'): void {
    const { ctx } = this;
    ctx.font = LABEL_FONT;
    const w = ctx.measureText(text).width + 10;
    const ty = placement === 'above' ? y - 16 : y;
    ctx.fillStyle = bg;
    ctx.fillRect(x, ty, w, 16);
    ctx.fillStyle = fg;
    ctx.textBaseline = 'middle';
    ctx.fillText(text, x + 5, ty + 8.5);
  }

  private drawPoints(frame: DetectionFrame, r: Rect): void {
    const { ctx } = this;
    ctx.strokeStyle = OVERLAY_COLORS.human;
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    for (const p of frame.humanCoordinates) {
      const [px, py] = this.toPx(p, frame, r);
      ctx.moveTo(px - 5, py); ctx.lineTo(px + 5, py);
      ctx.moveTo(px, py - 5); ctx.lineTo(px, py + 5);
    }
    ctx.stroke();
  }

  /** Low-res grid → tiny canvas; drawImage with smoothing upscales it into a soft heat glow. */
  private buildHeatmap({ rows, cols, values }: Heatmap): HTMLCanvasElement {
    const c = this.heatmap && this.heatmap.width === cols && this.heatmap.height === rows
      ? this.heatmap
      : Object.assign(document.createElement('canvas'), { width: cols, height: rows });
    const hctx = c.getContext('2d')!;
    const img = hctx.createImageData(cols, rows);
    for (let i = 0; i < values.length; i++) {
      const v = values[i];
      const o = i * 4;
      if (v < 0.04) { img.data[o + 3] = 0; continue; }
      const [rr, gg, bb] = heatColor(v);
      img.data[o] = rr; img.data[o + 1] = gg; img.data[o + 2] = bb;
      img.data[o + 3] = Math.round(Math.min(1, v * 1.25) * 230);
    }
    hctx.putImageData(img, 0, 0);
    return c;
  }
}

/** One requestAnimationFrame loop shared by every camera tile (cheaper than one loop per tile). */
@Injectable({ providedIn: 'root' })
export class OverlayLoopService {
  private readonly renderers = new Set<OverlayRenderer>();
  private raf = 0;
  private last = 0;

  register(renderer: OverlayRenderer): void {
    this.renderers.add(renderer);
    if (!this.raf) {
      this.last = performance.now();
      this.raf = requestAnimationFrame(this.loop);
    }
  }

  unregister(renderer: OverlayRenderer): void {
    this.renderers.delete(renderer);
    if (!this.renderers.size && this.raf) { cancelAnimationFrame(this.raf); this.raf = 0; }
  }

  private readonly loop = (now: number): void => {
    const dt = Math.min(0.1, (now - this.last) / 1000);
    this.last = now;
    const ease = 1 - Math.exp(-dt * EASING_RATE);
    for (const r of this.renderers) r.draw(now, ease);
    this.raf = requestAnimationFrame(this.loop);
  };
}
