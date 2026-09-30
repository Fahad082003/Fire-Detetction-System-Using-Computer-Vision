import { createApi } from './core/api.js';
import { $, $$, rafThrottle, toast } from './core/dom.js';
import { LiveSocket } from './core/socket.js';
import { AudioAlarm } from './features/alarm.js';
import { EventLog, IncidentTable, SEVERITY_RANK } from './features/incidents.js';
import { OverlayEngine } from './features/overlay.js';
import { PushController } from './features/push.js';
import { SensorPanel } from './features/sensors.js';
import { SprinklerPanel } from './features/sprinklers.js';
import { VideoWall } from './features/video.js';

const config = JSON.parse(document.getElementById('app-config').textContent);
const api = createApi(config.apiBase);
const socket = new LiveSocket(config.wsPath);

const overlay = new OverlayEngine({ showHeatmap: $('#toggle-heatmap').checked });
const video = new VideoWall($('#video-grid'), overlay, { streamUrlFor: api.cameraStreamUrl });
const sensors = new SensorPanel($('#sensor-grid'), $('#sensor-meta'));
const sprinklers = new SprinklerPanel($('#sprinkler-list'), $('#master-sprinkler'), api);
const incidents = new IncidentTable($('#incident-body'), api);
const eventLog = new EventLog($('#event-log'));
const alarm = new AudioAlarm($('#btn-mute'));
const push = new PushController(api, $('#btn-push'));

// ------------------------------------------------------------------ system status + KPIs
const LEVEL_LABEL = {
  nominal: () => 'ALL SYSTEMS NOMINAL',
  elevated: (n) => `ELEVATED RISK · ${n} OPEN`,
  critical: (n) => `CRITICAL ALERT · ${n} OPEN`,
};

function computeLevel() {
  const top = incidents.maxOpenRank();
  const states = sensors.states();
  if (top >= SEVERITY_RANK.High || states.has('alarm') || states.has('critical')) return 'critical';
  if (top >= SEVERITY_RANK.Low || states.has('warning')) return 'elevated';
  return 'nominal';
}

function setKpi(id, value, hint, tone) {
  const card = document.getElementById(id);
  card.dataset.tone = tone;
  card.querySelector('.kpi__value').textContent = value;
  card.querySelector('.kpi__hint').textContent = hint;
}

function sensorTone(sensor) {
  if (!sensor) return 'neutral';
  return { normal: 'ok', warning: 'warn' }[sensor.state] || 'danger';
}

const refreshDashboard = rafThrottle(() => {
  const open = incidents.openIncidents();
  const level = computeLevel();
  const pill = $('#system-status');
  pill.dataset.level = level;
  pill.querySelector('.status-pill__label').textContent = LEVEL_LABEL[level](open.length);
  document.body.dataset.level = level;

  const critical = open.filter((i) => i.severity === 'Critical').length;
  const topRank = incidents.maxOpenRank();
  setKpi('kpi-incidents', open.length, critical ? `${critical} critical` : open.length ? 'monitoring' : 'none open',
    topRank >= SEVERITY_RANK.High ? 'danger' : topRank >= 0 ? 'warn' : 'ok');

  const { humans, atRisk, liveFeeds } = video.liveTotals();
  setKpi('kpi-humans', humans, `${atRisk} at risk · ${liveFeeds} live feed${liveFeeds === 1 ? '' : 's'}`,
    atRisk > 0 ? 'danger' : humans > 0 ? 'ok' : 'neutral');

  const temp = sensors.peak('Temperature');
  setKpi('kpi-temp', temp ? `${temp.current_value.toFixed(1)}°C` : '—',
    temp ? `${temp.location_zone} · ${temp.sensor_id}` : 'no sensors', sensorTone(temp));

  const smoke = sensors.peak('Smoke');
  setKpi('kpi-smoke', smoke ? `${Math.round(smoke.current_value)} ppm` : '—',
    smoke ? `${smoke.location_zone} · ${smoke.sensor_id}` : 'no sensors', sensorTone(smoke));
});
video.onChange = refreshDashboard;

function populateZones() {
  const select = $('#dispatch-zone');
  const current = select.value;
  const zones = [...new Set([...video.zones(), ...sprinklers.zones(), ...sensors.all().map((s) => s.location_zone)])].sort();
  select.replaceChildren(...zones.map((z) => new Option(z, z)));
  if (zones.includes(current)) select.value = current;
}

// ------------------------------------------------------------------ state application
function applySnapshot(snapshot) {
  video.setCameras(snapshot.cameras);
  sensors.load(snapshot.sensors);
  sprinklers.load(snapshot.sprinklers);
  incidents.load(snapshot.incidents);
  populateZones();
  refreshDashboard();
}

