#!/usr/bin/env bash
# Seeds (or resets) a sandbox target repo from sandbox/sample-target and applies the
# merge-queue ruleset on main. Safe to re-run: it commits the template over whatever is
# there and updates the ruleset in place.
#
# Usage: sandbox/seed-target.sh <owner/repo> [--slow]
#   --slow  sets slow_suite_minutes to 5 and turns timed_tests off (the slow-suite variant)
#
# Needs gh logged in as a sandbox org admin: pushing to main bypasses the ruleset.
set -euo pipefail

repo="${1:?usage: seed-target.sh <owner/repo> [--slow]}"
variant="${2:-}"
here="$(cd "$(dirname "$0")" && pwd)"
source_sha="$(git -C "$here" rev-parse --short HEAD)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

gh repo clone "$repo" "$work/repo" -- --quiet
cd "$work/repo"
git checkout -q -B main

# Replace the tree with the template.
git rm -rq --ignore-unmatch .
(cd "$here/sample-target" && tar --exclude=bin --exclude=obj --exclude=TestResults \
  --exclude=.sandbox-empty-packages -cf - .) | tar -xf -

if [ "$variant" = "--slow" ]; then
  sed -i 's/"slow_suite_minutes": 0/"slow_suite_minutes": 5/; s/"timed_tests": true/"timed_tests": false/' sandbox.json
  grep -q '"slow_suite_minutes": 5' sandbox.json
fi

git add -A
if git diff --cached --quiet; then
  echo "$repo already matches the template"
else
  git commit -qm "Seed sandbox target from MainWatcher@$source_sha${variant:+ ($variant)}"
  git push -q origin main
  echo "Pushed template to $repo"
fi

ruleset="$here/rulesets/main-merge-queue.json"
name="$(sed -n 's/^  "name": "\(.*\)",$/\1/p' "$ruleset")"
id="$(gh api "repos/$repo/rulesets" --jq ".[] | select(.name == \"$name\") | .id")"
if [ -n "$id" ]; then
  gh api -X PUT "repos/$repo/rulesets/$id" --input "$ruleset" > /dev/null
  echo "Updated ruleset '$name' ($id)"
else
  gh api -X POST "repos/$repo/rulesets" --input "$ruleset" --jq '"Created ruleset \(.name) (\(.id))"'
fi
