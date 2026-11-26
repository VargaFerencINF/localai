# Decisions and deviations

Every deviation from `CLAUDE.md` / `docs/PROMPTS.md` and every non-obvious design choice is recorded here.
Format: `D-<number>` (step) title, then decision and reason.

## Process

### D-001 (setup) Decisions are made by Claude Code
The project owner asked Claude Code to decide open questions on its own best judgement and not to wait for answers
or plan approvals. Steps are still executed one at a time and every step still ends with tests, docs, commit, push
and a stop. Every decision taken this way is recorded in this file.

### D-002 (setup) Development toolchain in the sandbox
The Linux sandbox had no .NET SDK. It was installed once with the official `dotnet-install.sh --channel 10.0`
into `~/.dotnet` (SDK 10.0.404). `global.json` pins SDK `10.0.100` with `rollForward: latestFeature`, so any
10.0.x feature band works locally and in CI. Cross-publishing the `win-x64` single-file exe from Linux works.

## Step 1

### D-101 Data directory resolution
`CLAUDE.md` says `C:\ProgramData\EfMailAgent`, Step 1 says `./.devdata` for dev runs. Resolution order:
1. `--data-dir <path>` (recursive option, valid for every command),
2. environment variable `EFMAILAGENT_DATA_DIR`,
3. `C:\ProgramData\EfMailAgent` for a **Release build on Windows** (the installed exe),
4. `./.devdata` otherwise (Debug builds, Linux/macOS).

A data directory counts as "development" unless it is the production default path. This drives the default LLM
provider on first run (D-110).

### D-102 ASP.NET Core shared framework referenced from Step 1
The exe references `Microsoft.AspNetCore.App` already now instead of individual `Microsoft.Extensions.*` packages.
Reason: the newest NuGet versions of those packages are 11.x (.NET 11) and would drift from the .NET 10 runtime;
the shared framework always matches it, and Kestrel is needed from Step 3 anyway. Side effect: the self-contained
single-file exe is about 105 MB.

### D-103 Package choices
`System.CommandLine` 3.0.0 (stable), `YamlDotNet` 18.1.0, `Serilog` 4.4.0 with `Serilog.AspNetCore` 10.0.0
(matches .NET 10), xUnit 2.9.3. JSON Schema validation library (`JsonSchema.Net`) is added in Step 2 where it is used.

### D-104 Console logging goes to stderr
All Serilog console output is written to stderr so that stdout of CLI commands stays machine-readable
(`print-token` prints only the token). File logs: `logs/efmailagent-YYYYMMDD.log`, daily rolling, 30 files kept,
50 MB size limit per file, UTF-8 without BOM.

### D-105 Subject normalization details (CLAUDE.md section 6)
- Implementation order: NFD + removal of combining marks, invariant lowercase, whitespace collapse, then the leading
  prefixes are stripped repeatedly, then trim. The result is identical to the order in the spec (strip, NFD,
  lowercase), but the prefix list can then be written in plain ASCII (`tovabbitas`, `va`, `valasz`) and behaves the
  same in C# and JavaScript.
- Combining marks = Unicode categories Mn, Mc, Me (= JavaScript `\p{M}`).
- Invisible characters are removed: U+00AD soft hyphen, U+200B..U+200F, U+2060, U+FEFF.
- Whitespace is an explicit list (tab, LF, VT, FF, CR, space, U+0085, U+00A0, U+1680, U+2000..U+200A, U+2028,
  U+2029, U+202F, U+205F, U+3000) because `\s` differs between .NET and JavaScript.
- Prefixes: `re fw fwd tov tovabbitas va valasz aw wg`, optionally followed by `[n]` or `(n)`, optional single space
  before the colon (`Fwd : x`), no space needed after it (`RE:FW:x`). Tags: a leading `[...]` block of 1-30
  characters without nested brackets (`[EXT]`, `[Külső]`, `[ticket #123]`).
- Keywords (`subject_contains`) get the same normalization **without** prefix stripping, so a keyword such as
  `re: fontos` is kept intact.
- Known, accepted difference: JavaScript lowercases a final Greek capital sigma context-sensitively; irrelevant for
  Hungarian subjects.

### D-106 Regex dialect
`subject_regex` is compiled with `RegexOptions.ECMAScript` (250 ms match timeout), which keeps the .NET dialect close
to JavaScript, because the add-on evaluates the same pattern. It runs on the normalized subject; the validator warns
if the pattern contains uppercase or accented letters (outside escapes such as `\S`, `\D`), since those can never
match.