function handleIncident(incident, action) {
  incidents.upsert(incident, action);
  refreshDashboard();
  if (action !== 'created' && action !== 'escalated') return;
  const rank = SEVERITY_RANK[incident.severity] ?? 0;
  if (rank >= SEVERITY_RANK.High) {
    toast(`${incident.severity.toUpperCase()} — Incident #${incident.id}`, incident.message || incident.location, 'critical', 10000);
    alarm.play(incident.severity);
    push.notifyLocal(incident);
  } else {
    toast(`Incident #${incident.id} (${incident.severity})`, incident.message || incident.location, 'warning');
  }
}

// ------------------------------------------------------------------ socket wiring
const on = (type, handler) => socket.addEventListener(type, (e) => handler(e.detail));

on('state', ({ state, attempt }) => {
  const indicator = $('#ws-status');
  indicator.dataset.state = state;
  indicator.querySelector('.conn__label').textContent = {
    open: 'Live', connecting: attempt ? `Reconnecting (${attempt})` : 'Connecting', closed: 'Offline',
  }[state] || state;
  if (state === 'open') eventLog.add({ level: 'success', message: 'Realtime link established' });
  if (state === 'closed') eventLog.add({ level: 'warning', message: 'Realtime link lost — retrying' });
});

on('snapshot', ({ data }) => applySnapshot(data));
on('detection', ({ data }) => video.onDetection(data));
on('telemetry', ({ data }) => { sensors.upsert(data); refreshDashboard(); });
on('sprinkler', ({ data }) => { sprinklers.upsert(data); refreshDashboard(); });
on('incident', ({ data, action }) => handleIncident(data, action));
on('log', ({ data, ts }) => eventLog.add({ ...data, ts }));
on('notification', ({ data, ts }) => {
  const outcome = data.ok ? `delivered to ${data.delivered}` : `failed${data.detail ? `: ${data.detail}` : ''}`;
  eventLog.add({ level: data.ok ? 'notify' : 'warning', message: `${data.channel.toUpperCase()} alert for incident #${data.incident_id} ${outcome}`, ts });
});
on('error', (msg) => console.warn('[ws] server error', msg));

// ------------------------------------------------------------------ controls
$$('[data-filter]').forEach((btn) => btn.addEventListener('click', () => {
  $$('[data-filter]').forEach((b) => { b.classList.toggle('is-active', b === btn); b.setAttribute('aria-selected', String(b === btn)); });
  video.setFilter(btn.dataset.filter);
}));

$$('[data-incident-filter]').forEach((btn) => btn.addEventListener('click', () => {
  $$('[data-incident-filter]').forEach((b) => { b.classList.toggle('is-active', b === btn); b.setAttribute('aria-selected', String(b === btn)); });
  incidents.setFilter(btn.dataset.incidentFilter);
}));

$('#toggle-heatmap').addEventListener('change', (e) => { overlay.showHeatmap = e.target.checked; });
$('#btn-clear-log').addEventListener('click', () => eventLog.clear());

const dispatchDialog = $('#dispatch-dialog');
const dispatchForm = $('#dispatch-form');
$('#btn-dispatch').addEventListener('click', () => {
  populateZones();
  dispatchForm.reset();
  dispatchDialog.showModal();
});
dispatchDialog.querySelector('[data-close]').addEventListener('click', () => dispatchDialog.close());
dispatchForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = new FormData(dispatchForm);
  const submit = dispatchForm.querySelector('[type="submit"]');
  submit.disabled = true;
  try {
    const incident = await api.dispatch({
      zone: form.get('zone'),
      severity: form.get('severity'),
      message: form.get('message').trim(),
      activate_sprinklers: form.get('activate_sprinklers') === 'on',
    });
    dispatchDialog.close();
    toast('Emergency dispatch sent', `Incident #${incident.id} · ${incident.zone}`, 'critical');
  } catch (err) {
    toast('Dispatch failed', err.message, 'error');
  } finally {
    submit.disabled = false;
  }
});

// ------------------------------------------------------------------ clocks
const clock = $('#clock');
const tickClock = () => { clock.textContent = new Date().toLocaleTimeString([], { hour12: false }); };
tickClock();
setInterval(tickClock, 1000);
setInterval(() => { sprinklers.refreshTimes(); sensors.refreshTimes(); }, 15000);

// ------------------------------------------------------------------ boot
api.snapshot().then(applySnapshot).catch((err) => {
  eventLog.add({ level: 'warning', message: `Initial snapshot failed: ${err.message}` });
});
socket.connect();
push.init();
