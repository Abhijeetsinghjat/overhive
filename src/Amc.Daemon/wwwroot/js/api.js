// REST client. The token is embedded in the page by the daemon (meta amc-token).

const meta = document.querySelector('meta[name="amc-token"]');
export const TOKEN = meta ? meta.getAttribute('content') : '';
export const TOKEN_MISSING = !TOKEN || TOKEN === '__AMC_TOKEN__';

export class ApiError extends Error {
  constructor(status, message, body) {
    super(message);
    this.status = status;
    this.body = body;
  }
  get notAvailable() { return this.status === 404 || this.status === 405 || this.status === 501; }
}

// Reads are retried across short daemon restarts (e.g. after an upgrade); writes are never
// retried automatically, so an action is not performed twice.
const READ_RETRY_DELAYS = [500, 1000, 2000, 4000];

async function request(method, path, body) {
  let res;
  for (let attempt = 0; ; attempt++) {
    try {
      res = await fetch(path, {
        method,
        headers: { 'X-Amc-Token': TOKEN, ...(body ? { 'Content-Type': 'application/json' } : {}) },
        body: body ? JSON.stringify(body) : undefined,
        cache: 'no-store',
      });
      break;
    } catch (e) {
      if (method !== 'GET' || attempt >= READ_RETRY_DELAYS.length)
        throw new ApiError(0, 'Daemon unreachable: ' + e.message);
      await new Promise((r) => setTimeout(r, READ_RETRY_DELAYS[attempt]));
    }
  }
  const ct = res.headers.get('content-type') || '';
  let data;
  if (ct.includes('json')) {
    const txt = await res.text();
    try { data = txt ? JSON.parse(txt) : null; } catch { data = txt; }
  } else data = await res.text();
  if (!res.ok) {
    const msg = (data && typeof data === 'object' && (data.error || data.detail)) || `${res.status} ${res.statusText}`;
    throw new ApiError(res.status, res.status === 404 && !(data && data.error || data && data.detail)
      ? `Not available: ${path.split('?')[0]} is not implemented by this daemon build yet`
      : String(msg), data);
  }
  return data;
}

export const api = {
  get: (path) => request('GET', path),
  post: (path, body) => request('POST', path, body),

  health: () => request('GET', '/api/health'),
  state: () => request('GET', '/api/state'),
  events: (params) => request('GET', '/api/events?' + qs(params)),
  agent: (id) => request('GET', `/api/agents/${encodeURIComponent(id)}`).catch((e) => {
    if (e.status === 404) e.message = 'the daemon does not know this agent id (404)';
    throw e;
  }),
  processes: () => request('GET', '/api/processes'),
  sendMessage: (id, text) => request('POST', `/api/agents/${encodeURIComponent(id)}/messages`, { text }),
  control: (id, action, confirm = false) => request('POST', `/api/agents/${encodeURIComponent(id)}/control`, { action, confirm }),
  worktreeDiff: (id, path) => request('GET', `/api/worktrees/${encodeURIComponent(id)}/diff` + (path ? '?' + qs({ path }) : '')),
  worktreeFile: (id, path) => request('GET', `/api/worktrees/${encodeURIComponent(id)}/file?` + qs({ path })),
  worktreeLog: (id, path, limit = 50) => request('GET', `/api/worktrees/${encodeURIComponent(id)}/log?` + qs({ path, limit })),
  tools: () => request('GET', '/api/tools'),
  launchSession: (body) => request('POST', '/api/sessions', body),
  agentDiff: (id) => request('GET', `/api/agents/${encodeURIComponent(id)}/diff`),
};

/** Builds a query string; arrays become repeated keys, null/'' are skipped. */
export function qs(params) {
  const u = new URLSearchParams();
  for (const [k, v] of Object.entries(params || {})) {
    if (v == null || v === '') continue;
    if (Array.isArray(v)) v.forEach((x) => u.append(k, x));
    else u.append(k, String(v));
  }
  return u.toString();
}

export function wsUrl(path, params) {
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${proto}//${location.host}${path}?${qs({ ...params, token: TOKEN })}`;
}

/** Extracts text from an endpoint whose payload shape is not fixed yet (text or JSON). */
export function textOf(data, ...fields) {
  if (data == null) return '';
  if (typeof data === 'string') return data;
  for (const f of [...fields, 'diff', 'patch', 'content', 'text', 'output']) {
    if (typeof data[f] === 'string') return data[f];
  }
  return JSON.stringify(data, null, 2);
}
