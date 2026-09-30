import { el, formatDateTime, formatTime, toast } from '../core/dom.js';

export const SEVERITY_RANK = { Low: 0, Medium: 1, High: 2, Critical: 3 };
const MAX_ROWS = 200;
const MAX_LOG = 250;
const SOURCE_LABEL = { vision: 'AI Vision', sensor: 'Sensor', manual: 'Manual' };

export class IncidentTable {
  constructor(tbody, api) {
    this.tbody = tbody;
    this.api = api;
    this.incidents = new Map();
    this.filter = 'open';

    tbody.addEventListener('click', async (e) => {
      const btn = e.target.closest('[data-resolve]');
      if (!btn) return;
      btn.disabled = true;
      try {
        this.upsert(await this.api.resolveIncident(btn.dataset.resolve), 'resolved');
      } catch (err) {
        btn.disabled = false;
        toast('Could not resolve incident', err.message, 'error');
      }
    });
  }

  load(incidents) {
    this.incidents.clear();
    for (const i of incidents) this.incidents.set(i.id, i);
    this.render();
  }

  upsert(incident, action = 'updated') {
    const isNew = !this.incidents.has(incident.id);
    this.incidents.set(incident.id, incident);
    this._trim();
    this.render(isNew || action === 'escalated' ? incident.id : null);
  }

  setFilter(filter) {
    this.filter = filter;
    this.render();
  }

  openIncidents() {
    return [...this.incidents.values()].filter((i) => !i.resolved);
  }

  maxOpenRank() {
    return this.openIncidents().reduce((max, i) => Math.max(max, SEVERITY_RANK[i.severity] ?? -1), -1);
  }

  zones() {
    return [...this.incidents.values()].map((i) => i.zone);
  }

  render(flashId = null) {
    const rows = [...this.incidents.values()]
      .filter((i) => this.filter === 'all' || !i.resolved)
      .sort((a, b) => new Date(b.timestamp) - new Date(a.timestamp));

    if (!rows.length) {
      this.tbody.replaceChildren(el('tr', { class: 'table__empty' },
        el('td', { colspan: 10, text: this.filter === 'open' ? 'No open incidents.' : 'No incidents recorded.' })));
      return;
    }

    const fragment = document.createDocumentFragment();
    for (const incident of rows) {
      const row = this._row(incident);
      if (incident.id === flashId) {
        row.classList.remove('is-new');
        void row.offsetWidth; // restart animation
        row.classList.add('is-new');
      }
      fragment.append(row);
    }
    this.tbody.replaceChildren(fragment);
  }

  _row(i) {
    const row = el('tr', { dataset: { severity: i.severity, resolved: String(i.resolved) } },
      el('td', { class: 'num', text: i.id }),
      el('td', { text: formatDateTime(i.timestamp), title: new Date(i.timestamp).toLocaleString() }),
      el('td', {}, el('span', { class: `sev sev--${i.severity}`, text: i.severity })),
      el('td', { class: 'loc', text: i.location, title: i.message || '' }),
      el('td', { text: SOURCE_LABEL[i.source] || i.source }),
      el('td', { class: 'num', text: i.fire_detected ? (i.fire_severity != null ? `${Math.round(i.fire_severity * 100)}%` : 'Yes') : '—' }),
      el('td', { class: 'num', text: i.human_count }),
      el('td', { class: 'num', text: i.danger_count }),
      el('td', {}, el('span', { class: `state state--${i.resolved ? 'resolved' : 'open'}`, text: i.resolved ? 'Resolved' : 'Open' })),
      el('td', {}, i.resolved ? null : el('button', {
        type: 'button', class: 'btn btn--sm btn--success', 'data-resolve': i.id, text: 'Resolve',
      })));
    return row;
  }

  _trim() {
    if (this.incidents.size <= MAX_ROWS) return;
    const oldestResolved = [...this.incidents.values()]
      .filter((i) => i.resolved)
      .sort((a, b) => new Date(a.timestamp) - new Date(b.timestamp));
    while (this.incidents.size > MAX_ROWS && oldestResolved.length) this.incidents.delete(oldestResolved.shift().id);
  }
}

export class EventLog {
  constructor(list) {
    this.list = list;
  }

  add({ level = 'info', message, zone = null, ts = null }) {
    const time = ts ? new Date(ts) : new Date();
    const item = el('li', { dataset: { level } },
      el('time', { datetime: time.toISOString(), text: formatTime(time) }),
      el('span', { class: 'dot' }),
      el('span', { class: 'msg' }, message, zone ? el('span', { class: 'zone', text: zone }) : null));
    this.list.prepend(item);
    while (this.list.children.length > MAX_LOG) this.list.lastElementChild.remove();
  }

  clear() {
    this.list.replaceChildren();
  }
}
