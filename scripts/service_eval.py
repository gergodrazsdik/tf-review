#!/usr/bin/env python3
"""Compares what the real service posted on the demo's seed PRs with each case's expected findings.

Nothing is replayed here: real plans, scanners, model and App. So a miss can be the model on a bad day (see
docs/evals/baseline.md), and a single one needs reading rather than reverting. Run it outside the Claude sandbox.
"""

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

from comment_table import not_reviewed, rows

DEMO = "gergodrazsdik/tf-review-demo"
APP = "tf-review-gergodrazsdik"
MARKER = "<!-- tf-review -->"
CASES = Path(__file__).resolve().parent.parent / "tests" / "fixtures" / "cases"
POLL_SECONDS = 15


def gh(*arguments):
    """Runs gh, retrying because GitHub's API times out from here now and then."""
    error = ""
    for attempt in range(5):
        done = subprocess.run(["gh", *arguments], capture_output=True, text=True, check=False)
        if done.returncode == 0:
            return done.stdout
        error = done.stderr
        if attempt < 4:
            time.sleep(5)
    sys.exit(f"gh {arguments[0]} failed: {error.strip()[:200]}")


def api(path):
    return json.loads(gh("api", f"repos/{DEMO}/{path}"))


def seeds():
    pulls = api("pulls?state=open&per_page=100")
    return [pull for pull in pulls if pull["head"]["ref"].startswith("seed/")]


def check_run(sha):
    """The App's check on this commit, or None until there is one."""
    runs = api(f"commits/{sha}/check-runs?check_name=tf-review")["check_runs"]
    return next((run for run in runs if run["app"]["slug"] == APP), None)


def comment(pull):
    """The sticky comment, which either identity may have written."""
    comments = api(f"issues/{pull['number']}/comments?per_page=100")
    return next((item["body"] for item in comments if item["body"].startswith(MARKER)), None)


def parse_findings(body):
    """The (resource, rule) each row stands for, including any rules the arbiter folded into a row."""
    findings = set()
    for row in rows(body):
        findings.add((row["address"], row["rule"]))
        findings.update((row["address"], other) for other in row["also"])
    return findings


def judge(pull, body):
    """What's wrong with this PR's review, as lines to print."""
    sha = pull["head"]["sha"]
    case = pull["head"]["ref"].removeprefix("seed/")
    if f"`{sha[:7]}`" not in body:
        return [f"the comment isn't for commit {sha[:7]}"]

    expected_path = CASES / case / "expected.json"
    if not expected_path.is_file():
        return [f"there's no fixture for {case} in {CASES}"]
    expected = json.loads(expected_path.read_text(encoding="utf-8"))
    wanted = {(item["resource_address"], item["rule"]) for item in expected["findings"]}
    posted = parse_findings(body)

    problems = [f"missing: {resource} {rule}" for resource, rule in sorted(wanted - posted)]
    problems += [f"unexpected: {resource} {rule}" for resource, rule in sorted(posted - wanted)]
    problems += not_reviewed(body)
    return problems


def rerun(pulls):
    """Starts each seed's latest workflow again, for testing a service that changed without a push."""
    for pull in pulls:
        runs = json.loads(gh(
            "run", "list", "-R", DEMO, "--workflow", "plan", "--branch", pull["head"]["ref"],
            "--limit", "1", "--json", "databaseId"))
        gh("run", "rerun", str(runs[0]["databaseId"]), "-R", DEMO)


def wait_for_checks(pulls, timeout, since):
    """Waits for each PR's check, newer than `since` if that's given. Returns those still without one."""
    deadline = time.monotonic() + timeout
    waiting = list(pulls)
    while waiting and time.monotonic() < deadline:
        ready = []
        for pull in waiting:
            run = check_run(pull["head"]["sha"])
            if run and run["status"] == "completed" and (since is None or run["completed_at"] > since):
                ready.append(pull)
        waiting = [pull for pull in waiting if pull not in ready]
        if waiting:
            time.sleep(POLL_SECONDS)
    return waiting


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rerun", action="store_true", help="review every seed again first")
    parser.add_argument("--timeout", type=int, default=900, help="seconds to wait for reviews")
    parser.add_argument("--version", help="fail any seed PR that isn't pinned to this reviewer version")
    options = parser.parse_args()

    pulls = sorted(seeds(), key=lambda pull: pull["head"]["ref"])
    if not pulls:
        sys.exit("No open seed PRs.")

    since = None
    if options.rerun:
        since = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        rerun(pulls)

    # GitHub runs no pull_request workflow for a PR that conflicts with its base, and says nothing about it.
    for pull in pulls:
        state = api(f"pulls/{pull['number']}").get("mergeable")
        if state is False:
            sys.exit(f"{pull['head']['ref']} conflicts with main, so GitHub won't review it. Merge main into it first.")

    if options.version:
        for pull in pulls:
            pinned = gh("api", f"repos/{DEMO}/contents/.github/workflows/plan.yml?ref={pull['head']['sha']}",
                        "-H", "Accept: application/vnd.github.raw+json")
            if f"# {options.version}" not in pinned:
                sys.exit(f"{pull['head']['ref']} isn't pinned to {options.version}, so this wouldn't test it.")

    late = wait_for_checks(pulls, options.timeout, since)
    failures = 0
    for pull in pulls:
        name = pull["head"]["ref"].removeprefix("seed/")
        if pull in late:
            problems = [f"no review after {options.timeout} seconds"]
        else:
            body = comment(pull)
            problems = judge(pull, body) if body else ["no comment"]

        print(f"{'FAIL' if problems else 'PASS'}: {name} (#{pull['number']})")
        for problem in problems:
            print(f"  {problem}")
        failures += bool(problems)

    print(f"{len(pulls) - failures} of {len(pulls)} passed.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
