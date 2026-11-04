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

cases = [
    # name, schema, document, should it pass
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
