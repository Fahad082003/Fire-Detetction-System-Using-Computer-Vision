import { cloneTemplate } from '../core/dom.js';

const HLS_CDN = 'https://cdn.jsdelivr.net/npm/hls.js@1.5/dist/hls.min.js';
const MJPEG_RETRY_MS = 5000;
let hlsLoader = null;

function loadHls() {
  if (window.Hls) return Promise.resolve(window.Hls);
  if (!hlsLoader) {
    hlsLoader = new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = HLS_CDN;
      script.async = true;
      script.crossOrigin = 'anonymous';
      script.onload = () => (window.Hls ? resolve(window.Hls) : reject(new Error('hls.js failed to initialise')));
      script.onerror = () => { hlsLoader = null; reject(new Error('hls.js failed to load')); };
      document.head.append(script);
    });
  }
  return hlsLoader;
}

/** Build the media element for a feed. Returns { el, destroy } or null for simulated feeds. */
function createPlayer(camera, streamUrlFor, onStatus) {
  const format = camera.stream_format;
  if (!camera.stream_url || format === 'SIM') return null;

  if (format === 'MJPEG') {
    const img = new Image();
    img.alt = '';
    img.decoding = 'async';
    const base = streamUrlFor(camera.feed_id);
    let retry = 0;
    img.addEventListener('load', () => onStatus(true));
    img.addEventListener('error', () => {
      onStatus(false);
      clearTimeout(retry);
      retry = setTimeout(() => { img.src = `${base}?r=${Date.now()}`; }, MJPEG_RETRY_MS);
    });
    img.src = base;
    return { el: img, destroy: () => { clearTimeout(retry); img.removeAttribute('src'); } };
  }

  const video = document.createElement('video');
  video.muted = true;
  video.autoplay = true;
  video.playsInline = true;
  video.preload = 'auto';
  video.loop = format === 'MP4';
  video.addEventListener('playing', () => onStatus(true));
  video.addEventListener('error', () => onStatus(false));
  const player = { el: video, hls: null, destroy() { this.hls?.destroy(); video.removeAttribute('src'); video.load(); } };

  if (format === 'HLS' && !video.canPlayType('application/vnd.apple.mpegurl')) {
    loadHls().then((Hls) => {
      if (!Hls.isSupported()) { onStatus(false); return; }
      const hls = new Hls({ lowLatencyMode: true, liveSyncDurationCount: 2, maxBufferLength: 10, backBufferLength: 30 });
      player.hls = hls;
      hls.on(Hls.Events.ERROR, (_evt, data) => {
        if (!data.fatal) return;
        onStatus(false);
        if (data.type === Hls.ErrorTypes.NETWORK_ERROR) setTimeout(() => hls.startLoad(), 3000);
        else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
        else hls.destroy();
      });
      hls.loadSource(camera.stream_url);
      hls.attachMedia(video);
      video.play().catch(() => {});
    }).catch(() => onStatus(false));
  } else {
    video.src = camera.stream_url;
    video.play().catch(() => {});
  }
  return player;
}

function tileAlertLevel(frame) {
  const fireSev = frame.fire_severity ?? 0;
  if (frame.danger_count > 0 || fireSev >= 0.5 || (frame.fire_detected && frame.human_count > 0)) return 'critical';
  if (frame.fire_detected) return 'elevated';
  return '';
}

