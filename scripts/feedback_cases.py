#!/usr/bin/env python3
"""Drafts negative eval cases from findings a reviewer dismissed on the demo's PRs (D56).

Only the demo: its PRs are public, so their plans can become public fixtures. Other repos' dismissals reach only a
metric, by rule. For each PR whose tf-review comment has a ticked dismiss box, this writes a draft to --out: the PR, the
commit reviewed, what was dismissed and what was kept, and the redacted plan if that commit's plan artifact still
exists (artifacts last a day: re-run the PR's plan workflow if it's gone). A person turns a draft into a case under
tests/fixtures/cases, with expected.json holding the kept findings only, so the dismissed one fails the case if it's
reported again. Nothing here writes into the repo. Run it outside the Claude sandbox: it calls gh and vscan.
"""

import argparse
import json
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

DEMO = "gergodrazsdik/tf-review-demo"
APP = "tf-review-gergodrazsdik[bot]"
MARKER = "<!-- tf-review -->"
TICKED = re.compile(r"^- \[[xX]\] Dismiss `(?P<address>[^`]*)` \(`(?P<rule>[^`]*)`\) <!-- tf-review:dismiss [0-9a-f]{16}\b.*-->\s*$", re.MULTILINE)
COMMIT = re.compile(r"for commit `(?P<commit>[0-9a-f]{7,40})`")
ROW = re.compile(r"^\| (?P<severity>[^|]+) \| `(?P<address>[^`]*)` \| (?P<rules>[^|]+) \| `(?P<title>[^`]*)` \|[^|]*\| (?P<source>[^|]+) \|\s*$", re.MULTILINE)
GUID = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")


def try_gh(*arguments):
    """Runs gh, retrying because GitHub's API times out from here now and then. Returns its output, or its error."""
    error = ""
    for attempt in range(5):
        done = subprocess.run(["gh", *arguments], capture_output=True, text=True, check=False)
        if done.returncode == 0:
            return True, done.stdout
        error = done.stderr
        if attempt < 4:
            time.sleep(5)
    return False, error


def gh(*arguments):
    """Runs gh, and stops the script if it keeps failing."""
    ok, output = try_gh(*arguments)
    if not ok:
        sys.exit(f"gh {arguments[0]} failed: {output.strip()[:200]}")
    return output


def api(path):
    return json.loads(gh("api", "--paginate", "--slurp", f"repos/{DEMO}/{path}"))


def first_rule(cell):
    """The row's own rule: a link's text, or the first code span."""
    match = re.search(r"\[([^\]]+)\]|`([^`]+)`", cell)
    return (match.group(1) or match.group(2)) if match else cell.strip()


def plan_for(commit, target):
    """Downloads the redacted plan the PR's plan job made for this commit, if its artifact still exists."""
    runs = json.loads(gh("run", "list", "-R", DEMO, "--workflow", "plan", "--commit", commit, "--status", "success",
                         "--limit", "1", "--json", "databaseId"))
    if not runs:
        return "no successful plan run for this commit: re-run the PR's plan workflow"
    work = target / "artifact"
    downloaded, _ = try_gh("run", "download", str(runs[0]["databaseId"]), "-R", DEMO, "-n", "tf-review-plan", "-D", str(work))
    if not downloaded:
        return "the plan artifact has expired: re-run the PR's plan workflow"
    if subprocess.run(["vscan", str(work)], check=False).returncode != 0:
        return "vscan didn't pass the artifact: look at it before using it"
    # Subscription and tenant IDs sit inside resource IDs; masked in logs, they'd be public in a fixture (D30).
    plan = GUID.sub("00000000-0000-0000-0000-000000000000", (work / "redacted.json").read_text())
    (target / "plan.json").write_text(plan)
    shutil.rmtree(work)
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True, help="a directory outside the repo for the drafts")
    parser.add_argument("--state", default="all", choices=["open", "closed", "all"], help="which PRs to read")
    options = parser.parse_args()

    pulls = [pull for page in api(f"pulls?state={options.state}&per_page=100") for pull in page]
    drafted = 0
    for pull in pulls:
        number = pull["number"]
        comments = [comment for page in api(f"issues/{number}/comments?per_page=100") for comment in page]
        review = next((comment["body"] for comment in comments
                       if comment["user"]["login"] == APP and comment["body"].startswith(MARKER)), None)
        if review is None:
            continue
        dismissed = [match.groupdict() for match in TICKED.finditer(review)]
        if not dismissed:
            continue

        short = COMMIT.search(review)
        commits = [commit for page in api(f"pulls/{number}/commits?per_page=100") for commit in page]
        commit = next((c["sha"] for c in commits if short and c["sha"].startswith(short.group("commit"))), None)
        gone = {(item["address"], item["rule"]) for item in dismissed}
        kept = [
            {"address": row["address"], "rule": first_rule(row["rules"]), "severity": row["severity"].strip(),
             "title": row["title"], "source": row["source"].strip()}
            for row in (match.groupdict() for match in ROW.finditer(review))
            if (row["address"], first_rule(row["rules"])) not in gone
        ]

        target = options.out / f"pr-{number}"
        target.mkdir(parents=True, exist_ok=True)
        problem = plan_for(commit, target) if commit else "the comment's commit isn't one of the PR's"
        draft = {"pull_request": number, "title": pull["title"], "commit": commit, "dismissed": dismissed,
                 "kept": kept, "plan": None if problem else "plan.json", "problem": problem}
        (target / "draft.json").write_text(json.dumps(draft, indent=2) + "\n")
        drafted += 1
        print(f"#{number}: {len(dismissed)} dismissed, {len(kept)} kept"
              + (f"; {problem}" if problem else f"; plan at {target / 'plan.json'}"))

    print(f"{drafted} draft(s) in {options.out}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
