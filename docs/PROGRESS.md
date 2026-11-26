# Progress

| Step | Title | Status |
|---|---|---|
| 1 | Scaffold, configuration, rule loading | **done** |
| 2 | Core pipeline (queue, worker, LLM, validation, CSV) | **done** |
| 3 | HTTP API | **done** |
| 4 | Thunderbird add-on | **done** |
| 5 | Add-on installation from the exe | next |
| 6 | Admin UI | |
| 7 | Windows Service and installer | |
| 8 | End-to-end test, hardening, user guide | |

---

## Step 1: Scaffold, configuration, rule loading (done, 2026-11-26)

### What exists
- Solution `EfMailAgent.sln`: `src/EfMailAgent` (console exe, .NET 10) and `src/EfMailAgent.Tests` (xUnit).
  `Directory.Build.props` (net10.0, nullable, warnings as errors, version from `VERSION`), `global.json`,
  `.editorconfig`, `.gitattributes`, `.gitignore`, placeholders `addon/`, `installer/`, `scripts/`.
- **Configuration** (`src/EfMailAgent/Configuration`): typed `AppSettings` (Server, Llm incl. `mock`, Paths, Csv,
  App.TimeZone), data folder resolution (`--data-dir`, `EFMAILAGENT_DATA_DIR`, ProgramData for Windows Release,
  `./.devdata` otherwise), first-run bootstrap (folders, default `appsettings.json`, 32-byte base64url token,
  sample rules copied into an empty rules folder), Hungarian validation errors.
- **Logging**: Serilog to console (stderr) and daily rolling file in `logs/`.
- **Rules** (`src/EfMailAgent/Rules`): YAML model, loader with Hungarian validation messages (invalid rules are
  skipped, never crash), immutable `RuleSet` snapshot with `Version`, `RuleRepository` with FileSystemWatcher hot
  reload (1 s debounce), JSON Schema generator.
- **Matching** (`src/EfMailAgent/Matching`): subject normalization and best-rule selection per CLAUDE.md section 6.
- **CLI** (System.CommandLine): `validate-rules [--watch]`, `print-token`, global `--data-dir`; unknown commands print
  help. Exit codes: 0 ok, 1 invalid rules, 2 configuration error.
- **Shared vectors**: `shared/normalization-cases.json` with 33 normalization and 16 matching cases (two suites:
  priorities/disabled/regex and priority ties).
- **Samples**: `samples/rules` (`_base/system.md`, `eloreendeles` rule, prompt, 2 skills), demo fixtures from
  DEV_MODE section 8 (`demo_eloreendeles_01.eml`, `demo_nem_illeszkedo.eml`, mock answers, expected CSV with BOM and
  CRLF), 6 messy Hungarian `.txt` emails plus `samples/emails/index.json`.
- **CI**: `.github/workflows/build.yml` (Linux: build, test, CLI check; Windows: test, single-file publish, smoke test,
  artifact).

### Verification
- `dotnet build`: 0 warnings, 0 errors. `dotnet test`: **104 passed**, 0 failed.
- `dotnet run --project src/EfMailAgent -- validate-rules --data-dir ./.devdata` on an empty folder:
  ```
  Szabályok verziója: 72d63c5cb69fa44b
  AZONOSÍTÓ     NÉV                      ÁLLAPOT   PRIORITÁS  ILLESZTÉS
  eloreendeles  Előrendelés feldolgozás  érvényes  10         előrendelés, elorendeles, pre-order
  Összesen: 1 érvényes, 0 hibás szabály.
  ```
  (first run created the folder structure, `appsettings.json` with provider `mock`, and copied the sample rules).
- Hot reload checked manually with `validate-rules --watch`: editing `rule.yaml` reprinted the table with a new
  version within ~1 s.
- `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` from Linux: `EfMailAgent.exe` (~105 MB).

### Notes for the next step
- Decisions D-101..D-116 in `docs/DECISIONS.md`; D-201 (retry/outage semantics) applies to Step 2.
- Dev environment: `export PATH=$HOME/.dotnet:$PATH` before using `dotnet` in the sandbox.

---

## Step 2: Core pipeline (done, 2026-11-26)

### What exists
- **Jobs** (`src/EfMailAgent/Jobs`): SQLite `data/queue.db` with `schema_version` migrations (2 migrations), job model,
  `JobQueue` (enqueue with Message-ID dedup, atomic FIFO dequeue respecting `next_attempt_at`, retry scheduling,
  release without attempt, completion, requeue, startup recovery of `processing` jobs).
