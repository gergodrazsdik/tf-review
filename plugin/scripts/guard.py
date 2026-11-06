#!/usr/bin/env python3
"""PreToolUse hook on Bash: blocks OpenTofu and Terraform commands that change real infrastructure or state.

A plugin can't ship permission rules, so this is how the guardrail travels with it. Applying is the repo's CI job
behind its environment approval; planning, validating and formatting stay free. It reads the hook's JSON on stdin,
and exits 2 with the reason on stderr to block, which Claude Code shows to Claude.

It errs towards blocking. The command is split into words the way a shell would (quotes removed, line continuations
joined), so a path such as `/usr/bin/tofu`, a quoted or escaped name and a wrapper such as `env`, `xargs` or `sh -c`
are all seen. `echo tofu apply` is blocked too, and says why. What a shell can't show before running, such as a
variable holding the binary, is blocked when the next word is a blocked subcommand. Input it can't read, and a command
it can't split, are blocked whenever they mention tofu or terraform beside a blocked word.

A hook is best effort: a script or recipe that runs `tofu apply` isn't seen. The real control is that the people and
agents working in a repo hold no credentials that can apply.
"""

import json
import re
import shlex
import sys

BINARIES = {"tofu", "terraform"}

# Subcommands that change infrastructure or state on their own. `test` applies real resources by default.
BLOCKED = {"apply", "destroy", "import", "taint", "untaint", "force-unlock", "refresh", "test"}

# Subcommands that change state only with certain second words.
BLOCKED_PAIRS = {
    "state": {"rm", "mv", "push", "replace-provider"},
    "workspace": {"delete", "new"},
}

# Subcommands that change state only with certain flags: these copy state into a new backend.
BLOCKED_FLAGS = {"init": {"-migrate-state", "-force-copy"}}

# Words that can't be split are checked against these, conservatively.
DANGEROUS = BLOCKED | {"rm", "mv", "push", "replace-provider", "delete", "-migrate-state", "-force-copy"}

# Shell operators end one command and start another. A backtick does too, for this purpose.
OPERATORS = set("();<>|&`")

# Commands that run the rest of their words as a command, and what their own options look like.
WRAPPERS = {"env", "sudo", "xargs", "command", "exec", "nohup", "time", "nice", "timeout", "stdbuf", "doas"}

SHELLS = {"sh", "bash", "zsh", "dash", "ksh"}

MENTION = re.compile(r"(?<![\w-])(tofu|terraform)(?![\w-])")


def binary(word):
    """The tool a word runs, if it's tofu or terraform at any path, with or without .exe."""
    name = word.rsplit("/", 1)[-1].rsplit("\\", 1)[-1]
    name = name.removesuffix(".exe")
    return name if name in BINARIES else None


def words_of(command):
    """The command's words, with operators as their own words, or None if it can't be split."""
    # A backslash before a newline joins the lines, as a shell does; other newlines end a command.
    command = re.sub(r"\\\r?\n", " ", command).replace("\n", " ; ")
    lexer = shlex.shlex(command, posix=True, punctuation_chars="();<>|&`")
    lexer.whitespace_split = True
    lexer.commenters = ""
    try:
        return list(lexer)
    except ValueError:
        return None


def segments(words):
    """The words split into simple commands at each operator."""
    current = []
    for word in words:
        if word and set(word) <= OPERATORS:
            if current:
                yield current
            current = []
        else:
            current.append(word)
    if current:
        yield current


def verdict(name, rest):
    """What's blocked about running tofu or terraform with these words after it, or None."""
    args = [word for word in rest if not word.startswith("-")]
    flags = [word.split("=", 1)[0] for word in rest if word.startswith("-")]
    first = args[0] if args else ""
    second = args[1] if len(args) > 1 else ""
    if first in BLOCKED:
        return f"{name} {first}"
    if second in BLOCKED_PAIRS.get(first, set()):
        return f"{name} {first} {second}"
    if first in BLOCKED_FLAGS and BLOCKED_FLAGS[first] & set(flags):
        return f"{name} {first} {' '.join(sorted(BLOCKED_FLAGS[first] & set(flags)))}"
    return None


def conservative(text):
    """Blocks text that mentions tofu or terraform beside a blocked word, for anything that can't be split."""
    if not MENTION.search(text):
        return None
    for word in re.split(r"[\s;&|()`'\"$={}]+", text):
        if word in DANGEROUS:
            return f"a command mentioning tofu or terraform with `{word}`"
    return None


def blocked(command, depth=0):
    """The blocked invocation in a command, or None."""
    words = words_of(command)
    if words is None:
        return conservative(command)

    for segment in segments(words):
        for index, word in enumerate(segment):
            rest = segment[index + 1 :]
            name = binary(word)
            if name is not None:
                found = verdict(name, rest)
                if found is not None:
                    return found
                # `echo apply | xargs tofu` takes its subcommand from stdin.
                if not [w for w in rest if not w.startswith("-")] and "xargs" in segment[:index]:
                    return f"xargs {name}"
            elif ("$" in word or "`" in word) and MENTION.search(word):
                # Expansion inside the word, such as tofu${IFS}apply: what runs isn't known until then.
                found = conservative(" ".join(segment))
                if found is not None:
                    return found
            elif index == first_word(segment) and word.startswith("$"):
                # A variable as the command, such as `$T apply`: it may hold the binary.
                found = verdict(word, rest)
                if found is not None:
                    return found
            elif word in SHELLS | {"eval"} and depth < 3:
                # `sh -c 'tofu apply'` and `eval 'tofu apply'` run a string as a command.
                for inner in rest:
                    if MENTION.search(inner):
                        found = blocked(inner, depth + 1)
                        if found is not None:
                            return found
    return None


def first_word(segment):
    """Where the command word is, after any VAR=value assignments and wrappers such as env or xargs."""
    for index, word in enumerate(segment):
        if re.match(r"^[A-Za-z_][A-Za-z0-9_]*=", word) or word in WRAPPERS or word.startswith("-"):
            continue
        return index
    return len(segment)


def main():
    try:
        event = json.load(sys.stdin)
        command = (event.get("tool_input") or {}).get("command", "")
        if not isinstance(command, str):
            raise TypeError("command isn't a string")
    except (json.JSONDecodeError, UnicodeDecodeError, AttributeError, TypeError) as error:
        # Fail closed: a hook that can't read its input can't say the command is safe.
        print(
            f"Blocked by tf-review-opentofu: the hook couldn't read its input ({error}), so it can't tell whether "
            "the command applies or changes state.",
            file=sys.stderr,
        )
        return 2
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
