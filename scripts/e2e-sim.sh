#!/usr/bin/env bash
# End-to-end simulation (docs/DEV_MODE.md section 4): starts EfMailAgent in console mode with the mock LLM, then runs
# the add-on core against it with a simulated Thunderbird. Exit code != 0 on any mismatch.
# Env: EFMAILAGENT_DLL (prebuilt EfMailAgent.dll; default: build Release), E2E_DATA_DIR (default .e2edata), E2E_PORT (47899)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DATA="${E2E_DATA_DIR:-$ROOT/.e2edata}"
PORT="${E2E_PORT:-47899}"
DLL="${EFMAILAGENT_DLL:-}"

if [[ -z "$DLL" ]]; then
  dotnet build "$ROOT/src/EfMailAgent" -c Release --nologo -v quiet
  DLL="$ROOT/src/EfMailAgent/bin/Release/net10.0/EfMailAgent.dll"
fi

rm -rf "$DATA"
mkdir -p "$DATA"
node -e '
  const [dataDir, port, mockDir] = process.argv.slice(1);
  const token = require("crypto").randomBytes(32).toString("base64url");
  const settings = { Server: { Port: Number(port), Token: token },
                     Llm: { Provider: "mock", MockDir: mockDir, MockDelayMs: 300 },
                     Worker: { IdlePollMilliseconds: 200 } };
  require("fs").writeFileSync(dataDir + "/appsettings.json", JSON.stringify(settings, null, 2));
' "$DATA" "$PORT" "$ROOT/samples/mock-responses"

dotnet "$DLL" --data-dir "$DATA" > "$DATA/efmailagent-console.log" 2>&1 &
PID=$!
cleanup() {
  kill "$PID" 2>/dev/null || true
  wait "$PID" 2>/dev/null || true
}
trap cleanup EXIT

if ! node "$ROOT/scripts/simulate-thunderbird.mjs" --data-dir "$DATA"; then
  echo "--- EfMailAgent napló ---"
  cat "$DATA/efmailagent-console.log"
  exit 1
fi
