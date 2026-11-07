#!/usr/bin/env python3
"""Does the bookkeeping of a release, so a release costs merges and approvals rather than hand edits.

A release keeps every control from plan.md's "Releasing" section: D23's signed image tags, D45's list of released
commits and a test before callers move. This script only does the steps in between:

  status            what's released, deployed and pinned, and the next step
  pin TAG           after `image-v*` passes: verify its signature, then open a PR pinning its digest in infra/
                    (with --workflow, in the reusable workflow too)
  trust             after the pin merges: open a PR adding main's commit to infra/released-commits.txt, and
                    retiring released commits no caller pins any more
  tag VERSION       after trust is applied and tested: tag vVERSION at the newest released commit, signed
  repin VERSION     open a PR in each caller repo moving it to vVERSION; with --standards COMMIT, its
                    .tf-review.yml moves to that tf-standards commit in the same PR
  standards [COMMIT]  open a PR moving tf-review's own standards pins (its .tf-review.yml, CI and evals) to a
                    tf-standards commit, main's by default
  seeds             merge the demo's main into each seed branch, so GitHub keeps running their workflows

Run it outside the Claude sandbox: it needs gh, git signing, crane and cosign. Merges stay the user's.
"""

import argparse
import base64
import json
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

OWNER = "gergodrazsdik"
REPO = f"{OWNER}/tf-review"
DEMO = f"{OWNER}/tf-review-demo"
# Repos that call the reusable workflow; bootstrap's repos map onboards them.
CALLERS = (DEMO, f"{OWNER}/tf-review-demo2")
CALLER_WORKFLOW = ".github/workflows/plan.yml"
CALLER_CONFIG = ".tf-review.yml"
STANDARDS = f"{OWNER}/tf-standards"
# The standards commit a .tf-review.yml reviews against.
STANDARDS_PIN = re.compile(r"(?m)^(\s+commit: )([0-9a-f]{40})")
IMAGE = f"ghcr.io/{REPO}"
ROOT = Path(__file__).resolve().parent.parent
APPS = ROOT / "infra" / "apps.tf"
WORKFLOW = ROOT / ".github" / "workflows" / "tf-review.yml"
RELEASED = ROOT / "infra" / "released-commits.txt"
CONFIG = ROOT / CALLER_CONFIG
# tf-review's own copies of its standards pin: its review, CI's policy tests, the live evals and the image smoke test.
STANDARDS_FILES = (
    CONFIG,
    ROOT / ".github" / "workflows" / "ci.yml",
    ROOT / ".github" / "workflows" / "e2e-live.yml",
    ROOT / "tests" / "fixtures" / "tf-review-smoke.yml",
)
USES = re.compile(r"(tf-review/\.github/workflows/tf-review\.yml@)([0-9a-f]{40})( # )(v[0-9][^\s]*)")
DIGEST = re.compile(rf"{re.escape(IMAGE)}@sha256:[0-9a-f]{{64}}")


def run(*arguments, capture=True, attempts=1):
    """Runs a command and exits on failure. Reads retry, because GitHub's API times out from here now and then."""
    error = ""
    for attempt in range(attempts):
        done = subprocess.run(arguments, capture_output=capture, text=True, check=False)
        if done.returncode == 0:
            return done.stdout.strip() if capture else ""
        error = (done.stderr or "").strip()
        if attempt < attempts - 1:
            time.sleep(5)
    sys.exit(f"{' '.join(arguments[:3])} failed: {error[:300]}")


def gh_api(path, *fields, method="GET"):
    arguments = ["gh", "api", "-X", method, path]
    for field in fields:
        arguments += ["-f", field]
    out = run(*arguments, attempts=5 if method == "GET" else 1)
    return json.loads(out) if out else {}


def git(*arguments):
    # Release candidates sort before their release, so the newest tag is the release once there is one.
    return run("git", "-c", "versionsort.suffix=-rc", "-C", str(ROOT), *arguments)


def require_clean_main():
    if git("status", "--porcelain"):
        sys.exit("The working tree has changes; commit or stash them first.")
    git("fetch", "--quiet", "origin", "main", "--tags")
    if git("rev-parse", "HEAD") != git("rev-parse", "origin/main"):
        git("switch", "--quiet", "main")
        git("merge", "--quiet", "--ff-only", "origin/main")


def released_commits():
    lines = RELEASED.read_text().splitlines()
    return [line.strip() for line in lines if line.strip() and not line.startswith("#")]


