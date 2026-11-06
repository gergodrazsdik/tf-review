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
]


def run(script, event):
    done = subprocess.run(
        [sys.executable, str(PLUGIN / "scripts" / script)],
        input=json.dumps(event),
        capture_output=True,
        text=True,
        check=False,
    )
    return done.returncode, done.stderr


def main():
    failed = 0
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
