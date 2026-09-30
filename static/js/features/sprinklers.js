import { cloneTemplate, confirmDialog, relativeTime, toast } from '../core/dom.js';

export class SprinklerPanel {
  constructor(root, masterSwitch, api) {
    this.root = root;
    this.master = masterSwitch;
    this.api = api;
    this.items = new Map();

    root.addEventListener('change', (e) => {
      if (!e.target.matches('.sprinkler__toggle')) return;
      const item = this._itemFor(e.target);
      if (item) this._toggle(item, e.target.checked);
    });
    root.addEventListener('click', (e) => {
      const btn = e.target.closest('[data-mode]');
      if (!btn) return;
      const item = this._itemFor(btn);
      if (item && btn.dataset.mode !== item.sprinkler.mode) this._setMode(item, btn.dataset.mode);
    });
    masterSwitch.addEventListener('change', () => this._master(masterSwitch.checked));
  }

  load(sprinklers) {
    const incoming = new Set(sprinklers.map((s) => s.device_id));
    for (const [id, item] of this.items) {
      if (!incoming.has(id)) { item.node.remove(); this.items.delete(id); }
    }
    for (const s of sprinklers) this.upsert(s);
    this._syncMaster();
  }

  upsert(sprinkler) {
    let item = this.items.get(sprinkler.device_id);
    if (!item) {
      const node = cloneTemplate('tpl-sprinkler-item');
      node.dataset.deviceId = sprinkler.device_id;
      item = {
        node,
        sprinkler,
        toggle: node.querySelector('.sprinkler__toggle'),
        modeButtons: [...node.querySelectorAll('[data-mode]')],
        last: node.querySelector('.sprinkler__last'),
      };
      node.querySelector('.sprinkler__id').textContent = sprinkler.device_id;
      this.items.set(sprinkler.device_id, item);
      this.root.querySelector('.empty')?.remove();
      this.root.append(node);
    }
    item.sprinkler = sprinkler;
    this._render(item);
    this._syncMaster();
    return item;
  }

  zones() {
    return [...this.items.values()].map((i) => i.sprinkler.zone);
  }

  activeCount() {
    return [...this.items.values()].filter((i) => i.sprinkler.status === 'Active').length;
  }

  refreshTimes() {
    for (const item of this.items.values()) this._renderLast(item);
  }

  // ---------------------------------------------------------------- actions
  async _toggle(item, active) {
    const status = active ? 'Active' : 'Inactive';
    await this._send(item, { status }, `Sprinkler ${item.sprinkler.device_id} ${active ? 'activated' : 'deactivated'}`);
  }

  async _setMode(item, mode) {
    await this._send(item, { mode }, `Sprinkler ${item.sprinkler.device_id} set to ${mode}`);
  }

  async _send(item, body, successMessage) {
    const previous = item.sprinkler;
    item.sprinkler = { ...previous, ...body };
    this._render(item);
    item.node.classList.add('is-pending');
    try {
      const updated = await this.api.toggleSprinkler(previous.device_id, body);
      this.upsert(updated);
      toast(successMessage, '', body.status === 'Active' ? 'warning' : 'success', 3000);
    } catch (err) {
      item.sprinkler = previous;
      this._render(item);
      toast('Sprinkler command failed', err.message, 'error');
    } finally {
      item.node.classList.remove('is-pending');
      this._syncMaster();
    }
  }

  async _master(activate) {
    const confirmed = await confirmDialog(activate
      ? {
        title: 'Activate ALL sprinklers?',
        text: 'Every relay in every zone will open and discharge water, including those in Manual mode.',
        confirmLabel: 'Activate all',
      }
      : {
        title: 'Deactivate all sprinklers?',
        text: 'All relays will close. Only do this once the area has been confirmed safe.',
        confirmLabel: 'Deactivate all',
      });
    if (!confirmed) { this._syncMaster(); return; }

    this.master.disabled = true;
    try {
      const result = await this.api.masterSprinkler(activate ? 'Active' : 'Inactive');
      result.sprinklers.forEach((s) => this.upsert(s));
      toast(activate ? 'All sprinklers activated' : 'All sprinklers deactivated',
        `${result.changed.length} relay(s) changed`, activate ? 'critical' : 'success');
    } catch (err) {
      toast('Master switch failed', err.message, 'error');
    } finally {
      this.master.disabled = false;
      this._syncMaster();
    }
  }

  // ---------------------------------------------------------------- render
  _itemFor(node) {
    const li = node.closest('.sprinkler');
    return li ? this.items.get(li.dataset.deviceId) : null;
  }

  _render(item) {
    const s = item.sprinkler;
    const active = s.status === 'Active';
    item.node.dataset.status = s.status;
    item.node.querySelector('.sprinkler__zone').textContent = s.zone;
    item.toggle.checked = active;
    item.toggle.setAttribute('aria-label', `${s.device_id} relay ${active ? 'on' : 'off'}`);
    for (const btn of item.modeButtons) {
      const on = btn.dataset.mode === s.mode;
      btn.classList.toggle('is-active', on);
      btn.setAttribute('aria-pressed', String(on));
    }
    this._renderLast(item);
  }

  _renderLast(item) {
    const s = item.sprinkler;
    item.last.textContent = s.status === 'Active'
      ? `spraying · ${relativeTime(s.last_triggered_at)}`
      : `last ${relativeTime(s.last_triggered_at)}`;
  }

  _syncMaster() {
    const total = this.items.size;
    this.master.checked = total > 0 && this.activeCount() === total;
    this.master.indeterminate = this.activeCount() > 0 && this.activeCount() < total;
  }
}
