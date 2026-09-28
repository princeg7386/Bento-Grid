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
echo "Open http://localhost:${WEB_PORT} and click 'Simulate last night'."
echo "Press Ctrl+C to stop both."

wait -n
