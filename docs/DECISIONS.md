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

## Planned for later steps (decided now, implemented later)

