# Progress

| Step | Title | Status |
|---|---|---|
| 1 | Scaffold, configuration, rule loading | **done** |
| 2 | Core pipeline (queue, worker, LLM, validation, CSV) | **done** |
| 3 | HTTP API | **done** |
| 4 | Thunderbird add-on | **done** |
| 5 | Add-on installation from the exe | **done** |
| 6 | Admin UI | **done** |
| 7 | Windows Service and installer | **done** |
| 8 | End-to-end test, hardening, user guide | **done** (v1.0.0) |

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

---

## Step 5: Add-on installation from the exe (done, 2026-11-26)

### What exists
- Verified the enterprise policy docs for ESR 153 / 140 (D-501): `ExtensionSettings` `force_installed` with a
  `file:///` `install_url` and `3rdparty` -> `storage.managed` are supported; no alternative needed.
- `src/EfMailAgent/Addon/`:
  - `ThunderbirdLocator` (registry 64/32-bit views behind `IRegistryReader`, default paths, `application.ini` version,
    running check), `FixedThunderbirdLocator` (`--thunderbird-dir`), `CombinedThunderbirdLocator`.
  - `ThunderbirdProfiles` (`profiles.ini`, relative/absolute paths, `[Install...]` and `Default=1` defaults).
  - `AddonPackage` (XPI manifest id/version, bundled XPI lookup, versioned file name).
  - `PolicyFile` (load/classify, merge, remove, backup, atomic UTF-8 write).
  - `AddonInstaller` (install policy/profile, uninstall, status, self-heal) and `AddonSelfHealService` (hosted
    service, runs in the background at service start).
- CLI: `install-addon [--mode policy|profile] [--profile] [--xpi] [--thunderbird-dir] [--user-profile-root]`,
  `uninstall-addon [--thunderbird-dir] [--profile] [--user-profile-root]`, `addon-status [--thunderbird-dir] [--xpi]`.
  Exit codes: 6 no Thunderbird / no XPI, 7 no admin rights / not writable, 8 malformed policies.json, 1 status not OK.
- Tests (33 new): policies.json merging (no file, empty file, other policies, older version of our entry, malformed
  JSON not overwritten, uninstall keeps other keys, idempotent install, backups, BOM-less pretty output), locator
  with a fake registry, profiles.ini, XPI reading, admin check, profile mode, status, self-heal (no state, missing
  entry, changed token, newer XPI, no Thunderbird, broken state file).
- CI (Windows): `install-addon --mode policy` against a temporary fake Thunderbird folder, checks of the merged
  policies.json, `addon-status`, `uninstall-addon`.

### Verification
- `dotnet test`: **238 passed**; Node tests 84 passed; e2e simulation passed.
- Manual run in the sandbox against a fake Thunderbird folder: install merged the entries next to the existing
  `DisableAppUpdate` / other extension keys and made a backup, `addon-status` printed "Állapot: RENDBEN", uninstall
  left exactly the original keys, a malformed policies.json was refused with exit code 8 and left untouched.

### Notes for the next step
- Decisions D-501..D-507. The admin UI (Step 6) can show `AddonInstaller.GetStatus()` for the add-on panel.

---

## Step 6: Admin UI (done, 2026-11-26)

