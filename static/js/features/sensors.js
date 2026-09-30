import { cloneTemplate, relativeTime } from '../core/dom.js';

const STATE_COLORS = { normal: '#10b981', warning: '#f59e0b', alarm: '#ef4444', critical: '#ef4444' };
const HISTORY_POINTS = 60;

function formatValue(sensor) {
  const v = Number(sensor.current_value);
  return sensor.type === 'Temperature' ? v.toFixed(1) : Math.round(v).toString();
}

export class SensorPanel {
  constructor(root, meta) {
    this.root = root;
    this.meta = meta;
    this.cards = new Map();
  }

  load(sensors) {
    const incoming = new Set(sensors.map((s) => s.sensor_id));
    for (const [id, card] of this.cards) {
      if (!incoming.has(id)) { card.node.remove(); this.cards.delete(id); }
    }
    for (const sensor of sensors) this.upsert(sensor, { history: sensor.history });
    this._renderMeta();
  }

  upsert(sensor, { history = null } = {}) {
    let card = this.cards.get(sensor.sensor_id);
    if (!card) {
      const node = cloneTemplate('tpl-sensor-card');
      card = {
        node,
        sensor,
        history: [],
        gauge: node.querySelector('.gauge'),
        value: node.querySelector('.gauge__value'),
        unit: node.querySelector('.gauge__unit'),
        type: node.querySelector('.sensor__type'),
        state: node.querySelector('.sensor__state'),
        zone: node.querySelector('.sensor__zone'),
        id: node.querySelector('.sensor__id'),
        limit: node.querySelector('.sensor__limit'),
        spark: node.querySelector('.sparkline'),
      };
      this.cards.set(sensor.sensor_id, card);
      this.root.querySelector('.empty')?.remove();
      this.root.append(node);
      this._renderMeta();
    }

    if (history) card.history = history.slice(-HISTORY_POINTS).map(Number);
    else if (card.sensor.last_reading_at !== sensor.last_reading_at || !card.history.length) {
      card.history.push(Number(sensor.current_value));
      if (card.history.length > HISTORY_POINTS) card.history.shift();
    }
    card.sensor = sensor;
    this._render(card);
    return card;
  }

  all() {
    return [...this.cards.values()].map((c) => c.sensor);
  }

  states() {
    return new Set(this.all().map((s) => s.state));
  }

  peak(type) {
    return this.all().filter((s) => s.type === type)
      .reduce((best, s) => (!best || s.current_value > best.current_value ? s : best), null);
  }

  refreshTimes() {
    for (const card of this.cards.values()) {
      card.node.title = `Last reading ${relativeTime(card.sensor.last_reading_at)}`;
    }
  }

  _render(card) {
    const s = card.sensor;
    const scaleMax = s.critical_threshold * 1.1;
    const pct = Math.max(0, Math.min(100, (s.current_value / scaleMax) * 100));
    card.node.dataset.state = s.state;
    card.gauge.style.setProperty('--pct', pct.toFixed(1));
    card.value.textContent = formatValue(s);
    card.unit.textContent = s.unit;
    card.type.textContent = s.type === 'Temperature' ? 'Temperature' : 'Smoke';
    card.state.textContent = s.state;
    card.zone.textContent = s.location_zone;
    card.id.textContent = s.sensor_id;
    card.limit.textContent = `${s.threshold}${s.unit}`;
    card.node.setAttribute('aria-label', `${s.type} ${s.sensor_id} in ${s.location_zone}: ${formatValue(s)} ${s.unit}, ${s.state}`);
    this._drawSparkline(card);
  }

  _drawSparkline(card) {
    const canvas = card.spark;
    const data = card.history;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const w = canvas.clientWidth || 160;
    const h = canvas.clientHeight || 28;
    if (canvas.width !== Math.round(w * dpr)) {
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    if (data.length < 2) return;

    const s = card.sensor;
    const max = Math.max(s.threshold * 1.05, ...data);
    const min = Math.min(0, ...data);
    const range = max - min || 1;
    const x = (i) => (i / (HISTORY_POINTS - 1)) * w;
    const y = (v) => h - 2 - ((v - min) / range) * (h - 4);
    const offset = HISTORY_POINTS - data.length;
    const color = STATE_COLORS[s.state] || STATE_COLORS.normal;

    // threshold line
    ctx.setLineDash([3, 3]);
    ctx.strokeStyle = 'rgba(239,68,68,.45)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, y(s.threshold));
    ctx.lineTo(w, y(s.threshold));
    ctx.stroke();
    ctx.setLineDash([]);

    // area + line
    ctx.beginPath();
    data.forEach((v, i) => (i ? ctx.lineTo(x(i + offset), y(v)) : ctx.moveTo(x(i + offset), y(v))));
    ctx.strokeStyle = color;
    ctx.lineWidth = 1.5;
    ctx.lineJoin = 'round';
    ctx.stroke();
    ctx.lineTo(x(data.length - 1 + offset), h);
    ctx.lineTo(x(offset), h);
    ctx.closePath();
    const grad = ctx.createLinearGradient(0, 0, 0, h);
    grad.addColorStop(0, `${color}55`);
    grad.addColorStop(1, `${color}00`);
    ctx.fillStyle = grad;
    ctx.fill();
  }

  _renderMeta() {
    const count = this.cards.size;
    this.meta.textContent = `${count} node${count === 1 ? '' : 's'}`;
  }
}
