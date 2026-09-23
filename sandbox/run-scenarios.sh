#!/usr/bin/env bash
# The scenario suite (TS-001 §5): TS-S1–S18 against the sandbox, from one entry point. It puts HEAD into
# the sandbox (the gate repo, the watcher replica, the worker image and every pool target), runs the
# scenarios side by side on the target pool, and writes a report under sandbox/scenarios/out/. A full
# run that passes, of a commit pushed to GitHub with a clean working tree, posts the scenario-suite
# commit status that release.yml requires; any run that includes TS-S13 posts scenario-suite/ts-s13,
# which a change to the reporter pin needs (ci.yml).
#
# Usage: sandbox/run-scenarios.sh [--only TS-S13,TS-S4] [--no-deploy] [--targets N] [--no-status] [--list]
#        sandbox/run-scenarios.sh capture-jobs [--target sample-target-7] [--repeat 3]
#
# capture-jobs records how GitHub's jobs API reports a test job around its completion (#65), on one
# pool target that nothing else is using, and changes nothing the suite deploys.
#
# Needs: the .NET SDK, gh logged in as a sandbox organisation admin (admin:org, repo, workflow), git,
# docker and kubectl on the cluster that runs the sandbox worker, and bash with curl and openssl for
# TS-S8. See sandbox/README.md, "The scenario suite".
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
exec dotnet run --project "$root/sandbox/scenarios" --configuration Release -- "$@"
