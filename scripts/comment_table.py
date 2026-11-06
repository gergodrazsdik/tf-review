"""Reads the findings table and dismiss boxes out of a tf-review PR comment, for service_eval.py and feedback_cases.py.

The format is CommentRenderer's. tests/fixtures/comments/review.md is what it writes, kept equal by a test in
TfReview.GitHub.Tests, and test_comment_table.py parses that file, so a renderer change shows up in both.
"""

import re

SEVERITIES = ("high", "medium", "low")

# The App and, for a comment the workflow wrote first when the service was down (D39), the workflow's token.
BOT_LOGINS = ("tf-review-gergodrazsdik[bot]", "github-actions[bot]")

TICKED = re.compile(
    r"^- \[[xX]\] Dismiss (?P<address>`+.*?`+) \((?P<rule>`+.*?`+)\) <!-- tf-review:dismiss [0-9a-f]{16}\b.*-->\s*$",
    re.MULTILINE,
)


def cells(line):
    """A table row's cells, split on pipes the renderer didn't escape, or None if the line isn't a row."""
    if not line.startswith("|") or not line.rstrip().endswith("|"):
        return None
    parts, current, escaped = [], [], False
    for char in line.strip()[1:-1]:
        if escaped:
            current.append(char)
            escaped = False
        elif char == "\\":
            current.append(char)
            escaped = True
        elif char == "|":
            parts.append("".join(current).strip())
            current = []
        else:
            current.append(char)
    parts.append("".join(current).strip())
    return parts


def code_text(cell):
    """The text of a code span, as the renderer wrote it: any fence length, padding and escaped pipes undone."""
    match = re.fullmatch(r"(`+)(.*?)\1", cell.strip(), re.DOTALL)
    if not match:
        return cell.strip()
    text = match.group(2)
    if text.startswith(" ") and text.endswith(" ") and text.strip():
        text = text[1:-1]
    return text.replace("\\|", "|")


def rules(cell):
    """The row's own rule (a link's text or the first code span) and any the arbiter folded in after "also"."""
    main, _, also = cell.partition(", also ")
    link = re.match(r"\[([^\]]+)\]", main)
    rule = link.group(1) if link else code_text(main)
    return rule, [code_text(other) for other in re.findall(r"`+[^`]+`+", also)]


def rows(body):
    """Each finding row, as a dict with severity, blocking, address, rule, also, title, suggestion and source."""
    found = []
    for line in body.splitlines():
        parts = cells(line)
        if parts is None or len(parts) != 6:
            continue
        severity, _, flag = parts[0].partition(",")
        severity = severity.strip()
        if severity not in SEVERITIES:
            continue
        rule, also = rules(parts[2])
        found.append(
            {
                "severity": severity,
                "blocking": "blocking" in flag,
                "address": code_text(parts[1]),
                "rule": rule,
                "also": also,
                "title": code_text(parts[3]),
                "suggestion": code_text(parts[4]) if parts[4] else None,
                "source": parts[5],
            }
        )
    return found


def dismissed(body):
    """The (address, rule) of each ticked dismiss box."""
    return [
        {"address": code_text(match.group("address")), "rule": code_text(match.group("rule"))}
        for match in TICKED.finditer(body)
    ]


def not_reviewed(body):
    """The "Not reviewed" lines, for a whole scope or for some of its resources."""
    return re.findall(r"^Not reviewed(?::| by) .*$", body, re.MULTILINE)
