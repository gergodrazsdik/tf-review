#!/usr/bin/env python3
"""Checks that the files other repos keep copies of match their copies, where those repos are checked out alongside.

scripts/install-ci-tools.sh is the same in all three repos, and tf-standards keeps a copy of the corpus frontmatter
schema (`just contracts-sync` there). Part of `just lint`. CI checks out only this repo, so a missing sibling is
skipped quietly; locally the copies are what drift.
"""

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

COPIES = [
    ("scripts/install-ci-tools.sh", "tf-review-demo", "scripts/install-ci-tools.sh"),
    ("scripts/install-ci-tools.sh", "tf-standards", "scripts/install-ci-tools.sh"),
    ("docs/contracts/corpus-frontmatter.schema.json", "tf-standards", "contracts/corpus-frontmatter.schema.json"),
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("siblings", type=Path, nargs="?", default=ROOT.parent, help="where the other repos are")
    options = parser.parse_args()

    failed = 0
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