export class VideoWall {
  constructor(root, overlay, { streamUrlFor }) {
    this.root = root;
    this.overlay = overlay;
    this.streamUrlFor = streamUrlFor;
    this.tiles = new Map();
    this.filter = 'all';
    this.onChange = () => {};

    root.addEventListener('click', (e) => {
      const tile = e.target.closest('.tile');
      if (tile) this.toggleFocus(tile);
    });
    root.addEventListener('keydown', (e) => {
      const tile = e.target.closest('.tile');
      if (tile && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); this.toggleFocus(tile); }
      if (e.key === 'Escape') this.toggleFocus(null);
    });
  }

  setCameras(cameras) {
    const incoming = new Set(cameras.map((c) => c.feed_id));
    for (const id of [...this.tiles.keys()]) if (!incoming.has(id)) this._removeTile(id);
    for (const camera of cameras) this.upsertCamera(camera);
    this._renderEmpty();
    this.applyFilter();
  }

  upsertCamera(camera) {
    const existing = this.tiles.get(camera.feed_id);
    if (existing) {
      const mediaChanged = existing.camera.stream_url !== camera.stream_url
        || existing.camera.stream_format !== camera.stream_format;
      existing.camera = camera;
      this._applyMeta(existing);
      if (mediaChanged) this._mountPlayer(existing);
      return existing;
    }

    const node = cloneTemplate('tpl-video-tile');
    const tile = {
      camera,
      node,
      stage: node.querySelector('.tile__stage'),
      mediaHost: node.querySelector('.tile__media'),
      canvas: node.querySelector('.tile__overlay'),
      stats: {
        humans: node.querySelector('[data-stat="humans"]'),
        danger: node.querySelector('[data-stat="danger"]'),
        fire: node.querySelector('[data-stat="fire"]'),
      },
      player: null,
      frame: null,
      stale: true,
    };
    node.dataset.feedId = camera.feed_id;
    this.tiles.set(camera.feed_id, tile);
    this._applyMeta(tile);
    this.root.append(node);
    this.overlay.attach(camera.feed_id, {
      stage: tile.stage,
      canvas: tile.canvas,
      onStale: (stale) => this._onStale(tile, stale),
    });
    this._mountPlayer(tile);
    return tile;
  }

  onDetection(frame) {
    const tile = this.tiles.get(frame.camera_id);
    if (!tile) return;
    tile.frame = frame;
    this.overlay.update(frame.camera_id, frame);

    tile.stats.humans.textContent = frame.human_count;
    tile.stats.danger.textContent = frame.danger_count;
    tile.stats.danger.parentElement.dataset.active = String(frame.danger_count > 0);
    const fire = frame.fire_detected;
    tile.stats.fire.textContent = fire ? (frame.fire_severity != null ? `${Math.round(frame.fire_severity * 100)}%` : 'YES') : '—';
    tile.stats.fire.parentElement.dataset.active = String(fire);
    const level = tileAlertLevel(frame);
    if (level) tile.node.dataset.alert = level; else delete tile.node.dataset.alert;
    this.onChange();
  }

  /** Totals across feeds with a live (non-stale) detection stream. */
  liveTotals() {
    let humans = 0;
    let atRisk = 0;
    let liveFeeds = 0;
    for (const tile of this.tiles.values()) {
      if (tile.stale || !tile.frame) continue;
      liveFeeds += 1;
      humans += tile.frame.human_count;
      atRisk += tile.frame.danger_count;
    }
    return { humans, atRisk, liveFeeds };
  }

  zones() {
    return [...this.tiles.values()].map((t) => t.camera.location_zone);
  }

  setFilter(filter) {
    this.filter = filter;
    this.applyFilter();
  }

  applyFilter() {
    for (const tile of this.tiles.values()) {
      tile.node.hidden = this.filter !== 'all' && tile.camera.feed_type !== this.filter;
    }
  }

  toggleFocus(tileNode) {
    const focused = this.root.querySelector('.tile.is-focused');
    focused?.classList.remove('is-focused');
    if (tileNode && tileNode !== focused) {
      tileNode.classList.add('is-focused');
      this.root.dataset.focus = 'true';
      tileNode.focus({ preventScroll: true });
    } else {
      this.root.dataset.focus = 'false';
    }
  }

  // ---------------------------------------------------------------- internals
  _applyMeta(tile) {
    const { camera, node } = tile;
    const type = node.querySelector('.tile__type');
    type.textContent = camera.feed_type.toUpperCase();
    type.dataset.type = camera.feed_type;
    node.querySelector('.tile__name').textContent = camera.name;
    node.querySelector('.tile__zone').textContent = camera.location_zone;
    node.setAttribute('aria-label', `${camera.name}, ${camera.feed_type} feed, ${camera.location_zone}`);
    tile.mediaHost.dataset.label = camera.status === 'Offline' ? 'FEED OFFLINE' : 'SIMULATED FEED · AI OVERLAY';
  }

  _mountPlayer(tile) {
    tile.player?.destroy();
    tile.mediaHost.replaceChildren();
    tile.player = createPlayer(tile.camera, this.streamUrlFor, (ok) => {
      tile.mediaHost.dataset.offline = String(!ok);
    });
    const isSim = !tile.player;
    tile.mediaHost.classList.toggle('tile__media--sim', isSim);
    if (tile.player) tile.mediaHost.append(tile.player.el);
    this.overlay.setMedia(tile.camera.feed_id, tile.player?.el ?? null);
  }

  _removeTile(id) {
    const tile = this.tiles.get(id);
    if (!tile) return;
    tile.player?.destroy();
    this.overlay.detach(id);
    tile.node.remove();
    this.tiles.delete(id);
  }

  _onStale(tile, stale) {
    tile.stale = stale;
    tile.node.dataset.stale = String(stale);
    if (stale) {
      delete tile.node.dataset.alert;
      tile.stats.danger.parentElement.dataset.active = 'false';
      tile.stats.fire.parentElement.dataset.active = 'false';
    }
    this.onChange();
  }

  _renderEmpty() {
    const empty = this.root.querySelector('.empty');
    if (this.tiles.size) empty?.remove();
    else if (!empty) this.root.append(Object.assign(document.createElement('div'), { className: 'empty', textContent: 'No camera feeds registered.' }));
  }
}
