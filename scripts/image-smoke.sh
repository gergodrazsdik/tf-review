#!/usr/bin/env bash
# Runs the image's commands under the constraints the reusable workflow uses, and checks the output.
# It downloads the real standards and runs real conftest, so it needs network access.
# Usage: scripts/image-smoke.sh <image>
set -euo pipefail

image=$1
commit=0123456789abcdef0123456789abcdef01234567
pointer=/resource_changes/azurerm_virtual_network.a/change/after/tags
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cp tests/fixtures/plan-smoke.json "$work/plan.json"
cp tests/fixtures/tf-review-smoke.yml "$work/config.yml"
chmod 777 "$work"
chmod 666 "$work"/*

run() {
  docker run --rm --read-only --tmpfs /tmp -v "$work:/work" "$@"
}

run "$image" redact /work/plan.json /work/redacted.json
if grep -q smoke-canary-value "$work/redacted.json"; then
  echo "a secret survived redaction" >&2
  exit 1
fi

run "$image" review /work/redacted.json /work/config.yml /work/result.json "$commit"
jq -e --arg commit "$commit" --arg pointer "$pointer" '
  .verdict == "findings"
  and .commit == $commit
  and .findings[0].rule == "ADR-002"
  and .findings[0].agent == "scanner"
  and .findings[0].evidence[0].plan_pointer == $pointer
  and .not_reviewed[0].scope == "conventions"
' "$work/result.json"

# A plan the redactor refused has to come out as a refusal, not a clean result.
echo '{"refused":true}' >"$work/refused.json"
chmod 666 "$work/refused.json"
run "$image" review /work/refused.json /work/config.yml /work/refused-result.json "$commit"
jq -e '.verdict == "not_reviewed" and .not_reviewed[0].reason == "redaction_refused"' "$work/refused-result.json"

run --entrypoint conftest "$image" --version

uid=$(run --entrypoint id "$image" -u)
if [ "$uid" = 0 ]; then
  echo "the image runs as root" >&2
  exit 1
fi
