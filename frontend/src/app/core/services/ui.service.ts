import { Injectable, signal } from '@angular/core';

export type ToastTone = 'info' | 'success' | 'warning' | 'critical' | 'error';
export interface Toast { id: number; title: string; message: string; tone: ToastTone; }

/** Small notification bubbles in the bottom-right corner. */
@Injectable({ providedIn: 'root' })
export class ToastService {
  private nextId = 1;
  readonly toasts = signal<Toast[]>([]);

  show(title: string, message = '', tone: ToastTone = 'info', timeoutMs = 6000): void {
    const toast: Toast = { id: this.nextId++, title, message, tone };
    this.toasts.update((list) => [toast, ...list].slice(0, 5));
    if (timeoutMs) setTimeout(() => this.dismiss(toast.id), timeoutMs);
  }

  dismiss(id: number): void {
    this.toasts.update((list) => list.filter((t) => t.id !== id));
  }
}

export interface ConfirmRequest { title: string; text: string; confirmLabel: string; resolve: (ok: boolean) => void; }

/** Promise-based confirmation dialog, rendered once by ConfirmDialogComponent. */
@Injectable({ providedIn: 'root' })
export class ConfirmService {
  readonly request = signal<ConfirmRequest | null>(null);

  ask(title: string, text: string, confirmLabel = 'Confirm'): Promise<boolean> {
    return new Promise((resolve) => this.request.set({ title, text, confirmLabel, resolve }));
  }

  answer(ok: boolean): void {
    this.request()?.resolve(ok);
    this.request.set(null);
  }
}

/** Two-tone audible alarm via Web Audio (unlocked on first click because of browser autoplay rules). */
@Injectable({ providedIn: 'root' })
export class AlarmService {
  private ctx: AudioContext | null = null;
  private lastPlayed = 0;
  readonly muted = signal(this.readMuted());

  constructor() {
    const unlock = () => { this.ensureContext(); window.removeEventListener('pointerdown', unlock); };
    window.addEventListener('pointerdown', unlock);
  }

  toggleMute(): void {
    this.muted.update((m) => !m);
    try { localStorage.setItem('sentinel.muted', this.muted() ? '1' : '0'); } catch { /* ignore */ }
  }

  play(critical: boolean): void {
    if (this.muted()) return;
    const now = performance.now();
    if (now - this.lastPlayed < 4000) return;
    const ctx = this.ensureContext();
    if (!ctx || ctx.state !== 'running') return;
    this.lastPlayed = now;

    const [hi, lo, beeps] = critical ? [988, 740, 4] : [784, 587, 2];
    const t0 = ctx.currentTime + 0.02;
    for (let i = 0; i < beeps; i++) {
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      const t = t0 + i * 0.22;
      osc.type = 'square';
      osc.frequency.setValueAtTime(i % 2 ? lo : hi, t);
      gain.gain.setValueAtTime(0.0001, t);
      gain.gain.exponentialRampToValueAtTime(0.08, t + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.18);
      osc.connect(gain).connect(ctx.destination);
      osc.start(t);
      osc.stop(t + 0.2);
    }
  }

  private ensureContext(): AudioContext | null {
    if (typeof AudioContext === 'undefined') return null;
    this.ctx ??= new AudioContext();
    if (this.ctx.state === 'suspended') this.ctx.resume().catch(() => undefined);
    return this.ctx;
  }

  private readMuted(): boolean {
    try { return localStorage.getItem('sentinel.muted') === '1'; } catch { return false; }
  }
}
