import { Component, ElementRef, effect, input, viewChild } from '@angular/core';
import { HISTORY_POINTS } from '../../../core/services/signalr.service';

/** Tiny line chart of the last readings, with the safety threshold as a dashed red line. */
@Component({
  selector: 'app-sparkline',
  template: `<canvas #canvas class="block h-7 w-full" aria-hidden="true"></canvas>`,
})
export class SparklineComponent {
  readonly values = input.required<number[]>();
  readonly threshold = input.required<number>();
  readonly color = input('#10b981');

  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');

  constructor() {
    effect(() => this.draw(this.canvas().nativeElement, this.values(), this.threshold(), this.color(), HISTORY_POINTS));
  }

  private draw(canvas: HTMLCanvasElement, data: number[], threshold: number, color: string, points: number): void {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const w = canvas.clientWidth || 160;
    const h = canvas.clientHeight || 28;
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    if (data.length < 2) return;

    const max = Math.max(threshold * 1.05, ...data);
    const min = Math.min(0, ...data);
    const range = max - min || 1;
    const offset = points - data.length;
    const x = (i: number) => ((i + offset) / (points - 1)) * w;
    const y = (v: number) => h - 2 - ((v - min) / range) * (h - 4);

    ctx.setLineDash([3, 3]);
    ctx.strokeStyle = 'rgba(239,68,68,.5)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, y(threshold));
    ctx.lineTo(w, y(threshold));
    ctx.stroke();
    ctx.setLineDash([]);

    ctx.beginPath();
    data.forEach((v, i) => (i ? ctx.lineTo(x(i), y(v)) : ctx.moveTo(x(i), y(v))));
    ctx.strokeStyle = color;
    ctx.lineWidth = 1.5;
    ctx.lineJoin = 'round';
    ctx.stroke();
    ctx.lineTo(x(data.length - 1), h);
    ctx.lineTo(x(0), h);
    ctx.closePath();
    const grad = ctx.createLinearGradient(0, 0, 0, h);
    grad.addColorStop(0, `${color}55`);
    grad.addColorStop(1, `${color}00`);
    ctx.fillStyle = grad;
    ctx.fill();
  }
}
