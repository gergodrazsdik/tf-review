# /// script
# dependencies = ["check-jsonschema==0.39.0"]
# ///
"""Checks that the contracts reject what they should, not only that their examples pass."""

import copy
import json
import subprocess
import sys
import tempfile
from pathlib import Path

root = Path(__file__).resolve().parent.parent / "docs/contracts"
finding = json.loads((root / "examples/finding.json").read_text())
result = json.loads((root / "examples/review-result.json").read_text())
chunk = json.loads((root / "examples/reference-chunk.json").read_text())


def changed(base, **fields):
    doc = copy.deepcopy(base)
    doc.update(fields)
    return doc


plan = {
    "plan_pointer": "/resource_changes/azurerm_storage_account.logs/change/after/tags"
}
doc = {"doc_id": "ADR-002"}
scanner_finding = changed(
    finding, agent="scanner", deterministic=True, evidence=[plan], rule="CKV_AZURE_1"
)

# The YAML examples as JSON, which validates the same and needs no YAML parser here.
config = {
    "version": 1,
    "tool_version": "1.13.2",
    "standards": {"repository": "gergodrazsdik/tf-standards", "commit": "0123456789abcdef0123456789abcdef01234567"},
    "agents": ["conventions"],
    "references": True,
    "gate": {"mode": "advisory"},
}
frontmatter = {
    "id": "POL-001",
    "type": "policy",
    "status": "accepted",
    "applies_to": ["azurerm_storage_account"],
    "topics": ["security"],
    "severity": "high",
    "rego": "POL-001-storage.rego",
}


def without(base, field):
    return {k: v for k, v in base.items() if k != field}