- **Prompt** (`Processing/PromptBuilder`): base + rule prompt + skills + generated field list; metadata block and
  `<email>` body, placeholders, truncation, original sender detection (`OriginalSenderDetector`).
- **LLM** (`src/EfMailAgent/Llm`): `ILlmClient` with Ollama (`/api/chat`, schema in `format`, `think:false`,
  `num_ctx`), OpenAI-compatible (`/v1/chat/completions`, `response_format: json_schema, strict`) and the `mock`
  provider from DEV_MODE (lookup by Message-ID / default, `[mock:*]` scenarios, prompt dump to `data/mock-prompts`).
  Health checks via `/api/tags` and `/v1/models`. Transport errors classified (D-201).
- **Validation** (`Processing/ResponseValidator`): JSON parse, JsonSchema.Net validation, calendar date check, row
  status `ok` / `ellenorzendo`, one repair call (`JobProcessor`).
- **CSV** (`src/EfMailAgent/Output`): BOM, `;`, CRLF, RFC 4180 quoting, decimal separator, meta columns, header,
  file name tokens, append / per_job, lock retry with `_pending_` fallback, `_v2` on header change.
- **Worker** (`Processing/JobWorker`): BackgroundService, strictly one job at a time, wake-up on enqueue, backoff
  30 s / 2 min / 10 min, outage pause, graceful shutdown; structured per-job logs (no bodies above Debug).
- **CLI**: `test-rule <ruleId> --file <path> [--subject] [--from] [--write] [--new-message-id]`.
- **Email files**: `.eml` via MimeKit, `.txt` as body; small HTML-to-text converter.
- **Tests**: fake LLM HTTP server (Ollama + OpenAI endpoints, delays, error codes, concurrency tracking) and
  integration tests: valid -> done + CSV, invalid -> repair -> done, invalid twice -> failed, zero rows ->
  needs_review, timeout -> retry scheduled, 4 attempts -> failed, unreachable -> paused without attempt, duplicate
  Message-ID -> one job, restart recovery, sequential FIFO worker, shutdown requeue, demo email -> expected CSV.
- **CI**: the Windows smoke test now also runs `test-rule --write` with the mock provider and compares the CSV with
  `samples/expected/demo_eloreendeles_01.csv`.

### Verification
- `dotnet test`: **184 passed**, 0 failed (3 consecutive runs, ~2 s).
- `test-rule eloreendeles --file samples/emails/demo_eloreendeles_01.eml --write` produced
  `output/eloreendeles_2026-11.csv`, identical to `samples/expected/demo_eloreendeles_01.csv` except the ignored
  `job_id` / `feldolgozva` values; the second run was deduplicated by Message-ID.
- Win-x64 single-file publish from Linux OK (~113 MB).
- No real model in the sandbox: the mock provider returns the canned demo answer for every email, so the rows are
  the same for every sample; prompt assembly and original-sender detection are real. Real-model checks are in
  `docs/WINDOWS_ACCEPTANCE.md`.

### Notes for the next step
- Decisions D-201..D-214; D-302 (`priority` in `/api/matchers`) applies to Step 3.
- `ServiceRegistration.AddEfMailAgentCore` + `AddEfMailAgentWorker` are ready for the WebApplication host.

---

## Step 3: HTTP API (done, 2026-11-26)

### What exists
- **Service mode**: `EfMailAgent` without a command hosts the worker and the API in one process (`Api/ApiHost`,
  `Cli/RunCommand`), Kestrel bound to `127.0.0.1:<Server.Port>` only; a taken port gives a Hungarian fatal message
  and exit code 5.
- **Endpoints** (`Api/ApiEndpoints`): `GET /api/health`, `GET /api/matchers` (ETag / 304, `priority`),
  `POST /api/jobs` (202 / 200 duplicate / 400 / 413 / 422, never waits for the LLM), `GET /api/jobs/{id}`,
  `GET /api/jobs?status=&limit=`, `POST /api/jobs/{id}/retry`, OpenAPI at `/api/openapi.json`.
- **Security**: token middleware with constant-time comparison (401 without body), CORS only for
  `moz-extension://*`, 2 MB request limit, Origin logged at Debug.