def caller_pins():
    """(repo, commit, version) each caller's main pins the reusable workflow to."""
    pins = []
    for repo in CALLERS:
        content = gh_api(f"repos/{repo}/contents/{CALLER_WORKFLOW}")
        text = base64.b64decode(content["content"]).decode()
        match = USES.search(text)
        pins.append((repo, match.group(2) if match else None, match.group(4) if match else None))
    return pins


def open_pr(branch, title, body):
    """Commits what's staged on a new branch from main, pushes it and opens the PR."""
    git("switch", "--quiet", "-c", branch)
    with tempfile.NamedTemporaryFile("w", suffix=".txt") as message:
        message.write(f"{title}\n\n{body}\n")
        message.flush()
        run("git", "-C", str(ROOT), "commit", "--quiet", "-F", message.name, capture=False)
    git("push", "--quiet", "-u", "origin", branch)
    url = run("gh", "pr", "create", "-R", REPO, "--head", branch, "--title", title, "--body", body)
    git("switch", "--quiet", "main")
    print(url)


def standards_commit(commit):
    """The full SHA of a tf-standards commit, or of its main when none is given."""
    return gh_api(f"repos/{STANDARDS}/commits/{commit or 'main'}")["sha"]


def caller_file(repo, path, ref="main"):
    content = gh_api(f"repos/{repo}/contents/{path}?ref={ref}")
    return base64.b64decode(content["content"]).decode(), content["sha"]


def branch_exists(repo, branch):
    done = subprocess.run(["gh", "api", f"repos/{repo}/git/ref/heads/{branch}"], capture_output=True, check=False)
    return done.returncode == 0


def wait_for_release(tag):
    print(f"Waiting for the release run of {tag}...")
    for _ in range(90):
        runs = json.loads(run("gh", "run", "list", "-R", REPO, "--workflow", "release.yml", "--branch", tag,
                              "--limit", "1", "--json", "status,conclusion,url", attempts=5))
        if runs and runs[0]["status"] == "completed":
            if runs[0]["conclusion"] != "success":
                sys.exit(f"The release of {tag} didn't pass: {runs[0]['url']}")
            return
        time.sleep(20)
    sys.exit(f"The release of {tag} hasn't finished after 30 minutes.")


def verified_digest(tag):
    """The digest of the image release.yml pushed for this tag, after checking its signature names that tag."""
    digest = run("crane", "digest", f"{IMAGE}:{tag}", attempts=5)
    reference = f"{IMAGE}@{digest}"
    identity = f"https://github.com/{REPO}/.github/workflows/release.yml@refs/tags/{tag}"
    run("cosign", "verify", "--output", "text",
        "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com",
        "--certificate-identity", identity, reference, attempts=5)
    return reference


def status(_):
    git("fetch", "--quiet", "origin", "main", "--tags")
    tags = git("tag", "--list", "--sort=-v:refname", "image-v*", "v*").splitlines()
    image_tags = [tag for tag in tags if tag.startswith("image-v")]
    versions = [tag for tag in tags if tag.startswith("v")]
    deployed = DIGEST.search(APPS.read_text())
    workflow = DIGEST.search(WORKFLOW.read_text())
    print(f"Newest image tag:       {image_tags[0] if image_tags else '-'}")
    print(f"Service runs:           {deployed.group(0) if deployed else '-'}")
    print(f"Workflow image:         {workflow.group(0) if workflow else '-'}")
    print(f"Newest workflow tag:    {versions[0] if versions else '-'}")
    released = released_commits()
    print(f"Released commits:       {', '.join(commit[:7] for commit in released)}")
    for repo, commit, version in caller_pins():
        trusted = "released" if commit in released else "NOT RELEASED"
        print(f"{repo:<35} {version} ({commit[:7] if commit else '-'}, {trusted})")
    newest = standards_commit(None)
    own = STANDARDS_PIN.search(CONFIG.read_text())
    print(f"tf-standards main:      {newest[:7]}")
    print(f"{REPO:<35} standards {own.group(2)[:7] if own else '-'}")
    for repo in CALLERS:
        pin_match = STANDARDS_PIN.search(caller_file(repo, CALLER_CONFIG)[0])
        print(f"{repo:<35} standards {pin_match.group(2)[:7] if pin_match else '-'}")
    behind = git("rev-list", "--count", f"{image_tags[0]}..origin/main") if image_tags else "?"
    print(f"Commits on main since {image_tags[0] if image_tags else '-'}: {behind}")


