#!/usr/bin/env bash
# TS-S8, credential scope (ARCH-001 §8, ADR-009, ADR-010), and the live half of the TS-001 §6 review checklist.
# The committed half, workflow and manifest contents, is SecurityChecklistTests in MainWatcher.Core.Tests.
#
# Mints an installation token for mw-observer and for mw-doorbell and proves each can do only what ARCH-001 §8
# allows. A call it must not make has to return 403, and a positive control beside it proves the token itself
# works, so a 403 is the permission and not a dead token. Every refused write is sent with a body GitHub would
# reject anyway (no issue title, a branch that does not exist), so a call that is wrongly allowed gets a 422 and
# changes nothing. Then it checks where the keys live, which repositories each App is installed on (R-11), who
# can read the worker's Secret, and that nothing exposes the worker inbound.
#
# The keys are read from the worker's Secret into a private temporary folder and deleted on exit. They are
# never printed.
#
# Usage: sandbox/ts-s8-credential-scope.sh
#   MW_WATCHER_REPO     default main-watcher-sandbox/main-watcher; its targets.yml names the targets
#   MW_NAMESPACE        default main-watcher-sandbox; holds the Secret trigger-worker-keys
#   MW_OBSERVER_APP_ID, MW_DOORBELL_APP_ID   default the sandbox Apps (deploy/worker/sandbox)
#
# Needs curl, openssl, kubectl on the worker's cluster, and gh logged in as a sandbox org admin (secret names
# only are read; organisation secrets need the admin:org scope, and are skipped without it). Exits non-zero if
# any row fails.
set -euo pipefail

watcher="${MW_WATCHER_REPO:-main-watcher-sandbox/main-watcher}"
namespace="${MW_NAMESPACE:-main-watcher-sandbox}"
observer_id="${MW_OBSERVER_APP_ID:-4966600}"
doorbell_id="${MW_DOORBELL_APP_ID:-4966655}"
owner="${watcher%%/*}"
api=https://api.github.com

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
chmod 700 "$work"
failures=0

row() { # result (PASS, FAIL or SKIP), what, detail
  [ "$1" != FAIL ] || failures=$((failures + 1))
  printf '| %s | %s | %s |\n' "$1" "$2" "$3"
}

section() { printf '\n### %s\n\n| Result | Check | Detail |\n| --- | --- | --- |\n' "$1"; }

b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }

jwt() { # app id, key file
  local now header payload
  now="$(date +%s)"
  header="$(printf '{"alg":"RS256","typ":"JWT"}' | b64url)"
  payload="$(printf '{"iat":%d,"exp":%d,"iss":"%s"}' "$((now - 60))" "$((now + 540))" "$1" | b64url)"
  printf '%s.%s.%s' "$header" "$payload" \
    "$(printf '%s.%s' "$header" "$payload" | openssl dgst -sha256 -sign "$2" -binary | b64url)"
}

