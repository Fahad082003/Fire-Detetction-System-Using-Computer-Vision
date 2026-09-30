function readCookie(name) {
  const match = document.cookie.split('; ').find((row) => row.startsWith(`${name}=`));
  return match ? decodeURIComponent(match.slice(name.length + 1)) : '';
}

export class ApiError extends Error {
  constructor(status, body) {
    const detail = body && typeof body === 'object'
      ? body.detail || Object.entries(body).map(([k, v]) => `${k}: ${[].concat(v).join(' ')}`).join('; ')
      : String(body || '');
    super(detail || `Request failed (${status})`);
    this.status = status;
    this.body = body;
  }
}

export function createApi(base = '/api') {
  async function request(method, path, body, { timeout = 10000 } = {}) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeout);
    const headers = { Accept: 'application/json' };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (method !== 'GET') headers['X-CSRFToken'] = readCookie('csrftoken');
    try {
      const response = await fetch(`${base}${path}`, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        credentials: 'same-origin',
        signal: controller.signal,
      });
      const text = await response.text();
      let data = null;
      if (text) {
        try { data = JSON.parse(text); } catch { data = text; }
      }
      if (!response.ok) throw new ApiError(response.status, data);
      return data;
    } catch (err) {
      if (err.name === 'AbortError') throw new ApiError(0, { detail: 'Request timed out' });
      throw err;
    } finally {
      clearTimeout(timer);
    }
  }

  const enc = encodeURIComponent;
  return {
    request,
    snapshot: () => request('GET', '/system/snapshot/'),
    incidents: (params = {}) => request('GET', `/incidents/?${new URLSearchParams(params)}`),
    resolveIncident: (id) => request('POST', `/incidents/${id}/resolve/`, {}),
    toggleSprinkler: (deviceId, body = {}) => request('POST', `/sprinklers/${enc(deviceId)}/toggle/`, body),
    masterSprinkler: (status, zone) => request('POST', '/sprinklers/master/', zone ? { status, zone } : { status }),
    dispatch: (payload) => request('POST', '/dispatch/', payload),
    pushConfig: () => request('GET', '/push/'),
    subscribePush: (subscription) => request('POST', '/push/', subscription),
    unsubscribePush: (endpoint) => request('DELETE', '/push/', { endpoint }),
    cameraStreamUrl: (feedId) => `${base}/cameras/${enc(feedId)}/stream/`,
  };
}
