// Thin client for the EfMailAgent HTTP API. `fetch` is injected so the logic runs in Node tests and Thunderbird.

export const TOKEN_HEADER = 'X-EfMailAgent-Token';
const TIMEOUT_MS = 15_000;

export class ApiError extends Error {
  /** kind: 'unreachable' | 'unauthorized' | 'server' | 'rejected' */
  constructor(kind, message, status = 0, body = null) {
    super(message);
    this.kind = kind;
    this.status = status;
    this.body = body;
  }
}

export function createApi({ fetch, config }) {
  async function request(method, path, { body, headers = {}, accept = [200] } = {}) {
    let response;
    try {
      response = await fetch(`${config.serverUrl}${path}`, {
        method,
        headers: {
          [TOKEN_HEADER]: config.token ?? '',
          ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
          ...headers,
        },
        body: body !== undefined ? JSON.stringify(body) : undefined,
        signal: typeof AbortSignal !== 'undefined' && AbortSignal.timeout ? AbortSignal.timeout(TIMEOUT_MS) : undefined,
      });
    } catch (error) {
      throw new ApiError('unreachable', `Az EfMailAgent nem érhető el (${config.serverUrl}): ${error?.message ?? error}`);
    }

    const text = await response.text().catch(() => '');
    let json = null;
    try {
      json = text ? JSON.parse(text) : null;
    } catch {
      json = null;
    }

    if (accept.includes(response.status)) return { status: response.status, body: json, headers: response.headers };
    if (response.status === 401) throw new ApiError('unauthorized', 'Az EfMailAgent elutasította a tokent (401). Ellenőrizd a beállításokat.', 401);
    if (response.status >= 500) throw new ApiError('server', `Az EfMailAgent hibát adott: HTTP ${response.status}`, response.status, json);
    throw new ApiError('rejected', json?.error ?? `HTTP ${response.status}`, response.status, json);
  }

  return {
    health: async () => (await request('GET', '/api/health')).body,

    /** Returns { notModified: true } on 304, otherwise { etag, rulesVersion, matchers }. */
    matchers: async (etag) => {
      const result = await request('GET', '/api/matchers', { headers: etag ? { 'If-None-Match': etag } : {}, accept: [200, 304] });
      if (result.status === 304) return { notModified: true, etag };
      return { notModified: false, etag: result.headers.get('ETag'), ...result.body };
    },

    /** 202 new job, 200 duplicate: { jobId, ruleId, status, duplicate }. 422 (no match) throws ApiError('rejected'). */
    submit: async (payload) => (await request('POST', '/api/jobs', { body: payload, accept: [200, 202] })).body,

    /** Job state, or null when the exe does not know the job (404). */
    job: async (id) => {
      const result = await request('GET', `/api/jobs/${encodeURIComponent(id)}`, { accept: [200, 404] });
      return result.status === 404 ? null : result.body;
    },
  };
}
