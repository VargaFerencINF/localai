# EfMailAgent

Turns free-text pre-order emails into structured CSV rows with a **local** LLM. Two parts run on the same Windows
server:

- **EfMailAgent.exe** (`src/EfMailAgent`): .NET 10 Windows Service + CLI + localhost HTTP API + embedded admin UI. Owns
  the rules, the persistent SQLite queue, the sequential worker, the LLM clients (Ollama, OpenAI-compatible, mock),
  JSON Schema validation and the CSV writer.
- **Thunderbird add-on** (`addon/`): MailExtension (Manifest V3, vanilla JS). Watches new mail, pre-filters by subject
  using matchers downloaded from the exe, submits matching mail, and tags messages with the job status.

`CLAUDE.md` is the specification, `docs/DECISIONS.md` records every deviation and design decision, `docs/PROGRESS.md`
is the step log, and `docs/USER_GUIDE_HU.md` is the operator manual (Hungarian).

## Architecture

```
 Thunderbird + add-on  --POST /api/jobs (X-EfMailAgent-Token)-->  EfMailAgent.exe (127.0.0.1:47811)
                       <--GET /api/matchers, /api/jobs/{id}----     rules (YAML + Markdown, hot reload)
                                                                    queue (SQLite, survives restarts)
                                                                    worker (one LLM call at a time)
                                                                       |  JSON constrained by a JSON Schema
                                                                       v
                                                                    Ollama / LM Studio (local)
                                                                       |
                                                                    CSV (UTF-8 BOM, ';', CRLF) in output\
```

Key points:

- The model returns JSON constrained by a schema generated from the rule's `columns`; the exe validates it (one repair
  call on failure) and writes the CSV itself. The model never produces CSV.
- The exe is the source of truth for rules; the add-on only pre-filters, the exe re-checks on submit.
- Email content is untrusted data: it is wrapped in `<email>` delimiters (delimiter look-alikes neutralised), hidden
  and control characters are stripped, the schema is enforced server-side, and text cells that would start an
  Excel formula are prefixed with `'`.
- LLM outages pause the queue without consuming attempts; transport errors retry with 30 s / 2 min / 10 min backoff.
- Windows-only code (service, registry, `policies.json`, installer) sits behind interfaces and
  `OperatingSystem.IsWindows()` guards so everything else builds and tests on Linux.

Source map:

| Path | Contents |
|---|---|
| `src/EfMailAgent/Rules`, `Matching` | rule loading, validation, schema builder, subject normalisation |
| `src/EfMailAgent/Jobs` | SQLite database, migrations, queue |
| `src/EfMailAgent/Llm` | `ILlmClient`, Ollama, OpenAI-compatible, mock provider |
| `src/EfMailAgent/Processing` | prompt builder, sanitiser, validator, processor, worker, daily summary |
| `src/EfMailAgent/Output` | CSV formatter and lock-tolerant writer |
| `src/EfMailAgent/Api`, `Admin` | HTTP API, admin UI backend and embedded static files (`Admin/wwwroot`) |
| `src/EfMailAgent/Addon`, `Service` | add-on installation (`policies.json`, profiles), Windows Service management |
| `src/EfMailAgent/Cli` | command line (`--help` lists every command) |
| `addon/src/core` | add-on logic as pure, testable modules; `addon/src/background.js` is the thin wiring |
| `samples/` | demo rules, emails, mock LLM answers, expected CSV |
| `shared/normalization-cases.json` | subject normalisation vectors used by both the C# and the JS tests |
| `installer/EfMailAgent.iss` | Inno Setup 6 installer |

## Prerequisites

- .NET SDK 10 (`global.json`), Node.js 20+ (add-on tests and XPI packaging).
- Windows builds of the installer: Inno Setup 6 (`iscc` on `PATH` or in the default install folder).

## Build

Windows, full build (tests, XPI, single-file exe, installer):

```powershell
.\build.ps1
```

Output in `dist\`: `EfMailAgent.exe`, `efmailagent.xpi`, `EfMailAgent-Setup.exe`. Options: `-SkipTests`,
`-AddonOnly`, `-SkipInstaller`, `-Sign -CertificateThumbprint <thumbprint>`.

Any OS, without the installer:

```bash
dotnet build
node scripts/package-addon.mjs            # dist/efmailagent.xpi
dotnet publish src/EfMailAgent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Test

```bash
dotnet test                                # xUnit
cd addon && node --test "test/*.test.js"  # add-on, with a fake messenger API and a fake exe
bash scripts/e2e-sim.sh                    # exe + simulated Thunderbird + mock LLM (Windows: scripts\e2e-sim.ps1)
node scripts/ui-test.mjs                   # admin UI usability test in Chromium (needs Playwright; --url/--token for a running service)
```

## Run locally (development)

```bash
dotnet run --project src/EfMailAgent -- --data-dir ./.devdata
```

The first run creates the data folder with a token and the sample rules. Set `Llm.Provider` to `mock` in
`.devdata/appsettings.json` to use the simulated answers in `samples/mock-responses` (see `docs/DEV_MODE.md`). Admin UI:
`http://127.0.0.1:47811/admin` (log in with the token from `print-token`). Submit a sample email with
`node scripts/dev-submit.mjs` or `scripts/dev-submit.ps1`.

Useful commands: `validate-rules`, `test-rule <rule> <email file>`, `benchmark <rule>`, `print-token`,
`addon-status`.

## Release

1. `node scripts/set-version.mjs x.y.z` (updates `VERSION` and `addon/manifest.json` together).
2. Add a `## x.y.z` section to `docs/RELEASE_NOTES.md`, update `docs/PROGRESS.md`, commit and push to the default
   branch.
3. The GitHub Actions workflow (`.github/workflows/build.yml`) builds and tests on Linux and Windows, runs the
   simulation, the exe smoke tests and the admin UI test, installs the setup with parameters into a machine with a
   (fake) Thunderbird, runs the UI test against the installed service, upgrades and uninstalls. When everything is
   green and `vx.y.z` does not exist yet, the `release` job creates the tag and the GitHub Release with
   `EfMailAgent-Setup.exe`, `EfMailAgent.exe`, `efmailagent.xpi` and `SHA256SUMS.txt`.
4. Verify on the target server with `docs/WINDOWS_ACCEPTANCE.md`.

Upgrades keep `C:\ProgramData\EfMailAgent` (settings, token, rules, queue, output) untouched.
