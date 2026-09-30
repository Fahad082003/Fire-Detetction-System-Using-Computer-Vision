/**
 * OverlayEngine — one requestAnimationFrame loop drives every camera's <canvas>.
 *
 * Detection frames arrive at stream rate over the WebSocket; the engine keeps the
 * latest frame per camera, eases boxes toward their new positions (track-aware),
 * maps source-frame coordinates onto the letterboxed media rect, and fades frames
 * out when the producer goes quiet. Off-screen canvases are skipped entirely.
 */

const COLORS = {
  human: '#22d3ee',
  fire: '#ef4444',
  smoke: '#94a3b8',
  danger: '#f43f5e',
  vehicle: '#a78bfa',
  other: '#e2e8f0',
};
const ZONE_COLORS = { warning: '245,158,11', danger: '239,68,68', critical: '220,38,38' };
const LABEL_FONT = '600 11px "JetBrains Mono", "Cascadia Code", Consolas, monospace';
const STALE_MS = 1500;
const EXPIRE_MS = 4500;
const EASING_RATE = 14;

const rgbaCache = new Map();
function rgba(hex, alpha) {
  const key = `${hex}|${alpha.toFixed(2)}`;
  let value = rgbaCache.get(key);
  if (!value) {
    const n = parseInt(hex.slice(1), 16);
    value = `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha.toFixed(2)})`;
    if (rgbaCache.size > 512) rgbaCache.clear();
    rgbaCache.set(key, value);
  }
  return value;
}

function heatColor(v) {
  // transparent → amber → red → near-white core
  if (v < 0.5) {
    const t = v / 0.5;
    return [250, Math.round(204 - 40 * t), Math.round(21 * (1 - t))];
  }
  if (v < 0.85) {
    const t = (v - 0.5) / 0.35;
    return [Math.round(250 - 11 * t), Math.round(164 - 96 * t), Math.round(68 * t)];
  }
  const t = (v - 0.85) / 0.15;
  return [Math.round(239 + 16 * t), Math.round(68 + 170 * t), Math.round(68 + 150 * t)];
}