### D-107 Priority ties
Ties are broken by ordinal comparison of rule ids (`StringComparer.Ordinal`, equal to JavaScript's `<` on strings).

### D-108 JSON Schema shape
Generated from `columns`, compatible with OpenAI strict structured outputs and Ollama `format`:
every column is listed in `required`, optional columns are nullable (`["string","null"]`, optional enums include
`null` in `enum`), `additionalProperties: false` on both levels, `date` = `{"type":"string","format":"date"}`
(llama.cpp's grammar converter supports `format: date`; our own validation checks it again in Step 2), column
`description` copied. Required string columns have no `minLength`: an empty required value must lead to
`needs_review` (CLAUDE.md section 8), not to a schema failure. `rows_path` is the name of the array property.

### D-109 Rule validation rules beyond the spec
- Unknown YAML keys are errors (catches typos such as `prioritty`).
- `id`: `^[a-z0-9][a-z0-9_-]*$`; an id different from the folder name is a warning.
- Defaults: `enabled: true`, `priority: 100`, `prompt_file: prompt.md`, `output.mode: append`, `rows_path: rows`,
  `llm.max_body_chars: 20000`.
- Prompt and skill paths must stay inside the rule folder.
- Column names: `^[A-Za-z_][A-Za-z0-9_]*$`, unique, and not one of the CSV meta columns
  (`job_id feldolgozva email_datum kuldo eredeti_kuldo targy message_id statusz`).
- `output.file`: only the tokens `{yyyy} {MM} {dd} {ruleId} {jobId}`, no folders, no characters that Windows forbids.
- Duplicate ids: the first folder in ordinal order wins, later ones are reported and skipped.
- Prompt and skill **contents** are read into the immutable rule snapshot at load time, so a reload swaps the whole
  rule set atomically and a job never sees a half-edited rule.
- Folders starting with `_` or `.` are not rules. A missing `_base/system.md` is a warning.

### D-110 Configuration file
- `appsettings.json` uses .NET's usual PascalCase section and key names (`Server.Port`, `Llm.Provider` ...).
- Extra keys beyond Step 1's list: `Llm.Think` (false, DEV_MODE), `Llm.MockDir`, `Llm.MockDelayMs`,
  `App.TimeZone` (`Europe/Budapest`, DEV_MODE 8.7), `Llm.NumCtx` default **16384** (Ollama's own default is small
  and silently truncates a 20 000-character body).
- First run in a development data folder writes provider `mock` with an absolute `MockDir` pointing at the
  repository's `samples/mock-responses` (found by walking up from the exe and the current directory). The production
  default stays `ollama`.
- The file is never overwritten. If it exists but has no token (e.g. written by the installer), a token is generated
  and only that key is added. Written as UTF-8 without BOM, via temp file + rename.
- Invalid settings stop the command with exit code 2 and a Hungarian list of problems.
- Sample rules are copied into the rules folder only when it is completely empty; existing files are never replaced.

### D-111 Rules version
`RulesVersion` = first 16 hex characters of SHA-256 over all relevant files in the rules folder (relative path,
length, content; ordinal path order). `.bak`, `.tmp`, `*~` and hidden files/folders are ignored, so admin UI backups
do not change the version. The repository raises `Reloaded` only when the version actually changes. Reloads are
debounced by 1 s; a load that races with file writes is retried (up to 3 reads) so the version always describes the
loaded content.

### D-112 Sample prompts
`_base/system.md` and `eloreendeles/prompt.md` are taken verbatim from DEV_MODE 8.3 / 8.4. One line was appended to
`prompt.md` to cover the Step 1 requirement about vague "same as last time" items (own row, quantity only if
explicit, reference in `megjegyzes`, `bizonyossag: alacsony`). It does not affect the demo email.

### D-113 Sample email formats
`CLAUDE.md` lists `samples/emails/*.txt`, DEV_MODE uses `.eml` for the demo fixtures. Both exist:
`.txt` files contain only the body (what Thunderbird would extract); their subject, sender and expected rule are in
`samples/emails/index.json`. `.eml` files carry their own headers. `.gitattributes` marks `*.eml` and
`samples/expected/*.csv` as `-text` so they stay byte-exact (BOM, CRLF) on every checkout.

### D-114 Single VERSION file from Step 1
`VERSION` (currently `0.1.0`) is read by `Directory.Build.props` into the assembly version; Step 4/7 reuse it for the
add-on manifest and the installer.

### D-115 GitHub Actions from Step 1
`.github/workflows/build.yml` exists from Step 1 with a Linux job (build, test, CLI check) and a Windows job (test,
single-file publish, exe smoke test, artifact upload). Later steps add Node tests, the e2e simulation, XPI packaging
and Inno Setup as described in DEV_MODE section 6.

### D-116 Windows console encoding
On Windows the exe sets `Console.OutputEncoding = UTF-8`, otherwise Hungarian characters are garbled in the default
console code page.

## Step 2

### D-201 Retry semantics and LLM outages
"Max 3 attempts" with three backoff values (30 s, 2 min, 10 min) is read as **1 initial attempt + 3 retries**
(4 attempts), otherwise the 10 min value would never be used. Configurable as `Worker.RetryDelaysSeconds`.
Transport errors are classified (`LlmErrorKind`):

| Kind | Example | Effect |
|---|---|---|
| Unreachable | connection refused, DNS | job back to the queue **without** using an attempt, worker pauses `Worker.LlmOutagePauseSeconds` (30 s) |
| Configuration | no model configured | same as Unreachable |
| Timeout | no answer within `Llm.TimeoutSeconds` | counts as an attempt, backoff |
| ServerError / ClientError | HTTP 5xx / 4xx (e.g. unknown model) | counts as an attempt, backoff |
| Other | malformed envelope, missing mock answer | counts as an attempt, backoff |

So a 30-minute Ollama outage (Step 8) leaves jobs queued instead of failing them. The outage is logged once at
Warning and once at Information when the LLM is back.

### D-202 No Dapper
Plain `Microsoft.Data.Sqlite` with a small hand-written mapper. Timestamps are ISO 8601 UTC text (`O` format,
fixed width, so text comparison = time order); Dapper's `DateTimeOffset` handling on SQLite would need custom type
handlers anyway. WAL journal mode, 30 s busy timeout (the CLI and the service may share the database).
Dequeue is a single `UPDATE ... RETURNING` statement, so taking a job is atomic.

### D-203 Extra job columns (migration 2)
Besides the Step 2 column list: `original_sender`, `prompt_system`, `prompt_user`, `repair_response`, `parsed_rows`
(JSON with row status, reasons and values) and `duration_ms`. They feed the admin UI's job drawer (Step 6) and keep
parsed rows safe if the CSV cannot be written. `raw_response` holds the first answer, `repair_response` the repair
answer.

### D-204 Message-ID canonical form (was planned as D-301)
Implemented in `JobQueue.Enqueue`: trimmed and wrapped in `<...>`, so Thunderbird's bracket-less `headerMessageId`
and the raw header value deduplicate to the same job and the CSV shows `<id@host>` as in the expected demo CSV.

### D-205 Repair call
Messages: system, user, assistant (the invalid answer, max 20 000 characters), user (Hungarian instruction plus the
validation errors, max 30). Aggregate errors of the schema library ("properties", "items" ...) are dropped when more
specific errors exist. A Markdown code fence around the JSON is unwrapped before validation (harmless, common with
small models). A transport error during the repair call is handled like any transport error of the job.

### D-206 Validation library and dates
`JsonSchema.Net` 9.7.0 validates the generated schema (it also checks `format: date`). An extra check makes sure
dates are real calendar dates in `yyyy-MM-dd` form.

### D-207 Job outcome details
- Zero rows: `needs_review`, no CSV file is touched, `last_error` = "A modell nem talált egyetlen tételt sem."
- Rule deleted or disabled after the job was queued: `failed` with a clear message (can be retried later).
- CSV write failure (not a lock): `failed`, but the parsed rows stay in `parsed_rows`; `POST /api/jobs/{id}/retry`
  (Step 3) re-runs it.
- Shutdown during processing: the job goes back to the queue without using an attempt (Step 7's graceful
  shutdown requirement is covered already).

### D-208 CSV details
- Numbers: invariant format without thousand separators or trailing zeros (`2.50` -> `2,5`), decimal separator from
  `Csv.DecimalSeparator`. Booleans: `igen` / `nem`. `null` -> empty cell.
- Cells are quoted only when needed (delimiter, quote, CR or LF), quotes doubled (RFC 4180).
- `email_datum` and `feldolgozva` use `App.TimeZone`, file name date tokens too.
- Locked file: exclusive open is retried every `Csv.LockRetryIntervalSeconds` (5 s) for `Csv.LockRetrySeconds`
  (600 s), then `<name>_pending_<jobId>.csv` is written and a warning logged. On Linux every `IOException` while
  opening counts as a lock (there is no sharing-violation code), which only matters in tests.
- `per_job` mode never appends: an existing name gets `_2`, `_3` ...
- Header mismatch: `_v2`, `_v3` ... (the first file whose header matches, or a new one).
- A file saved without a final line break is continued on a new line.

### D-209 Original sender detection
Heuristic over the body: a `Feladó:` / `From:` / `Von:` line counts only inside a header block (a forward marker
such as `-------- Továbbított üzenet --------`, `---------- Forwarded message ---------`,
`-----Original Message-----`, an Outlook `____` line, or another header line like `Elküldve:` / `Sent:` / `Tárgy:`
within 4 lines). Of several candidates the first one outside the forwarder's own e-mail domain wins (the customer,
not a colleague or our own quoted offer); if all are internal, the deepest one. Outlook's `Name [mailto:a@b]` is
normalized to `Name <a@b>`. Verified on all sample emails (`samples/emails/index.json` has the expected values).

### D-210 Prompt details
- The user message starts with a Hungarian metadata block (forwarder, original sender or "nem azonosítható",
  subject, received time, today, attachment names if any), then `<email>...</email>`.
- A truncation note is placed **after** `</email>`, so it cannot be mistaken for e-mail content.
- `<email>` / `</email>` inside the body are neutralized (`‹email›`) so the body cannot close the data block.
- Placeholders are replaced in `_base/system.md`, `prompt.md` and skills, never in the e-mail body.
  Extra placeholder: `{{original_sender}}`.

### D-211 Ollama request
`think` is always sent with the value of `Llm.Think` (default `false`, DEV_MODE). `options.num_ctx` is sent only
when `Llm.NumCtx > 0`. If an older Ollama rejects `think` for a model, set it in the acceptance test
(docs/WINDOWS_ACCEPTANCE.md). Health checks use a 5 s timeout.

### D-212 test-rule command
- `.eml` files are parsed with MimeKit 4.18.2 (headers, encoded words, multipart, HTML-only bodies, attachment names);
  other files are the plain body, subject and sender come from `--subject` / `--from`.
- Rows are printed as one block per row (field: value), which stays readable with 11 columns.
- Without `--write` nothing is written (not even the mock prompt dump, which needs a job id).
- With `--write` the job is enqueued with the email's Message-ID (or a generated one, or a new one with
  `--new-message-id`), then exactly that job is taken (`TryDequeue`) and processed, so a running service does not
  process it twice. A scheduled retry is left to the service.