- **HTML bodies**: `bodyHtml` without `bodyText` is converted server side (`Email/HtmlToText`).
- **LLM health** cached for 30 s (`Api/LlmHealthCache`).
- **Scripts**: `scripts/dev-submit.ps1` and `scripts/dev-submit.mjs`.
- **Tests**: 21 API tests on TestServer (auth, every endpoint, duplicates, 422, ETag 304, oversized body, HTML body,
  CORS, OpenAPI, retry, end-to-end demo email -> expected CSV through the worker) and 2 real Kestrel tests
  (loopback-only binding, port in use).
- **CI**: Linux and Windows jobs start the exe in service mode and submit the demo email with the dev-submit script.

### Verification
- `dotnet test`: **205 passed**, 0 failed.
- Manual run in the sandbox: `dotnet run --project src/EfMailAgent -- --data-dir ./.devdata`, then
  `node scripts/dev-submit.mjs`:
  ```
  POST /api/jobs -> HTTP 202 {"jobId":"69b6ab5d-...","ruleId":"eloreendeles","status":"queued","duplicate":false}
  Feladat 69b6ab5d-...: needs_review, sorok: 5, kísérletek: 1
  CSV: /home/user/localai/.devdata/output/eloreendeles_2026-11.csv
  ```
  A second submission returned `200` with `"duplicate":true`; the non-matching demo email returned `422`.
- `dev-submit.ps1` cannot run in the sandbox (no PowerShell); it runs in the Windows CI job.

### Notes for the next step
- Decisions D-301..D-309. The add-on needs: token header, `moz-extension` origin, `priority` in matchers,
  Message-ID with or without brackets, `to` as array.

---

## Step 4: Thunderbird add-on (done, 2026-11-26)

### What exists
- `addon/manifest.json`: MV3, id `efmailagent@energofish.hu`, `strict_min_version` 128.0 (D-401), minimal
  permissions, `host_permissions` for `http://127.0.0.1/*`, Hungarian name/description, SVG icon, options page.
- `addon/src/core/` (pure logic, no direct `messenger.*`): `normalize.js` (twin of the C# normalizer), `htmlToText.js`,
  `backoff.js`, `tags.js`, `payload.js`, `config.js` (storage.managed first, then options), `api.js`, `agent.js`
  (new mail, matchers with ETag, submission, outbox, polling, tags, backfill, context menu), `wiring.js`.
- `addon/src/background.js`: event page that wires the real `messenger` into the core.
- `addon/options/`: Hungarian options page (server URL / token read-only when managed, watched folders, backfill
  days, on/off switch, "Kapcsolat tesztelése", host permission check, last 20 submissions, manual rescan).
- `addon/test/`: `fake-messenger.js` (the used Thunderbird API subset, records calls), `fake-exe.js`, and 84 Node
  tests: shared normalization vectors (100% parity with C#), demo payload = `samples/expected/demo_payload.json`,
  tag transitions, outbox (down / 401 / cap 500 / backoff), 422, duplicates, watched folders, pagination, ETag,
  rules version change, pending match, backfill, context menu, managed config, manifest checks.
- `scripts/package-addon.mjs` -> `dist/efmailagent.xpi`; `build.ps1` (tests, XPI, single-file exe into `dist/`).
- `scripts/simulate-thunderbird.mjs`, `scripts/e2e-sim.sh`, `scripts/e2e-sim.ps1`: DEV_MODE section 4 end-to-end run.
- `docs/TESTING.md`: automatic tests and the manual Thunderbird test (temporary loading, background console, checks).
- CI: Node tests, XPI packaging and the e2e simulation on Linux; `build.ps1` + e2e simulation + smoke tests on Windows;
  artifacts `EfMailAgent.exe` and `efmailagent.xpi`.

### Verification
- `dotnet test`: 205 passed. `node --test "test/*.test.js"` (addon): **84 passed**.
- `bash scripts/e2e-sim.sh`: **E2E SZIMULÁCIÓ SIKERES** in ~8 s: payload equals `demo_payload.json`, tag
  "AI: sorban" -> "AI: ellenőrzendő", CSV equals the expected fixture (BOM, `;`, CRLF, quoted cell), the
  non-matching email is not submitted, the second submission of the demo email returns `200` with the same job id
  and no duplicate rows.
- XPI: 16 files, `manifest.json` at the root, valid zip (checked with Python's zipfile).
- Real Thunderbird was not available: the on-site checks are in `docs/WINDOWS_ACCEPTANCE.md` and `docs/TESTING.md`.

### Notes for the next step
- Decisions D-401..D-411. Step 5 must write `policies.ExtensionSettings` and `policies["3rdparty"].Extensions` with
  `serverUrl` and `token`, which `config.js` reads via `storage.managed`.
