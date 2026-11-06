#!/usr/bin/env bash
# Merges a PR by fast-forwarding main to its head, so its commits land as they were reviewed and signed.
# GitHub marks the PR merged when its head reaches main. The admin bypass in .github/rulesets/main.json allows the
# push, and it also skips the ruleset's checks, so this checks the same things first: the required checks pass,
# every commit is signed by a key you trust, and main hasn't moved past the PR's base.
#
# Run it from the repo the PR is in: ff-merge.sh <pr-number>
set -euo pipefail

pr=${1:?usage: ff-merge.sh <pr-number>}

read -r head base state < <(gh pr view "$pr" --json headRefOid,baseRefName,state \
  --jq '"\(.headRefOid) \(.baseRefName) \(.state)"')
if [[ $state != OPEN || $base != main ]]; then
  echo "PR #$pr is $state against $base; only open PRs against main can be merged here." >&2
  exit 1
fi

# Exit 8 means checks are still running.
gh pr checks "$pr" --required

git fetch --quiet origin main "$head"
if ! git merge-base --is-ancestor origin/main "$head"; then
  echo "main has moved past the PR's base. Rebase the branch onto main and push it again." >&2
  exit 1
fi

for commit in $(git rev-list origin/main.."$head"); do
  if ! git verify-commit "$commit" 2>/dev/null; then
    echo "Commit $commit has no valid signature from a key you trust." >&2
    exit 1
  fi
done

git push origin "$head:refs/heads/main"
if [[ $(git branch --show-current) == main ]]; then
  git merge --quiet --ff-only "$head"
fi
echo "Merged #$pr: main is now ${head:0:7}."