- Exit codes: 0 ok, 1 invalid rules, 2 configuration error, 3 usage error, 4 processing failed.

### D-213 HTML to text
A small converter (`Email/HtmlToText`) exists already; Step 3 reuses it for `bodyHtml`-only submissions.

### D-214 Composition
`ServiceRegistration.AddEfMailAgentCore` wires the pipeline with Microsoft DI; the CLI uses it now, the HTTP host
(Step 3) will reuse it. `TimeProvider` is injected everywhere time matters, which keeps the retry tests
deterministic.

## Step 3

### D-301 Service mode = no subcommand
`EfMailAgent.exe` without a command runs the worker and the HTTP API until Ctrl+C (Step 7 adds the Windows Service
lifetime to the same path). `--data-dir` works here too. A taken port stops the process with a Hungarian fatal log
line and exit code 5.

### D-302 `/api/matchers` also returns `priority`
The add-on needs it to pick the same best rule as the exe in the shared-vector parity tests. Matchers are ordered by
priority, then id. `ETag` = `"<rulesVersion>"`, `Cache-Control: no-cache`; `If-None-Match` with that value (or `*`)
gives `304` with an empty body.

### D-303 CORS and the Origin Thunderbird sends
Thunderbird is Gecko based: requests made by an extension (background page, options page) carry
`Origin: moz-extension://<UUID>`, where the UUID is generated per profile and installation, so it cannot be known in
advance. The policy therefore accepts any `moz-extension://<host>` origin (GET/POST, headers `X-EfMailAgent-Token`,
`Content-Type`, `If-None-Match`, exposes `ETag`, preflight cached 10 minutes) and nothing else: web pages
(`http(s)://`), other browsers' extensions and `null` origins get no CORS headers. Because the token is a custom
header, any browser request from a web page needs a preflight, which is refused, and without the token every call is
`401` anyway. With `host_permissions` for `http://127.0.0.1/*` Gecko does not even enforce CORS for the extension,
so the policy is defence in depth.
Verification: this could not be observed in the sandbox (no Thunderbird). The API logs the `Origin` of every
request at Debug level and of rejected requests at Warning; the on-site check is in `docs/WINDOWS_ACCEPTANCE.md`.

### D-304 Contract details
- `POST /api/jobs`: duplicates are detected **before** rule matching (a re-submitted message always gets its
  existing job). `202` and `200` share one body shape: `{ jobId, ruleId, status, duplicate }`.
- `to` may be a string or an array of strings (Thunderbird gives an array); arrays are joined with `", "`.
- `source` defaults to `auto`; `receivedAt` must be ISO 8601. `ruleId` must name an existing, enabled rule (else
  `422`) and skips subject matching.
- `400` body: `{ error, errors: { field: [messages] } }` in Hungarian; `401` and `413` have empty bodies.
- `GET /api/jobs` returns `{ jobs: [...] }` (newest first, `limit` 1..500, default 50); job objects contain the
  spec fields plus `messageId`, `subject`, `source`, `nextAttemptAt`.
- `POST /api/jobs/{id}/retry`: `200` with the job, `404` unknown, `409` when the job is not `failed`/`needs_review`.
- `GET /api/health`: spec fields plus `llmProvider`, `llmModel`, `llmError`, `llmOutage`, `validRules`,
  `invalidRules`; `status` is `ok` or `degraded` (LLM unreachable or a rule invalid). The LLM check is cached 30 s.
- The OpenAPI document (`/api/openapi.json`, OpenAPI 3.1 from `Microsoft.AspNetCore.OpenApi`) also requires the
  token, like every `/api/*` path.

### D-305 Size limit
Kestrel's `MaxRequestBodySize` is 2 MB; the guard middleware also rejects a larger `Content-Length` with `413`
(this also covers the in-memory test server, which ignores Kestrel limits).

### D-306 Token comparison
Both the configured and the presented token are hashed with SHA-256 and compared with
`CryptographicOperations.FixedTimeEquals`, so neither content nor length leaks through timing.

### D-307 Host details
`WebApplication.CreateSlimBuilder` with the content root set to the exe folder (no `appsettings.json` is picked up
from the current directory; ours lives in the data folder). No `Server` header. Serilog is the only logging provider.

### D-308 API tests use TestServer instead of WebApplicationFactory
`Program` is a System.CommandLine entry point, so `WebApplicationFactory` cannot intercept a host built in `Main`
reliably. The tests call the production `ApiHost.Build(...)` and swap Kestrel for `TestServer`: same services, same
middleware, same endpoints. Binding to 127.0.0.1 and the port-in-use path are tested with the real Kestrel server.

