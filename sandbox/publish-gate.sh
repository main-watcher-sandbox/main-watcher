#!/usr/bin/env bash
# Publishes the gate action to the public main-watcher-sandbox/gate repo. Sandbox targets
# must be public (the merge queue needs it on the sandbox org's Free plan), and a public
# repo cannot use an action from a private one, so the gate is published on its own:
# only the action, src/MainWatcher.Gate and the build files it needs.
#
# Usage: sandbox/publish-gate.sh
#
# Commits the current checkout's gate files to main of the gate repo. Safe to re-run.
set -euo pipefail

gate_repo="main-watcher-sandbox/gate"
root="$(cd "$(dirname "$0")/.." && pwd)"
source_sha="$(git -C "$root" rev-parse --short HEAD)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

git clone -q "https://github.com/$gate_repo.git" "$work/repo"
cd "$work/repo"
git checkout -q -B main

git rm -rq --ignore-unmatch .
(cd "$root" && tar --exclude=bin --exclude=obj -cf - \
  .github/actions/gate src/MainWatcher.Gate global.json Directory.Build.props) | tar -xf -
cat > README.md <<README
# Main Watcher gate (sandbox)

The gate action for Main Watcher sandbox targets, published from MainWatcher@$source_sha
by \`sandbox/publish-gate.sh\`. Do not edit here.
README

git add -A
if git diff --cached --quiet; then
  echo "$gate_repo already matches MainWatcher@$source_sha"
else
  git commit -qm "Publish the gate from MainWatcher@$source_sha"
  git push -q origin main
  echo "Published the gate from MainWatcher@$source_sha to $gate_repo"
fi