### What exists
- `http://127.0.0.1:<port>/admin`: Hungarian single-page UI (static HTML + Vanilla JS + CSS variables, embedded in
  the exe, no CDN, no build step), light/dark theme, works down to phone width.
  - **Áttekintés**: AI (LLM) reachability, queue length, rules (enabled / invalid, version), Thunderbird add-on
    status, needs-review count with filter link; last 50 jobs with status filter chips (auto refresh 10 s); job drawer
    with metadata, extracted rows, raw answer, repair answer, assembled prompt, email body, "Újrafuttatás".
  - **Szabályok**: rule list with enabled switch and validity badges, "Közös rendszerprompt" (`_base/system.md`),
    file tabs (rule.yaml, prompt.md, skills), save with server-side validation and inline Hungarian errors, .bak on
    every save, add/remove skill file, "Új szabály" from a template, "Másolat készítése".
  - **Tesztkonzol**: rule + sample email picker, subject/sender/body, run synchronously against the LLM without
    queue or CSV; status, timing, rows table (needs-review rows highlighted), raw answer, repair answer and errors,
    assembled prompt.
  - **Beállítások**: LLM provider/URL/model (suggestions from the server's model list)/temperature/timeout/num_ctx/
    think/mock, "Kapcsolat tesztelése", CSV options, output folder, port; states what applied now and what needs a
    restart.
  - **Napló**: tail of the current log file, level filter, line count, auto refresh.
- Backend (`src/EfMailAgent/Admin`): `AdminSessions` + `AdminGuardMiddleware` (cookie session, CSRF, security
  headers), `AdminEndpoints` (/admin/api/*), `RuleFileService` + `RuleYamlEditor`, `SettingsService`, `LogTail`,
  `SampleEmails`, `StaticAssets`; `ReloadableLlmClient`; `CsvWriter` now reads settings per write.
- Tests (36 new): auth (cookie flags, forged cookie, logout, expiry, token change), CSRF (missing header, foreign
  origin/port, no origin, same-origin referer, login), static files + CSP, rule list/read, invalid rule.yaml refused
  with Hungarian errors and nothing written, prompt save with .bak and reload, path traversal / extension / unknown
  folder, enabled switch keeps comments and changes matchers, skills add/remove, new rule from template, duplicate,
  test console (rows, no queue, no CSV, repair path, unknown rule), samples, settings (masked token, live apply incl.
  output folder used by the next job, validation errors, port restart), LLM connection test, log tail, job detail +
  retry, YAML editor, and the acceptance flow (prompt changed in the UI -> Tesztkonzol -> next real job uses it).

### Verification
- `dotnet test`: **274 passed**; Node tests 84 passed; e2e simulation passed.
- Browser check with headless Chromium in the sandbox (D-608). What the screenshots show:
  1. Login card with the token field; a wrong token shows "Hibás token.".
  2. Áttekintés: five status cards (AI elérhető/mock, Sor 0 feladat, Szabályok 1 bekapcsolva, Thunderbird add-on
     "nincs telepítve / javítandó", Ellenőrzendő 5 feladat with link), filter chips with counts (Mind 6, Ellenőrzendő
     5, Hiba 1) and the job table with coloured status badges and the error line under the failed job.
  3. Job drawer: status, id, "Újrafuttatás", metadata (original sender detected), extracted rows table with the
     uncertain 5th row highlighted and "Alacsony bizonyosság." under its badge.
  4. Szabályok: rule list with the switch, rule.yaml tab; an invalid edit (`type: decimal`) is refused with
     "Nem mentettem: a szabály hibás lenne." and the Hungarian loader error.
  5. Tesztkonzol: form on the left (demo sample loaded), result on the right: amber verdict "Érvényes válasz, de van
     ellenőrzendő sor", original sender, rows table, collapsible raw answer and prompt sections.
  6. Beállítások: AI, CSV and service sections; "Kapcsolat rendben (0 ms). Elérhető modellek: mock.".
  7. Napló: colour-coded log lines with level filter and auto refresh switch.
  8. Dark theme at 390 px width: navigation wraps above the stacked cards.

### Notes for the next step
- Decisions D-601..D-608. Step 7 should mention the admin URL on the installer's final page.

---

## Step 7: Windows Service and installer (done, 2026-11-26)

### What exists
- Windows Service lifetime in the normal host (`AddWindowsService`), graceful 30 s shutdown.
- `src/EfMailAgent/Service/ServiceManager.cs` + CLI: `install-service [--account] [--password] [--no-start]`,
  `uninstall-service`, `stop-service`, `configure [--llm-provider] [--llm-base-url] [--llm-model] [--output]`,
  `open-admin`.
- `installer/EfMailAgent.iss`: Hungarian wizard (LLM server, model, output folder, service account), first install,
  upgrade in place, uninstall with "keep data" default, unattended parameters.
- `build.ps1`: clean, C# + Node tests, single-file exe, XPI, installer (`iscc`), optional signing; output
  `dist\EfMailAgent.exe`, `dist\efmailagent.xpi`, `dist\EfMailAgent-Setup.exe`.
- `scripts/set-version.mjs`: sets VERSION and the add-on manifest together.
- Tests: service registration arguments (new, upgrade keeps account, custom account + ACL, masked password on error,
  uninstall), account names, `configure` (first run, upgrade keeps token, invalid value refused).
- CI (Windows): Inno Setup via Chocolatey, `build.ps1` builds the installer; silent install -> service / health /
  admin checks -> upgrade to 99.0.0 keeps token, rules and output -> silent uninstall keeps data.

### Verification
- `dotnet test`: **285 passed**; Node tests 84 passed; e2e simulation passed (sandbox).
- Inno Setup, the service and the installer can only run on Windows: verified by the Windows CI job (see D-706);
  the interactive checks are in `docs/WINDOWS_ACCEPTANCE.md`.

### Notes for the next step
- Decisions D-701..D-706. Step 8: user guide (installer screens, unattended parameters, service account and network
  share notes), hardening and the final review.

---

## Step 8: End-to-end test, hardening, user guide (done, 2026-11-26)

### What exists
- Review against CLAUDE.md, gaps fixed (D-801): add-on new-mail listener with `monitorAllFolders`, prompt sanitiser,
  CSV formula protection, daily summary, benchmark command.
- `Processing/TextSanitizer.cs`: control, zero-width and bidi characters removed before the prompt (D-802).
- `Csv.EscapeFormulas` (default `true`): text cells starting with `= + - @` get a `'` prefix (D-803).
- `Processing/DailySummaryService.cs`: one Hungarian summary log line after local midnight (D-804).
- `EfMailAgent.exe benchmark <rule> [--dir] [--repeat]`: per-email and average LLM time on the sample emails (D-805).
- `src/EfMailAgent.Tests/Robustness/RobustnessTests.cs`: 30-minute LLM outage, 200 queued emails, very long email,
  prompt injection, sanitiser, formula protection, daily summary. Other scenarios were already covered (D-806).
- `docs/USER_GUIDE_HU.md`: Hungarian operator manual (what it does, installation, forwarding and subject keywords,
  new email type worked example, Tesztkonzol tuning, partner list as skill, CSV columns and statuses,
  troubleshooting table, operations, backup and upgrade, CLI summary). The installer installs it when present.
- `README.md`: developer overview (architecture, build, test, run, release).
- Version 1.0.0 (`VERSION`, add-on manifest), tag `v1.0.0`.

### Verification
- `dotnet test`: **292 passed**; Node tests **85 passed**; `scripts/e2e-sim.sh` passed (sandbox).
- Windows build, installer and service: Windows CI job; real-server checks in `docs/WINDOWS_ACCEPTANCE.md` (step 8).

### Performance note
- Sandbox (mock provider, 1.5 s simulated latency), `benchmark eloreendeles` on the 8 sample emails:
  average **1.5 s/email**, slowest 1.7 s, total 12.2 s. The pipeline overhead (prompt, validation, CSV) is in the
  milliseconds; the real time is the model's.
- Real model: to be measured on the server with `EfMailAgent.exe benchmark eloreendeles` (acceptance list). Rough
  expectation for a 7-9B model on a mid-range GPU: 10-40 s per email; CPU only: minutes per email. With strictly
  sequential processing this sets the daily capacity (e.g. 30 s/email is about 120 emails/hour).

---

## Final checklist (v1.0.0)

### Done
- [x] Rules as YAML + Markdown, hot reload, validation with Hungarian messages, schema generated from columns.
- [x] Subject normalisation identical in C# and JS (shared vectors), priority and tie-break.
- [x] Persistent SQLite queue, one worker, FIFO, restart recovery, outage pause, retry with backoff, one repair call.
- [x] Ollama, OpenAI-compatible and mock LLM clients; JSON Schema constrained output; exe writes the CSV.
- [x] CSV: UTF-8 BOM, `;`, CRLF, RFC 4180, decimal separator, meta columns, row status, lock retry and pending file,
      formula protection.
- [x] HTTP API on 127.0.0.1 with token; admin UI (overview, rules, test console, settings, log) with session + CSRF.
- [x] Thunderbird add-on: new mail, matchers refresh, submit, tags, polling, backfill, outbox, context menu,
      managed storage config, options page with connection test, Hungarian UI.
- [x] Add-on installation via `policies.json` merge (backup, uninstall, self-heal) and profile fallback.
- [x] Windows Service, CLI, Inno Setup installer (install, upgrade, uninstall), `build.ps1`, CI on Linux and Windows.
- [x] Prompt-injection hardening, daily summary, benchmark, robustness tests.
- [x] Hungarian user guide, English README.

### Known limitations
- Thunderbird must run in a logged-in session; gaps are covered only by the backfill (default 3 days).
- Verified only in the sandbox and on GitHub's Windows runner: real Thunderbird, a real local model, the interactive
  installer and network shares must be checked on the server (`docs/WINDOWS_ACCEPTANCE.md`).
- Extraction quality depends on the model; prompts need tuning with real emails in the Tesztkonzol.
- Re-running a job appends its rows again; old rows must be removed from the CSV by hand.
- Large skill lists (thousands of partners) are put into the prompt as text and may not fit the model's context.
- The XPI and the exe are unsigned unless `build.ps1 -Sign` is used (SmartScreen / antivirus may warn).
- Attachments are listed by name only; their content is not read.

### Ideas for v2
- Attachment parsing (xlsx, pdf, docx) feeding the same rule pipeline (`attachmentNames` already flows through).
- Master data lookup against ERP exports (partner and item codes matched in code after extraction instead of
  putting the whole list into the prompt).
- Multiple mailboxes / accounts with per-account rules, and multiple servers.
- Direct ERP import instead of CSV files, and a review screen to correct `ellenorzendo` rows before export.
- Optional replacement of rows on re-run (per-job output with a merge step).