export class OverlayEngine {
  constructor({ showHeatmap = true } = {}) {
    this.layers = new Map();
    this.showHeatmap = showHeatmap;
    this._byStage = new WeakMap();
    this._raf = 0;
    this._last = performance.now();
    this._loop = this._loop.bind(this);

    this._resizeObserver = new ResizeObserver((entries) => {
      for (const entry of entries) {
        const layer = this._byStage.get(entry.target);
        if (layer) layer.dirtySize = true;
      }
    });
    this._intersectionObserver = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        const layer = this._byStage.get(entry.target);
        if (layer) layer.visible = entry.isIntersecting;
      }
    });
  }

  attach(id, { stage, canvas, media = null, onStale = null }) {
    this.detach(id);
    const layer = {
      id, stage, canvas, media, onStale,
      ctx: canvas.getContext('2d'),
      frame: null,
      receivedAt: 0,
      tracks: new Map(),
      heatmap: null,
      cssW: 0,
      cssH: 0,
      dirtySize: true,
      visible: true,
      blank: true,
      stale: true,
    };
    this.layers.set(id, layer);
    this._byStage.set(stage, layer);
    this._resizeObserver.observe(stage);
    this._intersectionObserver.observe(stage);
    this.start();
    return layer;
  }

  detach(id) {
    const layer = this.layers.get(id);
    if (!layer) return;
    this._resizeObserver.unobserve(layer.stage);
    this._intersectionObserver.unobserve(layer.stage);
    this.layers.delete(id);
    if (!this.layers.size) this.stop();
  }

  setMedia(id, media) {
    const layer = this.layers.get(id);
    if (layer) layer.media = media;
  }

  update(id, frame) {
    const layer = this.layers.get(id);
    if (!layer) return;
    const now = performance.now();
    layer.frame = frame;
    layer.receivedAt = now;

    const seen = new Set();
    frame.detections.forEach((det, index) => {
      const key = det.track_id ? `t:${det.track_id}` : `${det.category}:${index}`;
      seen.add(key);
      const target = this._toUnitBox(det.bbox, frame);
      const track = layer.tracks.get(key);
      if (track) {
        track.target = target;
        track.det = det;
      } else {
        layer.tracks.set(key, { box: target.slice(), target, det, born: now });
      }
    });
    for (const key of layer.tracks.keys()) {
      if (!seen.has(key)) layer.tracks.delete(key);
    }

    layer.heatmap = frame.heatmap ? this._buildHeatmap(frame.heatmap, layer.heatmap) : null;
  }

  clear(id) {
    const layer = this.layers.get(id);
    if (!layer) return;
    layer.frame = null;
    layer.tracks.clear();
    layer.heatmap = null;
  }

  start() {
    if (!this._raf) {
      this._last = performance.now();
      this._raf = requestAnimationFrame(this._loop);
    }
  }

  stop() {
    cancelAnimationFrame(this._raf);
    this._raf = 0;
  }

  // ---------------------------------------------------------------- loop
  _loop(now) {
    const dt = Math.min(0.1, (now - this._last) / 1000);
    this._last = now;
    const ease = 1 - Math.exp(-dt * EASING_RATE);

    for (const layer of this.layers.values()) {
      if (!layer.visible) continue;
      if (layer.dirtySize) this._resize(layer);
      this._draw(layer, now, ease);
    }
    this._raf = requestAnimationFrame(this._loop);
  }

  _resize(layer) {
    const rect = layer.stage.getBoundingClientRect();
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    layer.cssW = rect.width;
    layer.cssH = rect.height;
    layer.canvas.width = Math.max(1, Math.round(rect.width * dpr));
    layer.canvas.height = Math.max(1, Math.round(rect.height * dpr));
    layer.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    layer.dirtySize = false;
    layer.blank = false; // canvas resize clears pixels; force one clean pass
  }

  _setStale(layer, stale) {
    if (layer.stale === stale) return;
    layer.stale = stale;
    layer.onStale?.(stale);
  }

  _draw(layer, now, ease) {
    const { ctx, cssW: W, cssH: H } = layer;
    const frame = layer.frame;

    if (!frame) {
      if (!layer.blank) {
        ctx.clearRect(0, 0, W, H);
        layer.blank = true;
      }
      this._setStale(layer, true);
      return;
    }

    const age = now - layer.receivedAt;
    if (age > EXPIRE_MS) {
      this.clear(layer.id);
      ctx.clearRect(0, 0, W, H);
      layer.blank = true;
      this._setStale(layer, true);
      return;
    }
    this._setStale(layer, age > STALE_MS);

    ctx.clearRect(0, 0, W, H);
    layer.blank = false;

    const alpha = age > STALE_MS ? Math.max(0, 1 - (age - STALE_MS) / (EXPIRE_MS - STALE_MS)) : 1;
    const rect = this._contentRect(layer);
    const pulse = 0.5 + 0.5 * Math.sin(now / 170);

    if (this.showHeatmap && layer.heatmap) {
      ctx.globalAlpha = alpha * 0.55;
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = 'high';
      ctx.drawImage(layer.heatmap, rect.x, rect.y, rect.w, rect.h);
    }

    ctx.globalAlpha = alpha;
    for (const zone of frame.danger_zones) this._drawZone(ctx, zone, frame, rect, now, pulse);

    for (const track of layer.tracks.values()) {
      const { box, target } = track;
      for (let i = 0; i < 4; i += 1) box[i] += (target[i] - box[i]) * ease;
      this._drawBox(ctx, track, rect, pulse, now);
    }

    if (frame.human_coordinates.length) this._drawPoints(ctx, frame, rect);
    ctx.globalAlpha = 1;
  }

  // ---------------------------------------------------------------- geometry
  _toUnitBox([x, y, w, h], frame) {
    if (frame.normalized) return [x, y, w, h];
    const fw = frame.frame_width;
    const fh = frame.frame_height;
    return [x / fw, y / fh, w / fw, h / fh];
  }

  _toUnitPoint([x, y], frame) {
    return frame.normalized ? [x, y] : [x / frame.frame_width, y / frame.frame_height];
  }

  /** Rect occupied by the media under `object-fit: contain`, in CSS pixels. */
  _contentRect(layer) {
    const W = layer.cssW;
    const H = layer.cssH;
    const media = layer.media;
    let sw = media ? media.videoWidth || media.naturalWidth || 0 : 0;
    let sh = media ? media.videoHeight || media.naturalHeight || 0 : 0;
    if (!sw || !sh) {
      if (layer.frame && !layer.frame.normalized) {
        sw = layer.frame.frame_width;
        sh = layer.frame.frame_height;
      } else {
        return { x: 0, y: 0, w: W, h: H };
      }
    }
    const scale = Math.min(W / sw, H / sh);
    const w = sw * scale;
    const h = sh * scale;
    return { x: (W - w) / 2, y: (H - h) / 2, w, h };
  }

  // ---------------------------------------------------------------- primitives
  _drawZone(ctx, zone, frame, rect, now, pulse) {
    const points = zone.polygon.map((p) => {
      const [ux, uy] = this._toUnitPoint(p, frame);
      return [rect.x + ux * rect.w, rect.y + uy * rect.h];
    });
    const rgb = ZONE_COLORS[zone.level] || ZONE_COLORS.danger;

    ctx.beginPath();
    points.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
    ctx.closePath();
    ctx.fillStyle = `rgba(${rgb},${(0.1 + pulse * 0.1).toFixed(3)})`;
    ctx.fill();
    ctx.setLineDash([8, 6]);
    ctx.lineDashOffset = -now / 40;
    ctx.lineWidth = 1.5;
    ctx.strokeStyle = `rgba(${rgb},0.9)`;
    ctx.stroke();
    ctx.setLineDash([]);

    if (zone.label) {
      const [lx, ly] = points.reduce((best, p) => (p[1] < best[1] ? p : best), points[0]);
      this._drawTag(ctx, zone.label.toUpperCase(), lx, ly - 4, `rgb(${rgb})`, '#fff', 'above');
    }
  }

  _drawBox(ctx, track, rect, pulse, now) {
    const det = track.det;
    const [ux, uy, uw, uh] = track.box;
    const x = rect.x + ux * rect.w;
    const y = rect.y + uy * rect.h;
    const w = uw * rect.w;
    const h = uh * rect.h;
    if (w < 1 || h < 1) return;

    const atRisk = det.at_risk || det.category === 'danger';
    const isFire = det.category === 'fire';
    const color = atRisk ? COLORS.danger : COLORS[det.category] || COLORS.other;
    const emphasis = atRisk || isFire;

    // body
    if (isFire) {
      ctx.fillStyle = rgba(color, 0.12 + pulse * 0.12);
      ctx.fillRect(x, y, w, h);
    } else if (det.category === 'smoke') {
      ctx.fillStyle = rgba(color, 0.1);
      ctx.fillRect(x, y, w, h);
    }
    ctx.lineWidth = 1;
    ctx.strokeStyle = rgba(color, emphasis ? 0.7 : 0.45);
    ctx.strokeRect(x + 0.5, y + 0.5, w, h);

    // corner brackets
    const len = Math.max(6, Math.min(18, Math.min(w, h) * 0.25));
    ctx.lineWidth = emphasis ? 2 + pulse * 1.2 : 2;
    ctx.strokeStyle = color;
    ctx.beginPath();
    ctx.moveTo(x, y + len); ctx.lineTo(x, y); ctx.lineTo(x + len, y);
    ctx.moveTo(x + w - len, y); ctx.lineTo(x + w, y); ctx.lineTo(x + w, y + len);
    ctx.moveTo(x + w, y + h - len); ctx.lineTo(x + w, y + h); ctx.lineTo(x + w - len, y + h);
    ctx.moveTo(x + len, y + h); ctx.lineTo(x, y + h); ctx.lineTo(x, y + h - len);
    ctx.stroke();

    // new-track flash
    const age = now - track.born;
    if (age < 600) {
      ctx.lineWidth = 1;
      ctx.strokeStyle = rgba(color, 1 - age / 600);
      const grow = (age / 600) * 8;
      ctx.strokeRect(x - grow, y - grow, w + grow * 2, h + grow * 2);
    }

    const name = (det.label || det.category).toUpperCase();
    const conf = Math.round((det.confidence ?? 1) * 100);
    const label = `${atRisk && det.category === 'human' ? 'AT RISK · ' : ''}${name} ${conf}%${det.track_id ? ` #${det.track_id}` : ''}`;
    this._drawTag(ctx, label, x, y, color, '#04070d', y - 18 < rect.y ? 'inside' : 'above');
  }

  _drawTag(ctx, text, x, y, bg, fg, placement) {
    ctx.font = LABEL_FONT;
    const padX = 5;
    const h = 16;
    const w = ctx.measureText(text).width + padX * 2;
    const ty = placement === 'above' ? y - h : y;
    ctx.fillStyle = bg;
    ctx.fillRect(x, ty, w, h);
    ctx.fillStyle = fg;
    ctx.textBaseline = 'middle';
    ctx.fillText(text, x + padX, ty + h / 2 + 0.5);
  }

  _drawPoints(ctx, frame, rect) {
    ctx.strokeStyle = COLORS.human;
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    for (const p of frame.human_coordinates) {
      const [ux, uy] = this._toUnitPoint(p, frame);
      const px = rect.x + ux * rect.w;
      const py = rect.y + uy * rect.h;
      ctx.moveTo(px - 5, py); ctx.lineTo(px + 5, py);
      ctx.moveTo(px, py - 5); ctx.lineTo(px, py + 5);
    }
    ctx.stroke();
  }

  _buildHeatmap({ rows, cols, values }, existing) {
    const canvas = existing && existing.width === cols && existing.height === rows
      ? existing
      : Object.assign(document.createElement('canvas'), { width: cols, height: rows });
    const ctx = canvas.getContext('2d');
    const image = ctx.createImageData(cols, rows);
    const px = image.data;
    for (let i = 0; i < values.length; i += 1) {
      const v = values[i];
      const o = i * 4;
      if (v < 0.04) { px[o + 3] = 0; continue; }
      const [r, g, b] = heatColor(v);
      px[o] = r; px[o + 1] = g; px[o + 2] = b;
      px[o + 3] = Math.round(Math.min(1, v * 1.25) * 230);
    }
    ctx.putImageData(image, 0, 0);
    return canvas;
  }
}
