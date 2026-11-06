#!/usr/bin/env python3
"""PostToolUse hook on edits: checks an edited .tf, .tofu or .tfvars file with `tofu fmt`.

It only checks: an unformatted or unparseable file is reported to Claude (exit 2, the diff on stderr) to fix, so the
change it makes is one Claude sees. Without tofu on PATH, or for any other file, it does nothing.
"""

import json
import shutil
import subprocess
import sys
from pathlib import Path

SUFFIXES = {".tf", ".tofu", ".tfvars"}


def main():
    try:
        event = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0
    path = (event.get("tool_input") or {}).get("file_path") or ""
    tofu = shutil.which("tofu")
    if not path or Path(path).suffix not in SUFFIXES or tofu is None or not Path(path).is_file():
        return 0

    done = subprocess.run(
        [tofu, "fmt", "-check", "-diff", "-no-color", path], capture_output=True, text=True, timeout=60, check=False
    )
    if done.returncode == 0:
        return 0
    print(f"tofu fmt found problems in {path}; fix them:\n{done.stdout}{done.stderr}".rstrip(), file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