call() { # token, method, path, [body] -> status code; response in $work/body
  local args=(-sS -o "$work/body" -w '%{http_code}' -X "$2"
    -H "Authorization: Bearer $1" -H 'Accept: application/vnd.github+json' -H 'X-GitHub-Api-Version: 2022-11-28')
  [ $# -ge 4 ] && args+=(-H 'Content-Type: application/json' --data "$4")
  curl "${args[@]}" "$api$3"
}

first() { grep -o "\"$1\": *\"\{0,1\}[^\",}]*" "$work/body" | head -1 | sed 's/.*: *"\{0,1\}//'; }

probe() { # app, token, expected status, method, path, [body]
  local app="$1" token="$2" want="$3" code
  shift 3
  code="$(call "$token" "$@")"
  if [ "$code" = "$want" ]; then
    row PASS "$app: \`$1 $2\`" "$code, as expected${4:+ ($(first message))}"
  else
    row FAIL "$app: \`$1 $2\`" "expected $want, got $code: $(first message)"
  fi
}

installation_token() { # jwt -> token for the App's installation on the watcher repo
  local code
  code="$(call "$1" GET "/repos/$watcher/installation")"
  [ "$code" = 200 ] || { echo "No installation on $watcher (HTTP $code)" >&2; exit 1; }
  code="$(call "$1" POST "/app/installations/$(first id)/access_tokens" '{}')"
  [ "$code" = 201 ] || { echo "Could not mint an installation token (HTTP $code)" >&2; exit 1; }
  first token
}

installed_repos() { # token -> sorted full names
  call "$1" GET '/installation/repositories?per_page=100' > /dev/null
  grep -o '"full_name": *"[^"]*"' "$work/body" | sed 's/.*"\([^"]*\)"$/\1/' | sort
}

# The keys, from the worker's Secret, as the worker mounts them.
for key in observer doorbell; do
  kubectl -n "$namespace" get secret trigger-worker-keys -o "jsonpath={.data.$key\.pem}" | base64 -d > "$work/$key.pem"
  [ -s "$work/$key.pem" ] || { echo "$key.pem missing from $namespace/trigger-worker-keys" >&2; exit 1; }
done

mapfile -t targets < <(gh api "repos/$watcher/contents/targets.yml" -H 'Accept: application/vnd.github.raw' |
  sed -n 's/^ *- *repo: *\([^ #]*\).*/\1/p')
[ "${#targets[@]}" -gt 0 ] || { echo "$watcher's targets.yml lists no targets" >&2; exit 1; }

observer_jwt="$(jwt "$observer_id" "$work/observer.pem")"
doorbell_jwt="$(jwt "$doorbell_id" "$work/doorbell.pem")"
observer="$(installation_token "$observer_jwt")"
doorbell="$(installation_token "$doorbell_jwt")"
none='{"ref":"ts-s8-no-such-ref"}'

echo "TS-S8 credential scope, $(date -u +%Y-%m-%dT%H:%M:%SZ): watcher $watcher, targets ${targets[*]}"

section "mw-observer: reads, never writes or dispatches"
probe mw-observer "$observer" 200 GET "/repos/$watcher/actions/workflows/watch.yml/runs?per_page=1"
probe mw-observer "$observer" 200 GET "/repos/$watcher/issues?per_page=1"
probe mw-observer "$observer" 403 POST "/repos/$watcher/actions/workflows/watch.yml/dispatches" "$none"
probe mw-observer "$observer" 403 POST "/repos/$watcher/issues" '{}'
for target in "${targets[@]}"; do
  probe mw-observer "$observer" 200 GET "/repos/$target/commits/main/check-runs?per_page=1"
  probe mw-observer "$observer" 200 GET "/repos/$target/actions/runs?per_page=1"
  probe mw-observer "$observer" 200 GET "/repos/$target/issues?per_page=1"
  probe mw-observer "$observer" 403 POST "/repos/$target/actions/workflows/main-watcher-tests.yml/dispatches" "$none"
  probe mw-observer "$observer" 403 POST "/repos/$target/check-runs" '{}'
  probe mw-observer "$observer" 403 POST "/repos/$target/issues" '{}'
  probe mw-observer "$observer" 403 POST "/repos/$target/labels" '{}'
  probe mw-observer "$observer" 403 PUT "/repos/$target/contents/ts-s8-probe.txt" '{}'
done

section "mw-doorbell: starts watch.yml, never touches a target"
# 422, not 403: the dispatch is allowed and fails only on the branch that does not exist.
probe mw-doorbell "$doorbell" 422 POST "/repos/$watcher/actions/workflows/watch.yml/dispatches" "$none"
probe mw-doorbell "$doorbell" 200 GET "/repos/$watcher/issues?per_page=1"
probe mw-doorbell "$doorbell" 403 POST "/repos/$watcher/check-runs" '{}'
probe mw-doorbell "$doorbell" 403 PUT "/repos/$watcher/contents/ts-s8-probe.txt" '{}'
for target in "${targets[@]}"; do
  # Not installed there, so no token for the target can be minted at all.
  probe mw-doorbell "$doorbell_jwt" 404 GET "/repos/$target/installation"
  probe mw-doorbell "$doorbell" 403 POST "/repos/$target/actions/workflows/main-watcher-tests.yml/dispatches" "$none"
  probe mw-doorbell "$doorbell" 403 POST "/repos/$target/check-runs" '{}'
  probe mw-doorbell "$doorbell" 403 POST "/repos/$target/issues" '{}'
done

section "Installations (R-11)"
check_installation() { # app, jwt, token, expected repos...
  local app="$1" jwt="$2" token="$3" selection actual expected
  shift 3
  call "$jwt" GET "/repos/$watcher/installation" > /dev/null
  selection="$(first repository_selection)"
  [ "$selection" = selected ] && row PASS "$app installed on selected repositories" "$selection" ||
    row FAIL "$app installed on selected repositories" "repository_selection is $selection"
  actual="$(installed_repos "$token" | paste -sd ' ' -)"
  expected="$(printf '%s\n' "$@" | sort -u | paste -sd ' ' -)"
  [ "${actual,,}" = "${expected,,}" ] && row PASS "$app repositories" "$actual" ||
    row FAIL "$app repositories" "expected $expected, got $actual"
}
check_installation mw-observer "$observer_jwt" "$observer" "$watcher" "${targets[@]}"
check_installation mw-doorbell "$doorbell_jwt" "$doorbell" "$watcher"

section "Where the keys live"
key_names='MAIN_WATCHER|MW_OBSERVER|MW_DOORBELL|PRIVATE_KEY'
reporter="$(gh api "repos/$watcher/environments/reporter/secrets" --jq '.secrets[].name' | paste -sd ' ' -)"
[[ " $reporter " == *" MAIN_WATCHER_PRIVATE_KEY "* ]] && row PASS "main-watcher key in $watcher's reporter environment" "$reporter" ||
  row FAIL "main-watcher key in $watcher's reporter environment" "found: ${reporter:-nothing}"
listed() { # label, gh api path
  local names
  if ! names="$(gh api "$2" --jq '.secrets[].name' 2> "$work/err" | paste -sd ' ' -)"; then
    row SKIP "No Main Watcher key in $1" "could not list: $(head -1 "$work/err")"
  elif grep -qiE "$key_names" <<< "$names"; then
    row FAIL "No Main Watcher key in $1" "found: $names"
  else
    row PASS "No Main Watcher key in $1" "${names:-no secrets}"
  fi
}
listed "$watcher's repository secrets" "repos/$watcher/actions/secrets"
listed "$owner's organisation secrets" "orgs/$owner/actions/secrets"
for target in "${targets[@]}"; do
  listed "$target's repository secrets" "repos/$target/actions/secrets"
  for environment in $(gh api "repos/$target/environments" --jq '.environments[].name'); do
    listed "$target's $environment environment" "repos/$target/environments/$environment/secrets"
  done
done
[ -n "$(gh api "repos/$watcher/environments/reporter" --jq '.protection_rules[]?.type')" ] ||
  echo "(note: the reporter environment has no protection rules)"

section "The worker's Secret and network (Kubernetes)"
readers=()
subjects=(system:anonymous)
for namespace_name in $(kubectl get namespaces -o jsonpath='{.items[*].metadata.name}'); do
  case "$namespace_name" in kube-*) continue ;; esac # the cluster's own controllers read every Secret
  for account in $(kubectl -n "$namespace_name" get serviceaccounts -o jsonpath='{.items[*].metadata.name}'); do
    subjects+=("system:serviceaccount:$namespace_name:$account")
  done
