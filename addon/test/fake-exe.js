// In-memory stand-in for the EfMailAgent HTTP API, exposed as a fetch() implementation for unit tests.

export function createFakeExe({ token = 'teszt-token', matchers } = {}) {
  const state = {
    down: false,
    rulesVersion: 'v1',
    matchers: matchers ?? [{ ruleId: 'eloreendeles', name: 'Előrendelés feldolgozás', subjectContains: ['előrendelés', 'elorendeles', 'pre-order'], subjectRegex: null, priority: 10 }],
    jobs: new Map(),
    requests: [],
    nextJob: 1,
    /** Status assigned when a job is "processed" by advance(). */
    finalStatus: 'needs_review',
    /** Optional override for POST /api/jobs: (payload) => { status, body } */
    submitOverride: null,
  };

  const json = (status, body, headers = {}) =>
    new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });

  async function fetch(url, init = {}) {
    const { pathname, searchParams } = new URL(url);
    const method = init.method ?? 'GET';
    const headers = Object.fromEntries(Object.entries(init.headers ?? {}).map(([k, v]) => [k.toLowerCase(), v]));
    const body = init.body ? JSON.parse(init.body) : undefined;
    state.requests.push({ method, path: pathname, query: Object.fromEntries(searchParams), headers, body });
    if (state.down) throw new TypeError('NetworkError when attempting to fetch resource.');
    if (headers['x-efmailagent-token'] !== token) return new Response(null, { status: 401 });

    if (method === 'GET' && pathname === '/api/health') {
      return json(200, { status: 'ok', version: '0.1.0', llmReachable: true, queueLength: [...state.jobs.values()].filter((j) => j.status === 'queued').length, rulesVersion: state.rulesVersion });
    }
    if (method === 'GET' && pathname === '/api/matchers') {
      const etag = `"${state.rulesVersion}"`;
      if (headers['if-none-match'] === etag) return new Response(null, { status: 304, headers: { ETag: etag } });
      return json(200, { rulesVersion: state.rulesVersion, matchers: state.matchers }, { ETag: etag });
    }
    if (method === 'POST' && pathname === '/api/jobs') {
      if (state.submitOverride) {
        const result = state.submitOverride(body);
        if (result) return json(result.status, result.body);
      }
      const existing = [...state.jobs.values()].find((j) => j.messageId === body.messageId);
      if (existing) return json(200, { jobId: existing.id, ruleId: existing.ruleId, status: existing.status, duplicate: true });
      const ruleId = body.ruleId ?? (/(előrendelés|elorendeles|pre-order)/i.test(body.subject) ? 'eloreendeles' : null);
      if (!ruleId) return json(422, { error: 'A tárgy egyik bekapcsolt szabályra sem illeszkedik, és nincs megadva ruleId.' });
      const job = { id: `job-${state.nextJob++}`, messageId: body.messageId, ruleId, status: 'queued', payload: body };
      state.jobs.set(job.id, job);
      return json(202, { jobId: job.id, ruleId, status: 'queued', duplicate: false });
    }
    const jobMatch = /^\/api\/jobs\/([^/]+)$/.exec(pathname);
    if (method === 'GET' && jobMatch) {
      const job = state.jobs.get(decodeURIComponent(jobMatch[1]));
      return job ? json(200, { id: job.id, status: job.status, ruleId: job.ruleId, attempts: 1, error: null, outputFile: null, rowCount: 5 }) : new Response(null, { status: 404 });
    }
    return new Response(null, { status: 404 });
  }

  /** Simulates the worker finishing every queued job. */
  function advance(status = state.finalStatus) {
    for (const job of state.jobs.values()) if (job.status === 'queued') job.status = status;
  }

  const submissions = () => state.requests.filter((r) => r.method === 'POST' && r.path === '/api/jobs');

  return { fetch, state, advance, submissions, token };
}
