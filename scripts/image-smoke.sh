#!/usr/bin/env bash
# Runs the image's commands under the constraints the reusable workflow uses, and checks the output.
# Usage: scripts/image-smoke.sh <image>
set -euo pipefail

image=$1
commit=0123456789abcdef0123456789abcdef01234567
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

cp tests/fixtures/plan-smoke.json "$work/plan.json"
chmod 777 "$work"

run() {
  docker run --rm --read-only --tmpfs /tmp -v "$work:/work" "$@"
}

run "$image" redact /work/plan.json /work/counts.json
jq -e '.resource_changes == 2 and .actions.create == 1' "$work/counts.json"
if grep -q azurerm "$work/counts.json"; then
  echo "plan content leaked into the counts" >&2
  exit 1
fi

run "$image" review /work/counts.json /work/result.json "$commit"
jq -e --arg commit "$commit" '.verdict == "not_reviewed" and .commit == $commit' "$work/result.json"

run --entrypoint conftest "$image" --version

uid=$(run --entrypoint id "$image" -u)
if [ "$uid" = 0 ]; then
  echo "the image runs as root" >&2
  exit 1
fi