done
for subject in "${subjects[@]}"; do
  if [ "$(kubectl auth can-i get secret/trigger-worker-keys -n "$namespace" --as="$subject" 2> /dev/null)" = yes ]; then
    readers+=("$subject")
  fi
done
[ "${#readers[@]}" -eq 0 ] &&
  row PASS "No service account can read $namespace/trigger-worker-keys" "${#subjects[@]} subjects checked, kube-* namespaces excepted" ||
  row FAIL "No service account can read $namespace/trigger-worker-keys" "readable by ${readers[*]}"
automount="$(kubectl -n "$namespace" get deploy trigger-worker -o jsonpath='{.spec.template.spec.automountServiceAccountToken}')"
[ "$automount" = false ] && row PASS "Worker pod gets no service account token" "automountServiceAccountToken: false" ||
  row FAIL "Worker pod gets no service account token" "automountServiceAccountToken: ${automount:-unset}"
inbound="$(kubectl -n "$namespace" get services,ingresses -o name | paste -sd ' ' -)"
[ -z "$inbound" ] && row PASS "No Service or Ingress in $namespace" "none" ||
  row FAIL "No Service or Ingress in $namespace" "$inbound"

echo
if [ "$failures" -eq 0 ]; then echo "TS-S8: every check passed."; else echo "TS-S8: $failures check(s) failed."; exit 1; fi
