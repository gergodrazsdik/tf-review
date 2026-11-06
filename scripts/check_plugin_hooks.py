#!/usr/bin/env python3
"""Runs the Claude Code plugin's hooks on known inputs, so a change to their patterns can't quietly let an apply
through or start blocking a plan. Part of `just lint`. The format check runs only where tofu is installed."""

import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

PLUGIN = Path(__file__).resolve().parent.parent / "plugin"

BLOCKED = [
    "tofu apply",
    "tofu -chdir=infra apply -auto-approve",
    "cd infra && terraform destroy",
    "TF_LOG=debug tofu apply plan.out",
    "(tofu apply)",
    "tofu import azurerm_resource_group.a /subscriptions/x",
    "tofu state rm azurerm_subnet.a",
    "tofu -chdir=infra state mv a b",
    "tofu workspace delete dev",
    "tofu taint azurerm_subnet.a",
    "tofu force-unlock 1234",
    "tofu refresh",
    "tofu plan && tofu apply",
    "echo start\ntofu apply",
    "x=$(tofu apply)",
    "/usr/bin/tofu apply",
    "./terraform destroy",
    "~/.local/bin/tofu apply",
    "\\tofu apply",
    '"tofu" apply',
    "'tofu' apply",
    "tofu 'apply'",
    "tofu \\\n  apply",
    "tofu -chdir=infra \\\n  apply -auto-approve",
    "T=tofu; $T apply",
    "$TF apply -auto-approve",
    "tofu${IFS}apply",
    "xargs tofu apply",
    "echo apply | xargs tofu",
    "env TF_LOG=debug tofu apply",
    "sudo terraform destroy",
    'tofu -chdir="a b" apply',
    "tofu test",
    "tofu init -migrate-state -force-copy",
    "terraform init -migrate-state",
    "tofu workspace new feature",
    "tofu untaint azurerm_subnet.a",
    "tofu state push state.json",
    "terraform.exe apply",
    "sh -c 'tofu apply'",
    'bash -lc "cd infra && tofu destroy"',
    "eval 'tofu apply'",
    "`tofu apply`",
    "tofu apply 'unbalanced",
]

ALLOWED = [
    "tofu plan",
    "tofu -chdir=infra plan -out plan.out",
    "tofu init -backend=false",
    "tofu validate",
    "tofu fmt -check -recursive",
    "tofu state list",
    "tofu state show azurerm_subnet.a",
    "tofu workspace select dev",
    "tofu show -json plan.out",
    "tofu plan && echo apply when approved",
    "git commit -m 'Add a subnet'",
    "opentofu-docs apply",
    "grep -r tofu .",
    "tofu init -reconfigure",
    'tofu -chdir="a b" plan',
    "/usr/bin/tofu plan",
    "git commit -m 'Run tofu apply in CI only'",
    "echo 'tofu plan' 'unbalanced",
    "tofu test-results.txt",
]

# Hook input that can't be read is blocked, because the hook can't say the command is safe.
UNREADABLE = ["not json", '{"tool_input": {"command": 42}}', '["a list"]', ""]


def run(script, event):
    return run_text(script, json.dumps(event))


def run_text(script, text):
    done = subprocess.run(
        [sys.executable, str(PLUGIN / "scripts" / script)],
        input=text,
        capture_output=True,
        text=True,
        check=False,
    )
    return done.returncode, done.stderr


def main():
    failed = 0
    for text in UNREADABLE:
        code, _ = run_text("guard.py", text)
        ok = code == 2
        failed += not ok
        print(f"{'ok  ' if ok else 'FAIL'} {'blocked' if code == 2 else 'allowed'} unreadable input: {text!r}")
    for command, expected in [(c, 2) for c in BLOCKED] + [(c, 0) for c in ALLOWED]:
        code, _ = run("guard.py", {"tool_name": "Bash", "tool_input": {"command": command}})
        verdict = "blocked" if code == 2 else "allowed"
        ok = code == expected
        failed += not ok
        print(f"{'ok  ' if ok else 'FAIL'} {verdict}: {command}")

    if shutil.which("tofu") is None:
        print("skip format check: tofu isn't installed")
    else:
        with tempfile.TemporaryDirectory() as work:
            unformatted = Path(work) / "main.tf"
            unformatted.write_text('resource "a" "b" {\nname="x"\n}\n')
            formatted = Path(work) / "ok.tf"
            formatted.write_text('resource "a" "b" {\n  name = "x"\n}\n')
            for path, expected in [(unformatted, 2), (formatted, 0), (Path(work) / "notes.md", 0)]:
                code, _ = run("fmt_check.py", {"tool_name": "Edit", "tool_input": {"file_path": str(path)}})
                ok = code == expected
                failed += not ok
                print(f"{'ok  ' if ok else 'FAIL'} format check {path.name}: exit {code}")

    print(f"{failed} failed.")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