def pin(options):
    require_clean_main()
    wait_for_release(options.tag)
    reference = verified_digest(options.tag)
    files = [APPS]
    APPS.write_text(re.sub(r"# image-v[0-9][^.\s]*\.[0-9]+\.[0-9]+", f"# {options.tag}",
                           DIGEST.sub(reference, APPS.read_text(), count=1), count=1))
    if options.workflow:
        WORKFLOW.write_text(DIGEST.sub(reference, WORKFLOW.read_text(), count=1))
        files.append(WORKFLOW)
    if not git("status", "--porcelain"):
        sys.exit(f"Everything already pins {options.tag}.")
    git("add", *map(str, files))
    where = "the service and the reusable workflow" if options.workflow else "the service"
    body = (f"Pins {where} to `{options.tag}` (`{reference.split('@')[1]}`), whose signature names "
            f"`release.yml` at that tag (checked with cosign). Opened by `scripts/release.py pin`.")
    if options.workflow:
        body += "\n\nAfter this merges, `scripts/release.py trust` adds its commit to the released list (D45)."
    open_pr(f"release/pin-{options.tag}", f"Pin the service to {options.tag}", body)


def trust(_):
    require_clean_main()
    head = git("rev-parse", "origin/main")
    released = released_commits()
    if head in released:
        sys.exit(f"{head[:7]} is already released.")
    pinned = {commit for _, commit, _ in caller_pins()}
    kept = [commit for commit in released if commit in pinned]
    retired = [commit for commit in released if commit not in pinned]
    header = [line for line in RELEASED.read_text().splitlines() if line.startswith("#")]
    RELEASED.write_text("\n".join([*header, *kept, head]) + "\n")
    git("add", str(RELEASED))
    body = f"Adds `{head}` (main) to the commits callers may pin (D45)."
    if retired:
        body += " Retires " + ", ".join(f"`{commit[:7]}`" for commit in retired) + ", which no caller pins."
    body += ("\n\nAfter the infra apply, test it on a draft demo PR pinned to the commit, then "
             "`scripts/release.py tag VERSION`. Opened by `scripts/release.py trust`.")
    open_pr(f"release/trust-{head[:7]}", f"Release commit {head[:7]} of the reusable workflow", body)


def tag(options):
    git("fetch", "--quiet", "origin", "main", "--tags")
    commit = released_commits()[-1]
    name = f"v{options.version.removeprefix('v')}"
    run("git", "-C", str(ROOT), "tag", "-s", "-m", name, name, commit, capture=False)
    run("git", "-C", str(ROOT), "push", "origin", f"refs/tags/{name}", capture=False)
    print(f"Tagged {name} at {commit[:7]}. Next: scripts/release.py repin {name}")


def repin(options):
    name = f"v{options.version.removeprefix('v')}"
    git("fetch", "--quiet", "origin", "--tags")
    commit = git("rev-list", "-n", "1", name)
    if commit not in released_commits():
        sys.exit(f"{name} ({commit[:7]}) isn't in infra/released-commits.txt, so callers would be refused.")
    standards = standards_commit(options.standards) if options.standards else None
    branch = f"pin-reviewer-{name}"
    for repo in CALLERS:
        # A run that stopped half way (GitHub's API times out from here now and then) left the branch, so a rerun
        # carries on from it: each file is compared with the branch's copy, and only what's missing is written.
        if not branch_exists(repo, branch):
            base = gh_api(f"repos/{repo}/git/ref/heads/main")["object"]["sha"]
            gh_api(f"repos/{repo}/git/refs", f"ref=refs/heads/{branch}", f"sha={base}", method="POST")
        # Each change is its own commit on the PR's branch: the workflow pin, then the standards pin.
        text, sha = caller_file(repo, CALLER_WORKFLOW, branch)
        updated = USES.sub(rf"\g<1>{commit}\g<3>{name}", text)
        changes = [(CALLER_WORKFLOW, updated, sha, f"Pin the reviewer to {name}")] if updated != text else []
        if standards:
            text, sha = caller_file(repo, CALLER_CONFIG, branch)
            updated = STANDARDS_PIN.sub(rf"\g<1>{standards}", text, count=1)
            if updated != text:
                changes.append((CALLER_CONFIG, updated, sha, f"Review against tf-standards at {standards[:7]}"))
        for path, content, blob, message in changes:
            # Commits made through the API are signed by GitHub, which the main ruleset requires.
            gh_api(f"repos/{repo}/contents/{path}", f"message={message}",
                   f"content={base64.b64encode(content.encode()).decode()}", f"sha={blob}",
                   f"branch={branch}", method="PUT")
        ahead = gh_api(f"repos/{repo}/compare/main...{branch}")["ahead_by"]
        if not ahead:
            print(f"{repo} already pins {name}" + (f" and tf-standards {standards[:7]}." if standards else "."))
            gh_api(f"repos/{repo}/git/refs/heads/{branch}", method="DELETE")
            continue
        existing = json.loads(run("gh", "pr", "list", "-R", repo, "--head", branch, "--json", "url", attempts=5))
        if existing:
            print(existing[0]["url"])
            continue
        body = f"Moves the reusable workflow to `{name}` (`{commit}`), which tf-review released (D45)."
        if standards:
            body += f" Reviews against tf-standards at `{standards}`."
        title = f"Pin the reviewer to {name}" + (" and its standards" if standards else "")
        url = run("gh", "pr", "create", "-R", repo, "--head", branch, "--title", title,
                  "--body", body + " Opened by `scripts/release.py repin`.")
        print(url)
    print("After the demo's PR merges: scripts/release.py seeds")


