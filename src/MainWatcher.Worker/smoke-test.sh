#!/usr/bin/env bash
# Container smoke test for the trigger worker image (TS-001 §5, #14).
# 1. Without configuration, the container exits at once with a configuration error.
# 2. With a GitHub that accepts connections and never answers, /healthz still answers 200:
#    the start-up credential check must not hold up the liveness endpoint.
# 3. With a credential GitHub rejects, the container exits 2 instead of running idle.
# Usage: src/MainWatcher.Worker/smoke-test.sh <image>
set -euo pipefail
image="${1:?usage: smoke-test.sh <image>}"
python="$(command -v python3 || command -v python)"
work="$(mktemp -d)"
name="mw-worker-smoke-$$"
stall_port=19091
stall_pid=""
cleanup() {
  docker rm -f "$name" >/dev/null 2>&1 || true
  [ -n "$stall_pid" ] && kill "$stall_pid" 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT

# A throwaway key: it is only ever used to be rejected.
openssl genrsa -out "$work/app.pem" 2048 2>/dev/null
chmod 644 "$work/app.pem"

run_worker() { # <api url> [extra docker args...]
  local api="$1"; shift
  docker run -d --name "$name" -p 127.0.0.1:18080:8080 \
    -v "$work/app.pem:/keys/app.pem:ro" \
    -e MW_WATCHER_REPO=owner/watcher -e MW_MAIN_WATCHER_APP_ID=1 \
    -e MW_OBSERVER_APP_ID=2 -e MW_OBSERVER_KEY_FILE=/keys/app.pem \
    -e MW_DOORBELL_APP_ID=3 -e MW_DOORBELL_KEY_FILE=/keys/app.pem \
    -e MW_GITHUB_API_URL="$api" -e MW_CHECK_PERIOD_SECONDS=10 \
    "$@" "$image" >/dev/null
}

echo "1. No configuration: the worker must fail fast."
set +e
output="$(timeout 60 docker run --rm "$image" 2>&1)"
status=$?
set -e
echo "$output"
if [ "$status" -ne 2 ] || ! grep -q "Configuration error: MW_WATCHER_REPO is required." <<<"$output"; then
  echo "FAIL: expected exit code 2 and a configuration error, got exit code $status." >&2
  exit 1
fi

echo "2. A GitHub that never answers: /healthz must still answer 200."
"$python" - "$stall_port" <<'PY' &
import socket, sys
listener = socket.socket()
listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
listener.bind(("0.0.0.0", int(sys.argv[1])))
listener.listen(16)
held = []
while True:  # accept and hold: every request stalls until its own timeout
    held.append(listener.accept())
PY
stall_pid=$!
run_worker "http://host.docker.internal:$stall_port/" --add-host=host.docker.internal:host-gateway
for _ in $(seq 1 30); do
  if body="$(curl -fsS http://127.0.0.1:18080/healthz 2>/dev/null)"; then break; fi
  sleep 1
done
if [ -z "${body:-}" ]; then
  echo "FAIL: /healthz did not answer within 30 s while GitHub stalled." >&2
  docker logs "$name" >&2
  exit 1
fi
echo "/healthz: $body"
if [ "$(docker inspect -f '{{.State.Running}}' "$name")" != "true" ]; then
  echo "FAIL: the container stopped while GitHub stalled; a stall is transient." >&2
  docker logs "$name" >&2
  exit 1
fi
docker rm -f "$name" >/dev/null
kill "$stall_pid" 2>/dev/null || true
stall_pid=""

echo "3. A credential GitHub rejects: the worker must stop, not run idle."
run_worker "https://api.github.com/"
set +e
status="$(timeout 90 docker wait "$name")"
set -e
logs="$(docker logs "$name" 2>&1)"
echo "$logs" | tail -3
if [ "$status" != "2" ] || ! grep -q "could not authenticate to owner/watcher" <<<"$logs"; then
  echo "FAIL: expected exit code 2 and a rejected credential, got exit code ${status:-none}." >&2
  exit 1
fi

echo "PASS"
