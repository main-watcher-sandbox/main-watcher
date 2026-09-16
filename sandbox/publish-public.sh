#!/usr/bin/env bash
# Publishes the parts of Main Watcher that run in target repos to the public
# main-watcher-sandbox/gate repo: the gate action, the reusable test workflow and its test
# runner action, their .NET projects and the build files they need. Sandbox targets must be
# public (the merge queue needs it on the sandbox org's Free plan), and a public repo cannot
# use an action or reusable workflow from a private one, so these are published on their own.
#
# The published test workflow is the sandbox build: it uses the test runner action from the
# gate repo's main, and has the fail_upload and hang_upload switches (upload-switches.yml)
# inserted before its upload step.
#
# Usage: sandbox/publish-public.sh
#
# Commits the current checkout's files to main of the gate repo. Safe to re-run.
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
  .github/actions/gate .github/actions/test-runner .github/workflows/run-integration-tests.yml \
  src/MainWatcher.Gate src/MainWatcher.TestRunner global.json Directory.Build.props) | tar -xf -

workflow=.github/workflows/run-integration-tests.yml
runner_uses="main-watcher-sandbox/gate/.github/actions/test-runner@main"
sed -i "s#Actium-Group-Corporation/MainWatcher/.github/actions/test-runner@v1#$runner_uses#" "$workflow"
sed -i "/# sandbox: upload switches are inserted here/r $root/sandbox/upload-switches.yml" "$workflow"
grep -q "uses: $runner_uses$" "$workflow"
grep -q "name: sandbox upload switches" "$workflow"

cat > README.md <<README
# Main Watcher sandbox parts

The gate action and the reusable test workflow for Main Watcher sandbox targets, published
from MainWatcher@$source_sha by \`sandbox/publish-public.sh\`. Do not edit here.
README

git add -A
if git diff --cached --quiet; then
  echo "$gate_repo already matches MainWatcher@$source_sha"
else
  git commit -qm "Publish from MainWatcher@$source_sha"
  git push -q origin main
  echo "Published MainWatcher@$source_sha to $gate_repo"
fi