def standards_pin(options):
    require_clean_main()
    commit = standards_commit(options.commit)
    current = STANDARDS_PIN.search(CONFIG.read_text())
    if not current:
        sys.exit(f"{CALLER_CONFIG} has no standards commit.")
    old = current.group(2)
    if old == commit:
        sys.exit(f"tf-review already reviews against tf-standards {commit[:7]}.")
    for path in STANDARDS_FILES:
        text = path.read_text()
        if old not in text:
            sys.exit(f"{path.relative_to(ROOT)} doesn't pin {old[:7]}; move it by hand.")
        path.write_text(text.replace(old, commit))
    git("add", *map(str, STANDARDS_FILES))
    body = (f"Moves tf-review's standards pins from `{old}` to `{commit}`: its own review (`{CALLER_CONFIG}`), "
            f"CI's policy tests, the live evals and the image smoke test. Callers move with "
            f"`scripts/release.py repin VERSION --standards {commit[:7]}`. Opened by `scripts/release.py standards`.")
    open_pr(f"release/standards-{commit[:7]}", f"Review against tf-standards at {commit[:7]}", body)


def seeds(_):
    pulls = gh_api(f"repos/{DEMO}/pulls?state=open&per_page=100")
    conflicts = []
    for pull in sorted(pulls, key=lambda pull: pull["head"]["ref"]):
        branch = pull["head"]["ref"]
        if not branch.startswith("seed/"):
            continue
        done = subprocess.run(["gh", "api", "-X", "POST", f"repos/{DEMO}/merges", "-f", f"base={branch}",
                               "-f", "head=main", "-f", f"commit_message=Merge main into {branch}"],
                              capture_output=True, text=True, check=False)
        if done.returncode == 0:
            print(f"{branch}: {'merged main' if done.stdout.strip() else 'up to date'}")
        else:
            conflicts.append(branch)
            print(f"{branch}: CONFLICTS, merge main into it by hand")
    if conflicts:
        sys.exit(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(required=True)
    commands.add_parser("status").set_defaults(run=status)
    pin_parser = commands.add_parser("pin")
    pin_parser.add_argument("tag", help="the image-v* tag whose release passed")
    pin_parser.add_argument("--workflow", action="store_true", help="pin the reusable workflow's image too")
    pin_parser.set_defaults(run=pin)
    commands.add_parser("trust").set_defaults(run=trust)
    tag_parser = commands.add_parser("tag")
    tag_parser.add_argument("version", help="like 0.0.13")
    tag_parser.set_defaults(run=tag)
    repin_parser = commands.add_parser("repin")
    repin_parser.add_argument("version", help="like 0.0.13")
    repin_parser.add_argument("--standards", metavar="COMMIT", help="also move the callers to this tf-standards commit")
    repin_parser.set_defaults(run=repin)
    standards_parser = commands.add_parser("standards")
    standards_parser.add_argument("commit", nargs="?", help="a tf-standards commit; main's by default")
    standards_parser.set_defaults(run=standards_pin)
    commands.add_parser("seeds").set_defaults(run=seeds)
    options = parser.parse_args()
    if options.run is pin and not options.tag.startswith("image-v"):
        sys.exit("pin takes an image-v* tag.")
    options.run(options)


if __name__ == "__main__":
    main()