### D-309 Development submit script in two flavours
`scripts/dev-submit.ps1` (Windows, PowerShell 5.1 and 7, uses `HttpClient` so UTF-8 bodies and 4xx answers behave
the same) and `scripts/dev-submit.mjs` (Node, for the Linux sandbox and CI). Both read port and token from the data
folder's `appsettings.json`, submit an `.eml` (headers parsed) or `.txt` (metadata from `samples/emails/index.json`),
and wait for the job to finish.

## Step 4

### D-401 Thunderbird version and strict_min_version
Step 4 asks to ask the owner for the installed Thunderbird version; per D-001 the decision was taken here. The
official docs (https://webextension-api.thunderbird.net, read on 2026-11-26) document MV3 for the current release
(161) and the newest ESR (153); 128 ESR is the first release with official MV3 support (migration guide). Every API
the add-on relies on exists since **128**, except `messengerUtilities.convertToPlainText` (140), which is used only
when present (own HTML-to-text fallback otherwise). Therefore `strict_min_version` is **`128.0`**: the add-on works on
128 ESR, 140 ESR and 153 ESR. If the server runs something older than 128, MV3 is not available and the add-on will
not install; the version must be confirmed on site (docs/WINDOWS_ACCEPTANCE.md).

### D-402 API choices (verified against the ESR 153 MV3 docs)

| Purpose | API | Since | Permission |
|---|---|---|---|
| New mail | `messages.onNewMailReceived(folder, MessageList)`, default monitoring (inbox and normal folders); folder filter in the add-on | 78 (monitorAllFolders 128, used since D-801) | accountsRead, messagesRead |
| Body | `messages.listInlineTextParts(id)` -> `[{contentType, content}]`; fallback `messages.getFull` tree walk | 128 | messagesRead |
| HTML -> text | `messengerUtilities.convertToPlainText(html)` if available, else own converter | 140 | - |
| Attachment names | `messages.listAttachments(id)` (`name`) | 91 | messagesRead |
| Tags | `messages.tags.list()` / `messages.tags.create(key, tag, color)` | 128 | messagesTagsList / messagesTags |
| Set tags | `messages.update(id, { tags })` | 68 | messagesUpdate |
| Backfill | `messages.query({ folderId, fromDate })` + `messages.continueList(id)` | 128 (folderId) | messagesRead, accountsRead |
| Folders | `folders.query({ specialUse: ['inbox'] })`, `folders.get(id)`; inbox = `specialUse` contains `"inbox"` | 128 | accountsRead |
| Context menu | `menus.create({ id, parentId, contexts: ['message_list'] })`, `menus.onClicked` (`info.selectedMessages` is a MessageList) | 68 | menus (+ messagesRead) |
| Config | `storage.managed.get` (read-only, from the policy), `storage.local` | 60 | storage |
| Timers | `alarms.create(name, { periodInMinutes })`, `alarms.onAlarm` | 60 | alarms |

Every MessageList is consumed page by page with `continueList`. Not verified from the Thunderbird pages (they were
not part of the docs read) and taken from Gecko/Firefox MV3 semantics: `background.scripts` with `type: module` is a
non-persistent event page, `host_permissions` for `http://127.0.0.1/*`, `browser_specific_settings.gecko`. These are
on the on-site checklist.

### D-403 No managed_schema in the manifest
The Thunderbird storage docs state that enforcement of extension-provided managed storage schemas is not supported
(the key is Chromium's). The manifest therefore does not declare `storage.managed_schema` (it would only produce a
manifest warning). `addon/managed_schema.json` documents the two keys (`serverUrl`, `token`) the policy provides.

### D-404 Tags
Fixed keys `efai_sorban`, `efai_kesz`, `efai_ellenorzendo`, `efai_hiba` with the labels from CLAUDE.md and distinct
colors (blue, green, amber, red). A tag that already has the same label (e.g. created by hand) is reused with its
key. Setting a status replaces any AI tag and keeps all other tags of the message.

### D-405 Event page state
All state lives in `storage.local` (`matchers`, `openJobs`, `outbox`, `history`, `tagKeys`, `pendingMatch`,
`options`); handlers within one page instance are serialized with a promise chain so read-modify-write sequences do
not interleave. Alarms: `efmailagent-poll` every minute (rules version check via `/api/health`, outbox, open jobs),
`efmailagent-matchers` every 15 minutes. Both are recreated on every startup.

### D-406 Outbox rules
- Network error, timeout, `401` and `5xx`: the payload goes to the outbox (never dropped, no tag). Retry delays
  1, 2, 5, 10, 30, 60 minutes (capped). A wrong token therefore heals itself once the token is fixed.
- `400` / `422`: a permanent answer from the exe; nothing is retried (422 = no rule matches according to the exe).
- Cap 500: when full, a new message is **not** added (warning in the console). It stays untagged, so the startup
  backfill picks it up later; nothing already queued is thrown away.

### D-407 Mail before the first matcher download
If the matchers were never downloaded (first start while the exe is down), new mail ids are remembered
(`pendingMatch`, max 500) and checked as soon as the matchers arrive. Afterwards the cached matchers are used while
the exe is unreachable.

### D-408 Payload details
`messageId` is sent in canonical `<...>` form; `to` is the recipients array (accepted by the exe, D-304);
`account` is the Thunderbird account id, `folder` the folder path; `receivedAt` is the message date in ISO UTC;
`bodyHtml` is sent only when there is no plain text part (then `bodyText` is the converted HTML); `ruleId` only for
manual submissions. The exact JSON for the demo email is `samples/expected/demo_payload.json`.

### D-409 Context menu
"Küldés AI feldolgozásra" with one sub-item per enabled rule (rebuilt whenever the matchers change; a disabled
placeholder item when there are none). A message already known to the exe returns its existing job (dedup by
Message-ID), also for a manual submission with another rule; re-processing is done with the retry endpoint / admin UI.

### D-410 Shared wiring and packaging
`addon/src/core/wiring.js` registers the handlers for both the real background page and the tests / e2e
simulation. `scripts/package-addon.mjs` builds a deterministic XPI (manifest first at the zip root, UTF-8 names,
fixed timestamps; tests, `package.json` and Markdown excluded) and refuses to build when the manifest version differs
from `VERSION`. `build.ps1` calls it, so Linux CI and Windows produce the same XPI. Icons are a single SVG.
`addon/package.json` only switches Node to ES modules for the tests; it is not packaged.

### D-411 End-to-end simulation uses the policy path
`scripts/simulate-thunderbird.mjs` gives the add-on its server URL and token through the fake `storage.managed`, the
same way the policy will in production (Step 5).

### D-412 Wrong weekday in the demo fixture
`samples/emails/demo_eloreendeles_01.eml` (DEV_MODE 8.1, kept verbatim) says `Date: Tue, 25 Nov 2026 09:41:00 +0100`,
but 25 November 2026 is a Wednesday. MimeKit and JavaScript ignore the weekday, .NET's `DateTimeOffset.Parse` rejects
it. `scripts/dev-submit.ps1` therefore strips the weekday before parsing. The expected CSV (`2026-11-25 09:41`) is
unaffected.

### D-413 Native libraries inside the single-file exe
The first Windows CI run of `build.ps1` showed that a single-file publish leaves SQLite's native `e_sqlite3.dll`
(and the IIS module) next to the exe; `build.ps1` copies only `EfMailAgent.exe`, so the service could not open its
database. `IncludeNativeLibrariesForSelfExtract=true` embeds them (extracted to a temp folder at first start), so
`EfMailAgent.exe` is really one file, which the installer (Step 7) relies on.

## Step 5

### D-501 Enterprise policy support verified
Checked on 2026-11-26 against the official generated policy docs
(https://thunderbird.github.io/policy-templates/, pages for ESR 153.8.0 and ESR 140.17.0; ESR 128 is listed too):
- `ExtensionSettings` is supported (base 68, `installation_mode` / `install_url` 89). `force_installed`: "installed
  automatically and the user can neither disable nor remove it". `install_url` may be "a `file:///` URL"; native
  paths are not supported, hence `new Uri(path).AbsoluteUri` (`file:///C:/ProgramData/...`). Nothing about signing.
- `3rdparty` is supported (78): "Set policies that WebExtensions can access via chrome.storage.managed ... which the
  extension reads with `browser.storage.managed`". Shape `policies["3rdparty"].Extensions[<id>] = { ... }`.
- Windows location: `distribution\policies.json` next to `thunderbird.exe`; every example uses the top-level
  `{ "policies": { ... } }` wrapper. GPO (`Software\Policies\Mozilla\Thunderbird`) exists as an alternative; the
  precedence between GPO and policies.json and whether the file is read only at startup are not documented (we tell
  the user to restart Thunderbird).
Nothing had to be replaced by an alternative, so the CLAUDE.md design stands.

### D-502 Versioned XPI file name (deviation)
CLAUDE.md shows `install_url: file:///C:/ProgramData/EfMailAgent/addon/efmailagent.xpi`. We copy the XPI as
`addon\efmailagent-<version>.xpi` and point `install_url` there, deleting older versioned copies. Reason: with a
fixed URL Thunderbird has no reason to notice that the file behind it changed after an upgrade; a new URL per
version makes the policy entry itself change, which is the reliable trigger for reinstalling a force-installed
add-on. `addon-status` and self-heal treat a different URL as "outdated". (To confirm on site:
docs/WINDOWS_ACCEPTANCE.md.)

### D-503 Install details
- The bundled XPI is `efmailagent.xpi` next to the exe (`build.ps1` puts both in `dist\`); `--xpi` overrides it;
  during development `dist/efmailagent.xpi` of the repository is found by walking up. Its manifest id must be
  `efmailagent@energofish.hu`.
- Thunderbird folders: `HKLM\SOFTWARE\Mozilla\Mozilla Thunderbird\<version>\Main\Install Directory` in the 64-bit
  and the 32-bit registry view, then `Program Files` / `Program Files (x86)\Mozilla Thunderbird`; a folder counts if
  it has `thunderbird.exe` (or `application.ini`). Stale registry entries are ignored, duplicates merged, the
  version comes from `application.ini`. `--thunderbird-dir` replaces the search (tests, CI, unusual installs).
  Every installation found gets the entry.
- All `policies.json` files are validated before anything is written; a malformed one stops the command with exit
  code 8 and is never touched. Otherwise the file is backed up as `policies.json.bak-yyyyMMdd-HHmmss` (never
  overwriting an older backup), merged (only our two keys change), written UTF-8 without BOM, indented, via temp file +
  rename. An already up-to-date file is not rewritten and not backed up.
- Admin rights are checked up front in policy mode (Windows; exit code 7 with a Hungarian hint to use an elevated
  prompt or `--mode profile`). No Thunderbird or no XPI: exit code 6.
- The output says whether Thunderbird is running and that it must be restarted.

### D-504 Install state file and self-heal
`install-addon` writes `addon\install-state.json` (mode, version, Thunderbird folders, time); `uninstall-addon`
deletes it. Self-heal runs in the background at every service start and acts **only** when that file says
`policy`: otherwise an intentional `uninstall-addon` would be undone by the next service start, and a developer
machine would get policies it never asked for. It repairs a missing entry (e.g. after a Thunderbird update replaced
the program folder), a changed token or port, and an XPI older than the bundled one, and logs a warning. It also uses
the folders recorded in the state file (so a `--thunderbird-dir` install is healed too). It never throws; without
Thunderbird it only logs.

### D-505 Uninstall
Removes only our `ExtensionSettings` and `3rdparty.Extensions` entries (and containers that become empty), backs the
file up first, deletes `<profile>\extensions\efmailagent@energofish.hu.xpi` from the given profile or from every
profile in `profiles.ini`, and deletes the state file. Once the policy entry is gone the add-on is no longer
enforced; Thunderbird may keep the installed copy in the profile, so the output tells the user it can be removed in
the Add-ons Manager. (Blocking it via policy would add keys, which "reverse the policy entries" does not ask for.)

### D-506 Profile mode
`--mode profile` copies the XPI to `<profile>\extensions\efmailagent@energofish.hu.xpi` of the `--profile` folder or
of the default profile from `profiles.ini` (`[Install...] Default=` first, then `Default=1`). The profile root is
`%APPDATA%\Thunderbird` or `--user-profile-root` (a service account's APPDATA is not the user's). Thunderbird asks the
user to allow such a side-loaded add-on, and there is no managed storage in this mode, so the output prints the
server URL and how to get the token for the options page.

### D-507 Token in policies.json
The token ends up in `<Thunderbird>\distribution\policies.json`, which every local user can read (it is in Program
Files). This is accepted: the API listens on 127.0.0.1 only, and on this dedicated server the local users are the
operators anyway. It is documented for the user guide (Step 8).

## Step 6

### D-601 Admin login and session
`POST /admin/api/login` exchanges the API token for a session cookie `efmailagent_admin` (HttpOnly, SameSite=Strict,
Path=/admin, no Secure flag because it is plain HTTP on 127.0.0.1). The value is `issuedAt.HMAC-SHA256(issuedAt)`
keyed with the API token: no server-side session store, sessions survive a service restart, expire after 12 hours
and die when the token changes. The token itself is never stored in the browser. A wrong token costs 500 ms and is
logged as a warning.

### D-602 CSRF protection
Every non-GET request under `/admin/api` (login included) needs the header `X-EfMailAgent-Admin: 1` and an `Origin`
(or, if the browser sends none, a `Referer`) whose scheme, host and port equal the request's. A custom header cannot
be sent cross-site without a CORS preflight, which the admin API never allows; together with SameSite=Strict this
blocks cross-site requests. Failures are `403` without details (logged as a warning).

### D-603 Admin page hardening
Static files are embedded resources (`Admin/wwwroot`, flat folder, `admin/<file>` resource names) served with
`Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; ... frame-ancestors 'none'`,
`X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy: same-origin`; API answers are `no-store`. The UI never uses
`innerHTML`: email text, prompts and model answers are inserted with `textContent` only.

### D-604 Rule editing
- `rule.yaml` is validated before it is written: the rule folder is copied to a temp folder with the new text and
  loaded by the real `RuleLoader` (plus a duplicate-id check against the other rules). Errors come back in Hungarian
  with `422` and nothing is written.
- Every save keeps the previous version as `<file>.bak` (one backup per file, overwritten by the next save; `.bak`
  files are ignored by the rules version). Rules are reloaded immediately instead of waiting for the file watcher.
- The enabled switch, adding/removing skills and duplicating a rule edit `rule.yaml` as text (only the affected
  line or the `skills` block changes, comments stay). A removed skill file is kept as `.bak`; `rule.yaml` and the
  prompt file cannot be removed.
- New rules (from a template or as a copy) start **disabled** so they cannot catch mail before their prompt is ready;
  a copy gets the new id in its CSV file name. Ids: `^[a-z0-9][a-z0-9_-]*$`; skill file names `^[a-z0-9][a-z0-9_-]*\.md$`.
- Only `.yaml`, `.yml`, `.md`, `.txt` files inside the rule folders and `_base` are reachable; paths may not leave
  the folder; files up to 1 MB.

### D-605 Test console
Runs the saved rule (rules are reloaded first, so "save, then test" works) through the same `ExtractAsync` as the
worker: prompt, LLM call, validation, repair call. Nothing goes to the queue or the CSV, and the mock provider does
not dump the prompt (no job id). Sample emails come from `samples\emails` (next to the exe or in the repository),
with metadata from `index.json`.

### D-606 Settings applied without restart
LLM settings (provider, URL, model, temperature, timeout, num_ctx, think, mock), CSV settings and the output folder
apply immediately: `ReloadableLlmClient` recreates the HTTP/mock client when provider, URL or mock settings change,
`CsvWriter` reads its settings and output folder on every write, and the LLM health cache is invalidated. The port
needs a restart (and `install-addon`, so the add-on policy gets the new URL). Token, time zone and data folder are
shown read-only. The output folder must be writable (checked with a probe file). Saving rewrites only the edited
keys of `appsettings.json` (token and unknown keys stay) and keeps `appsettings.json.bak`.

### D-607 Overview and log pages
The overview refreshes every 10 s; jobs can be filtered by status (the "Ellenőrzendő" card links to the filter, part
of Step 8's observability item). The add-on card uses the same status check as `addon-status`. The log page reads at
most the last 2 MB of the newest `efmailagent-*.log` (shared read, the file stays writable for Serilog), groups
multi-line entries (exceptions) and filters by minimum level.

### D-608 UI verification
The UI was driven with the preinstalled headless Chromium (Playwright) in the sandbox: login (wrong and right token),
overview, job drawer, rule editor with an invalid save, prompt change + Tesztkonzol, settings connection test, log
page, dark mode at phone width. Browser tests are not part of CI (no browser install there); the admin API is covered
by xUnit tests.

## Step 7

### D-701 Service hosting
`AddWindowsService` (Microsoft.Extensions.Hosting.WindowsServices) is part of the normal host: started by the Service
Control Manager the exe runs as the service "EfMailAgent", started from a console it runs as before. Shutdown
timeout 30 s; the running job is put back to the queue on stop (D-207). A start failure (e.g. port taken) ends the
process with a non-zero exit code, which the recovery settings below turn into a restart.

### D-702 Service registration from the exe
`install-service` uses `sc.exe` (create or config, `start= delayed-auto`, display name "EfMailAgent e-mail AI
feldolgozó", description, `failure ... actions= restart/60000/restart/60000/restart/60000` with `reset= 86400`, and
`failureflag 1` so non-crash failures also count) and `ServiceController` to start / stop / wait. Without `--account`
a new service runs as LocalSystem and an existing one keeps its account (upgrade). With `--account` the account
gets modify rights on the data folder (`icacls ... (OI)(CI)M /T`); `user` means a local account (`.\user`). The
password is masked in error messages. Other commands for the installer: `stop-service`, `uninstall-service`,
`configure` (creates the data folder, token and sample rules like any first run, then sets only the given LLM /
output values, validated, keeps `.bak`), `open-admin`.

### D-703 Installer behaviour
- Inno Setup 6 with the official `Hungarian.isl`; per-machine, admin, x64, Windows 10+.
- Installs to `Program Files\EfMailAgent`: `EfMailAgent.exe`, `efmailagent.xpi`, `samples\rules` (copied into the
  data folder by the exe only when its rules folder is empty) and `samples\emails` (for the Tesztkonzol).
- Wizard pages: LLM server type, URL and model; CSV output folder; service account (empty = LocalSystem, with a note
  about network shares). Every value can also be passed for unattended installs: `/LLMPROVIDER= /LLMURL= /LLMMODEL=
  /OUTPUT= /ACCOUNT=` (no password parameter: an account install with password is interactive).
- After copying: `configure` (first install with the wizard values; upgrade without values, so nothing changes),
  `install-service` (creates or reconfigures and starts it), `install-addon` (a missing Thunderbird is reported on
  the final page, not a failure). The final page shows the admin URL, how to get the token, the Thunderbird restart
  note and any problems; "Az admin felület megnyitása" opens the browser as the original user.
- Upgrade: detected by an existing `ProgramData\EfMailAgent\appsettings.json`; the configuration pages are skipped,
  the service is stopped before the files are replaced (`stop-service` in PrepareToInstall), token, settings, rules
  and output are never touched.
- Uninstall: `uninstall-addon`, `uninstall-service`, then a question (default and silent answer: No) whether to delete
  `ProgramData\EfMailAgent`. A CSV folder outside it is never deleted.

### D-704 Version handling
`VERSION` is the single source: assembly versions (Directory.Build.props), the installer (`/DAppVersion` from
build.ps1, or read from the file by the .iss itself), and the add-on manifest, which `scripts/set-version.mjs` keeps
in sync (the packager and a Node test refuse a mismatch).

### D-705 Code signing (optional)
`build.ps1 -Sign -CertificateThumbprint <sha1> [-TimestampUrl <url>]` signs `EfMailAgent.exe` before it is packed and
`EfMailAgent-Setup.exe` afterwards with `signtool` (SHA-256, RFC 3161 timestamp). Unsigned builds work but can
trigger SmartScreen or antivirus warnings on first run.

### D-706 Installer tested in CI
The Windows job installs the built setup silently, checks the running delayed-auto service with restart recovery,
the health endpoint and the admin page, then builds the same binaries as version 99.0.0, upgrades over a locally
modified rule and an existing CSV (both must survive, the token must not change), and finally uninstalls silently
(service and program files gone, data kept). A real interactive run on a clean machine stays in the acceptance list.

## Step 8

### D-801 Review against the specification
Gaps found and fixed in the final review:
- The add-on registered `messages.onNewMailReceived` with the default monitoring, which skips special-use folders
  (archives, sent, drafts, junk, trash, templates). An extra folder chosen in the options that has a special use (e.g.
  an archive a filter moves orders into) would never fire the event. It now passes `monitorAllFolders = true`
  (Thunderbird 128+, our minimum); the agent still drops events from folders that are not watched.
- Hidden and control characters were passed to the model unchanged (see D-802).
- CSV cells could start Excel formulas (see D-803).
- No daily summary log line (see D-804), no way to measure the real model speed on the server (see D-805).
Everything else in CLAUDE.md sections 5-10 was checked item by item and is implemented; the remaining deviations are
the ones already recorded in D-1xx..D-7xx.

### D-802 Prompt-injection hardening
On top of the existing measures (email inside `<email>` delimiters with look-alike tags neutralised, the base system
prompt says the email is data, schema enforced by the LLM server and validated by the exe, repair call limited to
one), `TextSanitizer` now removes C0/C1 control characters (except line feed and tab), zero-width characters,
bidirectional overrides and isolates, soft hyphen and BOM from the subject, sender, attachment names and body before
the prompt is assembled. Line breaks are normalised to `\n`; subject and sender become single lines. The stored job
keeps the original text (the sanitiser only shapes what the model sees). A model that obeys an injected instruction
and answers in another format fails validation twice and ends as `failed` with no CSV rows (test
`Prompt_injection_cannot_change_the_output_format`).

### D-803 CSV formula protection (`Csv.EscapeFormulas`, default `true`)
Email text ends up in CSV cells that operators open in Excel, so a cell starting with `=`, `+`, `-`, `@`, tab or CR is
prefixed with `'` (OWASP CSV injection guidance). It applies to `string` columns and to the text meta columns
(`kuldo`, `eredeti_kuldo`, `targy`, `message_id`); numbers, dates, booleans and enums are never changed, so negative
numbers stay numbers. The ERP import sees the `'` in the rare affected cells; set `EscapeFormulas` to `false` if the
import cannot handle it. The demo expected CSV is unchanged (none of its cells is affected).

### D-804 Daily summary
`DailySummaryService` writes one Information line a minute after local midnight (time zone `App.TimeZone`) about the
previous day: processed / done / needs_review / failed counts by `finished_at`, average processing time and current
queue length. It is a log line only; the admin UI already has the "Ellenőrzendők megnyitása" filter link.

### D-805 Performance measurement
No real model exists in the sandbox, so a `benchmark <rule> [--dir] [--repeat]` command was added: it runs the sample
emails through the configured LLM without touching the queue or the CSV and prints per-email and average times. The
sandbox number (mock provider with 1.5 s simulated latency) only proves the overhead is negligible; the real number
is measured on the server (`docs/WINDOWS_ACCEPTANCE.md`, step 8).

### D-806 Robustness scenarios
Covered by automated tests: restart mid-job, LLM down for 30 minutes (queue pauses, attempts stay 0, job then
succeeds), 200 queued emails (strict FIFO, one at a time, 1000 rows in one file), very long email (truncated prompt,
complete stored body), HTML-only body, CSV locked by Excel (retry, then `_pending_` file), broken rule (reported with a
Hungarian message, the other rules keep working), Thunderbird closed for a day (backfill), prompt injection. Real-server repetitions
are in the acceptance checklist.

### D-807 Version 1.0.0
`VERSION` and the add-on manifest are set to 1.0.0 and the commit is tagged `v1.0.0`.

## Pre-release checks (v1.0.0)

### D-808 Admin UI usability test
`scripts/ui-test.mjs` drives the admin UI in Chromium (Playwright) through the operator tasks of the user guide: login,
first start without an AI, configuring the AI in Settings, following jobs, retry, creating the worked-example rule
(`reklamacio`) with an invalid and a valid edit, a skill, the test console, enabling the rule and a live email ending
up in the CSV, the log, accessibility names, dark mode, phone width and logout (39 checks). It runs locally (starts its
own exe) and against an installed service (`--url --token`); CI runs it on Linux and on the installed Windows
service. Problems it found and that were fixed:
- The job list needed horizontal scrolling at 1440 px; the "Sorok" and "Idő" columns were out of view (fixed table
  layout, subject and sender truncated with a tooltip).
- Opening a job with Enter closed the panel again at once (the same key press reached the close button that got the
  focus); keyboard users could not open a job.
- The panel did not take the focus and did not give it back; the list refreshed only 10 s after a retry.
- The 10 s auto refresh dropped the keyboard focus from the list.
- Unsaved rule edits were lost without a question when leaving through the sidebar, the browser or logout.
- The log filters were 650 px wide and had no accessible name; a long rule name was cut off by its badge.
Expected refusals (401, 422) that the browser prints to the console are not counted as JavaScript errors.

### D-809 Installer verification and release from CI
The Windows CI job now installs with wizard parameters (LLM URL, model, output folder) on a runner with a Thunderbird
registered in the registry, checks that the values reach `appsettings.json`, the files are installed, the add-on is
force-installed through `policies.json` with the token, then runs the UI test against the installed service (the CSV
must appear in the chosen output folder), upgrades (settings, the rule created in the UI, local prompt edits and the
CSV stay) and uninstalls (service and policy entries gone, data and other Thunderbird policies kept).
The sandbox's git proxy refuses tag pushes, so the release is created by a `release` job in the same workflow: on a
push to the default branch, after the Linux and Windows jobs are green, if `v<VERSION>` does not exist yet, it creates
the tag and the GitHub Release with the setup, the exe, the XPI, `SHA256SUMS.txt` and the notes from
`docs/RELEASE_NOTES.md`.

## Version 1.1.0

### D-901 The admin UI always opens on start
User request: starting the program must always open the UI, where the AI connection and the output locations are
set. Started by a person (not as a Windows service, input not redirected, no `--no-browser`, no
`EFMAILAGENT_NO_BROWSER`), the exe opens the default browser with a one-time login link
(`/admin/login?code=issuedAt.nonce.hmac`, HMAC-SHA256 keyed by the API token, valid 2 minutes, single use, kept in
memory). Anyone able to create a link can read the token anyway (same machine, same data folder), so it does not
widen access. First start or an unreachable AI opens the Beállítások page, otherwise the overview. A refused link
redirects to the login page with a Hungarian explanation. The link never appears in logs (removed from error
messages). `open-admin` uses the same link.

### D-902 Second start = open the UI, start nothing
Before anything is built, the run command checks whether an EfMailAgent with the same token answers on the port; if
so, a person's start only opens the UI and exits 0 (read-only: no log file, no settings written, so a normal user can
use the Start menu icon while the SYSTEM service owns the files), a scripted start exits with code 5. A port taken by
another program is also detected before the host starts. Previously the worker of the second instance started before
Kestrel reported the port conflict and could recover or take a job of the running instance's queue.

### D-903 Sample rules built into the exe
A standalone EfMailAgent.exe (no installer) found no `samples` folder and started with an empty rules folder.
`samples/rules/**` and `samples/emails/**` are now embedded resources; they are used when no samples folder is found
(rules: copied into an empty rules folder; emails: extracted once per version into the temp folder for the
Tesztkonzol and `benchmark`). A test checks that the embedded copies are byte-identical to the folder.

### D-904 Output settings in the UI
Beállítások gets a "Kimenet" section: the output folder (validated as writable on save) and, per rule, `output.mode`
and `output.file` with a live preview of today's file name. They are written into rule.yaml by a line-based editor
that keeps comments and the columns list, and saved through the normal rule validation (folder in the name, unknown
placeholders and bad modes are refused with the loader's Hungarian messages). The overview shows a banner with a
link to the settings when the AI is unreachable or no model is chosen.

### D-905 Shortcuts
The installer creates "EfMailAgent kezelőfelület" in the Start menu (always) and on the public desktop (task, on by
default), both pointing at `EfMailAgent.exe` without arguments (start or open, D-902). A failed double-click start
keeps the console open until Enter so the reason can be read.

## Version 1.2.0

### D-1001 Desktop app window without a new UI framework
User request: the exe should open like a desktop application. Options were a WebView2 host (WinForms/WPF:
`net10.0-windows`, WebView2 runtime, Windows-only build and tests) or the installed Chromium browser in app mode.
Chosen: Microsoft Edge (then Chrome) with `--app=<login link>` and its own `--user-data-dir`
(`%LOCALAPPDATA%\EfMailAgent\app-window`): a window without tabs and address bar, with its own taskbar entry, no new
dependency, the same UI and tests on every OS. Fallback: default browser. Edge ships with Windows 10/11 and
Server 2022; on older servers Edge or Chrome has to be installed (documented).

### D-1002 No console window on double click
The exe stays a console-subsystem program (CLI, service and installer calls need the exit codes and output). When
the console was created only for it (GetConsoleProcessList == 1, i.e. double click), it calls FreeConsole and shows
start errors in a message box; started from a command prompt it behaves as before. The window can be closed while the
program keeps processing mail; "Kilépés a programból" (POST /admin/api/quit, session + CSRF, refused for the Windows
service) stops it.

### D-1003 Thunderbird add-on from the UI
Beállítások shows the add-on status and installs it with one click. Elevated processes (the LocalSystem service, an
elevated app) install in-process; otherwise the exe runs itself with `install-addon` and the `runas` verb, so Windows
shows the UAC prompt; a declined prompt (ERROR_CANCELLED) and the install-addon exit codes become Hungarian messages.
Only policy mode is offered (it also hands over the server URL and token); profile mode stays a CLI option.

### D-1004 Ollama setup wizard
Beállítások gets a guided Ollama setup: detection (`/api/version`, `/api/tags`), installation (download of
`https://ollama.com/download/OllamaSetup.exe`, Authenticode check: valid and signed by Ollama, then the Inno Setup
installer with `/SILENT`, then waiting for the API and starting the Ollama app if needed), model choice (curated list
with sizes and memory needs, a recommendation from the physical memory, installed models, free name validated by a
pattern), model download through `/api/pull` with streamed progress, and switching the settings to the model through
the normal settings validation. Installing is offered only on Windows when the program was started by a person: a
service cannot start an installer on the user's desktop (it shows the download link instead); model downloads work in
both cases. One background task at a time; the UI polls its state. The model list is a recommendation only: names
change in the Ollama library, so any name can be entered and a missing one gives a Hungarian hint.

### D-1005 Model list of the wizard (1.2.1)
The curated list of 1.2.0 still offered the Qwen3 names. Checked against the Ollama library in November 2026 (Qwen3.5
2B/4B/9B/27B, Gemma 4 E4B with their download sizes); recommendation by memory: 14 GB and more qwen3.5:9b, from 7 GB
qwen3.5:4b, below qwen3.5:2b. The 27B model is listed but never recommended automatically (too slow on a processor).

## Version 1.3.0

### D-1101 Model catalog shipped with the program
"Always the newest models for this machine": ollama.com has no stable public API to list and rank models, and
scraping it would break silently. The program ships `Admin/ollama-models.json` (name, size, parameters, estimated
Hungarian quality, current generation, successors of older models), embedded in the exe and updated with every
release. The Hungarian quality score is our estimate and is shown as such. Automatic catalog updates without a
release stay an open idea (FEJLESZTESI_OTLETEK.md).

### D-1102 Fit and speed estimate
Fit: model needs about 1.2 × download size + 1 GB; fits on the graphics card if that fits its dedicated memory,
otherwise "fits" up to 60 % and "tight" up to 85 % of the physical memory. Speed: generation tokens/s ≈ effective
memory bandwidth / model size (50 GB/s processor, 250 GB/s dedicated card; prompt ≈ 12× resp. 30× faster), one email
= 3000 prompt + 600 answer tokens. Measured values win: the quick test (Ollama's own eval/prompt_eval timings, saved
in `data/model-benchmarks.json`) and the median speed of real jobs; one measured model also calibrates the bandwidth
for the others. Tags: "ajánlott" = best Hungarian quality within ~4 minutes per email, "gyorsabb", "pontosabb".
Integrated graphics (Intel UHD/Iris/Arc Graphics, AMD Radeon Graphics) count as no card.

### D-1103 Model management
Delete is independent of updating and supports several models at once (Ollama `DELETE /api/delete`). The model in
use is refused unless a switch target is given; the UI picks another downloaded model and asks first. Outdated =
listed under a successor pattern in the catalog; "Frissítés" downloads the successor, switches the settings and
offers to delete the old one.

### D-1104 Performance page and LLM usage per job
A background sampler (2 s, 15 minutes kept) reads processor and memory load (Windows: GetSystemTimes,
GlobalMemoryStatusEx; Linux: /proc) and graphics load from the Windows "GPU Engine" performance counters (PDH; null
where unavailable, NPU not measured), and asks Ollama `/api/ps` every 6 s which model is loaded and how much of it is
on the graphics card. Hardware facts come from the registry (processor name, display adapters with dedicated
memory). Jobs store prompt/output tokens, generation tokens/s and the model (migration 3). Warnings: processor above
90 % for a minute, under 10 % free memory, model larger than the machine, model on the processor although a card
exists, partly offloaded model, average job time over 5 minutes.

