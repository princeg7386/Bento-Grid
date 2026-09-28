#!/usr/bin/env bash
# Start the CycleGuard API and dashboard together. Ctrl+C stops both.
set -euo pipefail

cd "$(dirname "$0")"

API_PORT="${CYCLEGUARD_API_PORT:-5179}"
WEB_PORT="${CYCLEGUARD_WEB_PORT:-5173}"

# The .NET SDK and Node may be installed under the user's home rather than on the system
# path, so add the usual per-user locations before checking.
if [[ -d "$HOME/.dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi

if [[ -s "$HOME/.nvm/nvm.sh" ]]; then
  # shellcheck disable=SC1091
  . "$HOME/.nvm/nvm.sh" >/dev/null 2>&1 || true
fi

for tool in dotnet node npm; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "error: '$tool' is not on your PATH. See the Prerequisites section of README.md." >&2
    exit 1
  fi
done

if [[ ! -d web/node_modules ]]; then
  echo "==> Installing dashboard dependencies (first run only)"
  (cd web && npm install --no-audit --no-fund)
fi

pids=()

cleanup() {
  echo
  echo "==> Stopping CycleGuard"
  for pid in "${pids[@]:-}"; do
    if [[ -n "${pid:-}" ]] && kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null || true
    fi
  done
  wait 2>/dev/null || true
}
trap cleanup EXIT INT TERM

echo "==> API      http://localhost:${API_PORT}  (Swagger at /swagger)"
dotnet run --project src/CycleGuard.Api/CycleGuard.Api.csproj --urls "http://localhost:${API_PORT}" &
pids+=($!)

echo "==> Dashboard http://localhost:${WEB_PORT}"
(cd web && npm run dev -- --port "${WEB_PORT}" --strictPort) &
pids+=($!)

echo

# Wait for the API to answer before telling anyone to open the dashboard, so the first
# thing they see is not four "request failed" panels.
for _ in $(seq 1 60); do
  if curl -sf "http://localhost:${API_PORT}/api/health" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

echo "Open http://localhost:${WEB_PORT} and click 'Simulate last night'."
echo "Press Ctrl+C to stop both."
echo

# Supervise both children and exit as soon as either one dies, so a crashed API does not
# leave a dashboard up that can only show errors.
#
# Deliberately not `wait -n`: macOS ships bash 3.2, where that option does not exist. It
# fails with "wait: -n: invalid option", which fires the EXIT trap and kills both servers
# about a second after they start. See docs/WHAT_BROKE.md.
while true; do
  for pid in "${pids[@]}"; do
    if ! kill -0 "$pid" 2>/dev/null; then
      echo "==> A CycleGuard process exited; shutting the other one down."
      exit 1
    fi
  done
  sleep 1
done
