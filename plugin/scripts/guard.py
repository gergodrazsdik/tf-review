#!/usr/bin/env python3
"""PreToolUse hook on Bash: blocks OpenTofu and Terraform commands that change real infrastructure or state.

A plugin can't ship permission rules, so this is how the guardrail travels with it. Applying is the repo's CI job
behind its environment approval; planning, validating and formatting stay free. It reads the hook's JSON on stdin,
and exits 2 with the reason on stderr to block, which Claude Code shows to Claude. It errs towards blocking: a
command that only mentions `tofu apply` in an echo is blocked too, and says why.
"""

import json
import re
import sys

BINARY = re.compile(r"(?:^|[\s;&|()`$'\"])(tofu|terraform)(?=\s)")

# Subcommands that change infrastructure or state on their own.
BLOCKED = {"apply", "destroy", "import", "taint", "untaint", "force-unlock", "refresh"}

# Subcommands that change state only with certain second words.
BLOCKED_PAIRS = {
    "state": {"rm", "mv", "push", "replace-provider"},
    "workspace": {"delete"},
}


def subcommand(rest):
    """The first two words after the binary that aren't global flags such as -chdir=dir."""
    words = [word for word in re.split(r"\s+", rest.strip()) if word]
    while words and words[0].startswith("-"):
        words.pop(0)
    return (words[0] if words else ""), (words[1] if len(words) > 1 else "")


def blocked(command):
    """The blocked invocation in a command, or None."""
    for match in BINARY.finditer(command):
        # Up to the next shell separator or subshell edge, so `tofu plan && echo apply` doesn't count and
        # `(tofu apply)` does.
        rest = re.split(r"[;&|()`\n]", command[match.end():], maxsplit=1)[0]
        first, second = subcommand(rest)
        if first in BLOCKED or second in BLOCKED_PAIRS.get(first, set()):
            return f"{match.group(1)} {first}" + (f" {second}" if first in BLOCKED_PAIRS else "")
    return None


def main():
    try:
        event = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0
    command = (event.get("tool_input") or {}).get("command") or ""
    found = blocked(command)
    if found is None:
        return 0
    print(
        f"Blocked by tf-review-opentofu: `{found}` changes real infrastructure or state. Applying is the repo's CI job "
        "behind its environment approval, and state changes are a person's to make. Plan, validate and format instead, "
        "or ask the user to run it.",
        file=sys.stderr,
    )
    return 2


if __name__ == "__main__":
    sys.exit(main())
