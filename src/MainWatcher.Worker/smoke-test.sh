#!/usr/bin/env bash
# Container smoke test for the trigger worker image (TS-001 §5, #14).
# 1. Without configuration, the container exits at once with a configuration error.
# 2. With valid configuration and an unreachable GitHub API, it stays up and /healthz answers 200.
# Usage: src/MainWatcher.Worker/smoke-test.sh <image>
set -euo pipefail
image="${1:?usage: smoke-test.sh <image>}"
work="$(mktemp -d)"
name="mw-worker-smoke-$$"
cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT

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

echo "2. Valid configuration, unreachable API: /healthz must answer."
# A throwaway key: nothing is ever signed for GitHub.
openssl genrsa -out "$work/app.pem" 2048 2>/dev/null
chmod 644 "$work/app.pem"
docker run -d --name "$name" -p 127.0.0.1:18080:8080 \
  -v "$work/app.pem:/keys/app.pem:ro" \
  -e MW_WATCHER_REPO=owner/watcher -e MW_MAIN_WATCHER_APP_ID=1 \
  -e MW_OBSERVER_APP_ID=2 -e MW_OBSERVER_KEY_FILE=/keys/app.pem \
  -e MW_DOORBELL_APP_ID=3 -e MW_DOORBELL_KEY_FILE=/keys/app.pem \
  -e MW_GITHUB_API_URL=http://127.0.0.1:9/ -e MW_CHECK_PERIOD_SECONDS=10 \
  "$image" >/dev/null
for _ in $(seq 1 30); do
  if body="$(curl -fsS http://127.0.0.1:18080/healthz 2>/dev/null)"; then
    echo "/healthz: $body"
    if [ "$(docker inspect -f '{{.State.Running}}' "$name")" != "true" ]; then
      echo "FAIL: the container stopped." >&2
      docker logs "$name" >&2
      exit 1
    fi
    echo "PASS"
    exit 0
  fi
  sleep 1
done
echo "FAIL: /healthz did not answer within 30 s." >&2
docker logs "$name" >&2
exit 1
