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

## Planned for later steps (decided now, implemented later)

### D-201 (Step 2) Retry semantics and LLM outages
"Max 3 attempts" with three backoff values (30 s, 2 min, 10 min) is read as 1 initial attempt + 3 retries, otherwise
the 10 min value would never be used. When the LLM server itself is unreachable (health check fails), the worker
pauses instead of burning job attempts, so a 30-minute outage (Step 8) does not fail jobs.

### D-301 (Step 3) Message-ID canonical form
Thunderbird's `headerMessageId` has no angle brackets; the expected CSV shows `<fwd-demo-01@...>`. The exe stores and
deduplicates the Message-ID in the canonical `<...>` form (trimmed, brackets added if missing).

### D-302 (Step 3) `/api/matchers` also returns `priority`
The add-on's matching parity tests (shared vectors) need the priority to choose the best rule; the extra field is
backwards compatible.
