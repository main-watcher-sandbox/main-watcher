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
# It also publishes the workflow variants some scenarios point a target's caller at, each a branch
# made from the published main with one change (sandbox/README.md, "Workflow variants"), so they
# never fall behind the workflow they vary.
#
# Usage: sandbox/publish-public.sh
#
# Commits the current checkout's files to main of the gate repo, and each variant to its branch.
# Safe to re-run.
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
# A Windows checkout has CRLF line ends; the edits below and the variants anchor on line ends.
sed -i 's/\r$//' "$workflow"
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

# The variants. Each edit is checked, so a change to the workflow that breaks one fails here
# rather than in the middle of a scenario.
variant() { # branch, description; the edit is applied to $workflow by the caller before this
  git add -A
  git diff --cached --quiet && { echo "variant $1: the edit changed nothing" >&2; exit 1; }
  git commit -qm "Variant $1 of MainWatcher@$source_sha: $2"
  if [ "$(git rev-parse "HEAD^{tree}")" = "$(git rev-parse -q --verify "origin/$1^{tree}" 2>/dev/null || true)" ]; then
    echo "Variant $1 already matches"
  else
    git push -qf origin "HEAD:refs/heads/$1"
    echo "Published variant $1"
  fi
  git checkout -q main
}
git fetch -q origin '+refs/heads/ts-s16-*:refs/remotes/origin/ts-s16-*' || true

# TS-S16 (c): the test step renamed, so the Reporter's contract is broken.
git checkout -q -B ts-s16-renamed-step main
sed -i 's/^      - name: main-watcher-test$/      - name: main-watcher-tests-renamed/' "$workflow"
variant ts-s16-renamed-step "the test step is named main-watcher-tests-renamed"

# TS-S16 (e): a timeout-minutes on the test step itself, shorter than the target's timeout.
git checkout -q -B ts-s16-step-timeout main
sed -i '/^        id: test$/a\        timeout-minutes: 1' "$workflow"
variant ts-s16-step-timeout "the test step has timeout-minutes: 1"

# TS-S16 (f): the report job waits for a runner label no runner has.
git checkout -q -B ts-s16-report-stuck main
awk '/^    runs-on: [$][{][{] inputs.runs-on [}][}]$/ && ++n == 2 { print "    runs-on: sandbox-no-such-runner"; next } { print }' \
  "$workflow" > "$workflow.new" && mv "$workflow.new" "$workflow"
variant ts-s16-report-stuck "the report job runs on sandbox-no-such-runner"

# TS-S16 (g): the test job waits its turn in a concurrency group that the target's sandbox-hold
# workflow holds for as long as a scenario asks, so it starts minutes after its check run.
git checkout -q -B ts-s16-held-job main
awk '/^    runs-on: [$][{][{] inputs.runs-on [}][}]$/ && ++n == 1 {
  print; print "    concurrency:"; print "      group: sandbox-hold-${{ github.repository }}"; next } { print }' \
  "$workflow" > "$workflow.new" && mv "$workflow.new" "$workflow"
variant ts-s16-held-job "the test job waits for the sandbox-hold concurrency group"

# TS-S16 (h): a job timeout far above the target's timeout plus the margin, so a job that never
# ends reaches Main Watcher's run deadline before GitHub's own job timeout ends it.
git checkout -q -B ts-s16-long-timeout main
sed -i 's/^    timeout-minutes: [$][{][{] fromJSON(.*$/    timeout-minutes: 120/' "$workflow"
variant ts-s16-long-timeout "the test job has timeout-minutes: 120"
