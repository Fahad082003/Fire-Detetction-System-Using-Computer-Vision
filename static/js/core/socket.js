/**
 * Resilient WebSocket client: exponential backoff with jitter, heartbeat pings and
 * dead-link detection. Emits `state` events plus one event per server message type.
 */
export class LiveSocket extends EventTarget {
  constructor(path, { heartbeatMs = 20000, deadAfterMs = 45000, maxBackoffMs = 30000 } = {}) {
    super();
    this.path = path;
    this.heartbeatMs = heartbeatMs;
    this.deadAfterMs = deadAfterMs;
    this.maxBackoffMs = maxBackoffMs;
    this.ws = null;
    this.state = 'idle';
    this.attempt = 0;
    this.lastMessageAt = 0;
    this._reconnectTimer = 0;
    this._heartbeatTimer = 0;
    this._closedByUser = false;

    window.addEventListener('online', () => this._reconnectNow());
    document.addEventListener('visibilitychange', () => {
      if (!document.hidden && this.state === 'closed') this._reconnectNow();
    });
  }

  get url() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${proto}//${location.host}${this.path}`;
  }

  connect() {
    this._closedByUser = false;
    clearTimeout(this._reconnectTimer);
    if (this.ws && (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING)) return;

    this._setState('connecting');
    const ws = new WebSocket(this.url);
    this.ws = ws;

    ws.addEventListener('open', () => {
      this.attempt = 0;
      this.lastMessageAt = Date.now();
      this._setState('open');
      this._startHeartbeat();
    });

    ws.addEventListener('message', (event) => {
      this.lastMessageAt = Date.now();
      let message;
      try { message = JSON.parse(event.data); } catch { return; }
      if (!message || typeof message.type !== 'string') return;
      this.dispatchEvent(new CustomEvent(message.type, { detail: message }));
    });

    ws.addEventListener('close', (event) => {
      if (this.ws !== ws) return;
      this._stopHeartbeat();
      this._setState('closed', { code: event.code });
      if (!this._closedByUser && event.code !== 4401) this._scheduleReconnect();
    });

    ws.addEventListener('error', () => ws.close());
  }

  send(payload) {
    if (this.ws?.readyState !== WebSocket.OPEN) return false;
    this.ws.send(JSON.stringify(payload));
    return true;
  }

  close() {
    this._closedByUser = true;
    clearTimeout(this._reconnectTimer);
    this._stopHeartbeat();
    this.ws?.close(1000);
  }

  _setState(state, extra = {}) {
    this.state = state;
    this.dispatchEvent(new CustomEvent('state', { detail: { state, attempt: this.attempt, ...extra } }));
  }

  _scheduleReconnect() {
    const base = Math.min(this.maxBackoffMs, 1000 * 2 ** this.attempt);
    const delay = base / 2 + Math.random() * (base / 2);
    this.attempt += 1;
    clearTimeout(this._reconnectTimer);
    this._reconnectTimer = setTimeout(() => this.connect(), delay);
  }

  _reconnectNow() {
    if (this._closedByUser || this.state === 'open' || this.state === 'connecting') return;
    this.attempt = 0;
    this.connect();
  }

  _startHeartbeat() {
    this._stopHeartbeat();
    this._heartbeatTimer = setInterval(() => {
      if (Date.now() - this.lastMessageAt > this.deadAfterMs) {
        this.ws?.close(4000);
        return;
      }
      this.send({ type: 'ping' });
    }, this.heartbeatMs);
  }

  _stopHeartbeat() {
    clearInterval(this._heartbeatTimer);
  }
}
