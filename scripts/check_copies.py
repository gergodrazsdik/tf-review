#!/usr/bin/env python3
"""Checks that copies match their originals: in other repos where those are checked out alongside, and in llm-smoke.cs.

scripts/install-ci-tools.sh is the same in all three repos, and tf-standards keeps a copy of the corpus frontmatter
schema (`just contracts-sync` there). Part of `just lint`. CI checks out only this repo, so a missing sibling is
skipped quietly; locally the copies are what drift.
"""

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

COPIES = [
    ("scripts/install-ci-tools.sh", "tf-review-demo", "scripts/install-ci-tools.sh"),
    ("scripts/install-ci-tools.sh", "tf-standards", "scripts/install-ci-tools.sh"),
    ("docs/contracts/corpus-frontmatter.schema.json", "tf-standards", "contracts/corpus-frontmatter.schema.json"),
]


def raw_string(text, declaration):
    """The JSON in the C# raw string literal that follows a declaration."""
    start = text.index('"""', text.index(declaration)) + 3
    return json.loads(text[start : text.index('"""', start)])


def smoke_problems():
    """llm-smoke.cs is a file-based app, so it repeats ReviewAgent's schema and the package pins: they have to match."""
    smoke = (ROOT / "scripts" / "llm-smoke.cs").read_text()
    problems = []
    agent = (ROOT / "src" / "TfReview.Agents" / "ReviewAgent.cs").read_text()
    if raw_string(smoke, "const string Schema =") != raw_string(agent, "private const string Schema ="):
        problems.append("scripts/llm-smoke.cs doesn't send ReviewAgent's schema: copy it over")
    central = dict(re.findall(r'<PackageVersion Include="([^"]+)" Version="([^"]+)"', (ROOT / "Directory.Packages.props").read_text()))
    for package, version in re.findall(r"^#:package ([^@\s]+)@(\S+)", smoke, re.MULTILINE):
        if central.get(package) != version:
            problems.append(f"scripts/llm-smoke.cs pins {package} {version}, and Directory.Packages.props {central.get(package)}")
    return problems


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("siblings", type=Path, nargs="?", default=ROOT.parent, help="where the other repos are")
    options = parser.parse_args()

    failed = 0
    for problem in smoke_problems():
        print(f"FAIL {problem}")
        failed += 1
    for original, repo, copy in COPIES:
        if not (options.siblings / repo).is_dir():
            continue
        target = options.siblings / repo / copy
        if not target.is_file():
            print(f"FAIL {repo}/{copy} is missing; it's a copy of {original}")
            failed += 1
        elif target.read_bytes() != (ROOT / original).read_bytes():
            print(f"FAIL {repo}/{copy} differs from {original}: change them together")
            failed += 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