cases = [
    # name, schema, document, should it pass
    ("example config", "tf-review-config", config, True),
    ("config with no standards", "tf-review-config", without(config, "standards"), False),
    ("config at version 2", "tf-review-config", changed(config, version=2), False),
    ("config with an unknown agent", "tf-review-config", changed(config, agents=["conventions", "style"]), False),
    ("config naming an agent twice", "tf-review-config", changed(config, agents=["cost", "cost"]), False),
    ("config with an unknown field", "tf-review-config", changed(config, reviewers=["a"]), False),
    ("config with standards at a branch", "tf-review-config", changed(config, standards={"repository": "a/b", "commit": "main"}), False),
    ("config with a short commit", "tf-review-config", changed(config, standards={"repository": "a/b", "commit": "0123456"}), False),
    ("config with a repository URL", "tf-review-config", changed(config, standards={"repository": "https://github.com/a/b", "commit": "0" * 40}), False),
    ("config with a gate that isn't advisory or blocking", "tf-review-config", changed(config, gate={"mode": "strict"}), False),
    ("config with references as a string", "tf-review-config", changed(config, references="yes"), False),
    ("config with a tool version range", "tf-review-config", changed(config, tool_version="~> 1.13"), False),
    ("example frontmatter", "corpus-frontmatter", frontmatter, True),
    ("frontmatter with a lowercase ID", "corpus-frontmatter", changed(frontmatter, id="pol-001"), False),
    ("frontmatter with a two-digit ID", "corpus-frontmatter", changed(frontmatter, id="POL-01"), False),
    ("frontmatter with an unknown status", "corpus-frontmatter", changed(frontmatter, status="draft"), False),
    ("superseded with no successor", "corpus-frontmatter", changed(frontmatter, status="superseded"), False),
    ("superseded with a successor", "corpus-frontmatter", changed(frontmatter, status="superseded", superseded_by="POL-009"), True),
    ("accepted with a successor", "corpus-frontmatter", changed(frontmatter, superseded_by="POL-009"), False),
    ("frontmatter applying to nothing", "corpus-frontmatter", changed(frontmatter, applies_to=[]), False),
    ("frontmatter with an unknown topic", "corpus-frontmatter", changed(frontmatter, topics=["style"]), False),
    ("frontmatter with no severity", "corpus-frontmatter", without(frontmatter, "severity"), False),
    ("frontmatter with an unknown field", "corpus-frontmatter", changed(frontmatter, owner="team-a"), False),
    ("frontmatter with rego that isn't a .rego file", "corpus-frontmatter", changed(frontmatter, rego="POL-001.py"), False),
    ("example finding", "finding", finding, True),
    ("example reference chunk", "reference-chunk", chunk, True),
    ("result with references", "review-result", changed(result, references={"corpus_version": "0123456789abcdef", "provider_docs_missing": False, "attributions": ["HashiCorp, MPL-2.0"]}), True),
    ("result with references and no index", "review-result", changed(result, references={"corpus_version": None, "provider_docs_missing": True, "attributions": []}), True),
    ("result with references missing a field", "review-result", changed(result, references={"corpus_version": None}), False),
    ("reference with no provider version", "reference-chunk", {k: v for k, v in chunk.items() if k != "provider_version"}, True),
    ("reference with an unknown licence", "reference-chunk", changed(chunk, license="CC-BY-NC-4.0"), False),
    ("reference with no licence", "reference-chunk", {k: v for k, v in chunk.items() if k != "license"}, False),
    ("reference with no attribution", "reference-chunk", changed(chunk, attribution=""), False),
    ("reference at a branch, not a commit", "reference-chunk", changed(chunk, source_commit="main"), False),
    ("reference URL not at a commit", "reference-chunk", changed(chunk, source_url="https://github.com/a/b/blob/main/c.md"), False),
    ("reference with a standards doc's ID", "reference-chunk", changed(chunk, id="ADR-001"), False),
    ("reference with a short embedding", "reference-chunk", changed(chunk, embedding=[0.1]), False),
    ("scanner finding with only a pointer", "finding", scanner_finding, True),
    (
        "LLM finding with only a pointer",
        "finding",
        changed(finding, deterministic=False, evidence=[plan]),
        False,
    ),
    (
        "LLM finding with only a doc",
        "finding",
        changed(finding, deterministic=False, evidence=[doc]),
        False,
    ),
    (
        "scanner finding with only a doc",
        "finding",
        changed(finding, evidence=[doc]),
        False,
    ),
    ("no evidence", "finding", changed(finding, evidence=[]), False),
    (
        "old index pointer",
        "finding",
        changed(
            finding, evidence=[{"plan_pointer": "/resource_changes/0/change"}, doc]
        ),
        False,
    ),
    (
        "escaped module address",
        "finding",
        changed(
            finding,
            evidence=[
                {
                    "plan_pointer": '/resource_changes/module.a.azurerm_x.y["b~1c"]/change/after/tags'
                },
                doc,
            ],
        ),
        True,
    ),
    ("example result", "review-result", result, True),
    (
        "scanner finding inside a result, proves the local $ref",
        "review-result",
        changed(result, findings=[scanner_finding]),
        True,
    ),
    (
        "bad finding inside a result",
        "review-result",
        changed(result, findings=[changed(finding, severity="urgent")]),
        False,
    ),
    (
        "findings verdict with none",
        "review-result",
        changed(result, findings=[]),
        False,
    ),
    (
        "no_findings verdict with findings",
        "review-result",
        changed(result, verdict="no_findings"),
        False,
    ),
    (
        "no_findings verdict, none",
        "review-result",
        changed(result, verdict="no_findings", findings=[]),
        True,
    ),
    (
        "not_reviewed with no reason",
        "review-result",
        changed(result, verdict="not_reviewed", findings=[]),
        False,
    ),
    (
        "not_reviewed with a reason",
        "review-result",
        changed(
            result,
            verdict="not_reviewed",
            findings=[],
            not_reviewed=[{"scope": "plan", "reason": "redaction_refused"}],
        ),
        True,
    ),
    (
        "part of a scope not reviewed, by resource",
        "review-result",
        changed(
            result,
            not_reviewed=[{"scope": "security", "reason": "plan_too_large", "resources": ["azurerm_subnet.a"]}],
        ),
        True,
    ),
    (
        "a blocking finding under a blocking gate",
        "review-result",
        changed(result, gate="blocking", findings=[changed(finding, blocking=True)]),
        True,
    ),
    (
        "a gate that isn't advisory or blocking",
        "review-result",
        changed(result, gate="strict"),
        False,
    ),
    (
        "an empty list of resources not reviewed",
        "review-result",
        changed(result, not_reviewed=[{"scope": "security", "reason": "plan_too_large", "resources": []}]),
        False,
    ),
]

failed = 0
with tempfile.TemporaryDirectory() as tmp:
    for name, schema, document, expected in cases:
        path = Path(tmp) / "doc.json"
        path.write_text(json.dumps(document))
        run = subprocess.run(
            [
                "check-jsonschema",
                "--schemafile",
                str(root / f"{schema}.schema.json"),
                str(path),
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        passed = run.returncode == 0
        ok = passed == expected
        failed += not ok
        print(
            f"{'ok  ' if ok else 'WRONG'} {'accepted' if passed else 'rejected'}: {name}"
        )
        if not ok:
            print((run.stdout + run.stderr)[:600])

sys.exit(1 if failed else 0)
