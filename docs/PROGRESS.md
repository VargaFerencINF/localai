# Progress

| Step | Title | Status |
|---|---|---|
| 1 | Scaffold, configuration, rule loading | **done** |
| 2 | Core pipeline (queue, worker, LLM, validation, CSV) | next |
| 3 | HTTP API | |
| 4 | Thunderbird add-on | |
| 5 | Add-on installation from the exe | |
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
