#!/usr/bin/env bash
# Turns each seeded branch's redacted plan into a committed eval fixture. It takes the artifact the demo's
# own review made, so no raw plan reaches this machine and no Azure login is needed. Artifacts last a day:
# re-run a seed's workflow if one has expired. Runs gh and vscan, so run it outside the Claude sandbox.
set -euo pipefail

demo=gergodrazsdik/tf-review-demo
# C4 isn't here, because it's hand-built (D33). Pass case names to refresh only those.
cases=(c1-open-ssh c2-public-storage c3-owner-role c5-missing-tags c6-bad-name c8-tag-value c9-bad-environment c10-subnet)
if [ "$#" -gt 0 ]; then
  cases=("$@")
fi
guid='[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

for case in "${cases[@]}"; do
  run=$(gh run list -R "$demo" --workflow plan --branch "seed/$case" --status success --limit 1 \
    --json databaseId --jq '.[0].databaseId')
  if [ -z "$run" ]; then
    echo "$case: no successful run" >&2
    exit 1
  fi

  rm -rf "${work:?}/$case"
  gh run download "$run" -R "$demo" -n tf-review-plan -D "$work/$case"
  vscan "$work/$case"

  # Subscription and tenant IDs sit inside resource IDs. Masked in logs, they'd be public here (D30).
  target="tests/fixtures/cases/$case"
  mkdir -p "$target"
  sed -E "s/$guid/00000000-0000-0000-0000-000000000000/g" "$work/$case/redacted.json" >"$target/plan.json"

  if jq -e '.refused == true' "$target/plan.json" >/dev/null; then
    echo "$case: the redactor refused this plan" >&2
    exit 1
  fi

  echo "$case: from run $run"
done
