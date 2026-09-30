const STORAGE_KEY = 'sentinel.muted';
const MIN_INTERVAL_MS = 4000;

/** Audible two-tone alarm via Web Audio. Unlocks on the first user gesture (autoplay policy). */
export class AudioAlarm {
  constructor(button) {
    this.button = button;
    this.ctx = null;
    this.lastPlayed = 0;
    try { this.muted = localStorage.getItem(STORAGE_KEY) === '1'; } catch { this.muted = false; }
    this._render();

    button.addEventListener('click', () => {
      this.muted = !this.muted;
      try { localStorage.setItem(STORAGE_KEY, this.muted ? '1' : '0'); } catch { /* storage unavailable */ }
      this._render();
    });
    const unlock = () => {
      this._ensureContext();
      window.removeEventListener('pointerdown', unlock);
      window.removeEventListener('keydown', unlock);
    };
    window.addEventListener('pointerdown', unlock);
    window.addEventListener('keydown', unlock);
  }

  play(severity = 'Critical') {
    if (this.muted) return;
    const now = performance.now();
    if (now - this.lastPlayed < MIN_INTERVAL_MS) return;
    const ctx = this._ensureContext();
    if (!ctx || ctx.state !== 'running') return;
    this.lastPlayed = now;

    const [hi, lo, beeps] = severity === 'Critical' ? [988, 740, 4] : [784, 587, 2];
    const t0 = ctx.currentTime + 0.02;
    for (let i = 0; i < beeps; i += 1) {
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = 'square';
      osc.frequency.setValueAtTime(i % 2 ? lo : hi, t0 + i * 0.22);
      gain.gain.setValueAtTime(0.0001, t0 + i * 0.22);
      gain.gain.exponentialRampToValueAtTime(0.08, t0 + i * 0.22 + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, t0 + i * 0.22 + 0.18);
      osc.connect(gain).connect(ctx.destination);
      osc.start(t0 + i * 0.22);
      osc.stop(t0 + i * 0.22 + 0.2);
    }
  }

  _ensureContext() {
    const Ctor = window.AudioContext || window.webkitAudioContext;
    if (!Ctor) return null;
    if (!this.ctx) this.ctx = new Ctor();
    if (this.ctx.state === 'suspended') this.ctx.resume().catch(() => {});
    return this.ctx;
  }

  _render() {
    this.button.setAttribute('aria-pressed', String(this.muted));
    this.button.title = this.muted ? 'Audible alarm muted' : 'Audible alarm on';
  }
}
