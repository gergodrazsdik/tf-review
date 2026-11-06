#!/usr/bin/env python3
"""Tests the comment parsers on what the renderer really writes. Part of `just lint`; stdlib only.

tests/fixtures/comments/review.md is CommentRenderer's output, kept equal by CommentFixtureTests, with a blocking
row, a folded rule, a suggestion holding a pipe, a title holding a backtick, a ticked box and a partial not-reviewed line.
"""

import unittest
from pathlib import Path

import comment_table
import service_eval

FIXTURE = Path(__file__).resolve().parent.parent / "tests" / "fixtures" / "comments" / "review.md"


class CommentTableTests(unittest.TestCase):
    def setUp(self):
        self.body = FIXTURE.read_text(encoding="utf-8")

    def test_reads_every_row(self):
        rows = comment_table.rows(self.body)
        self.assertEqual(
            [(row["severity"], row["address"], row["rule"]) for row in rows],
            [
                ("high", "azurerm_network_security_rule.ssh", "POL-002"),
                ("medium", "azurerm_storage_account.logs", "CKV_AZURE_59"),
                ("low", "azurerm_storage_account.logs", "ADR-001"),
            ],
        )

    def test_reads_a_blocking_row(self):
        row = comment_table.rows(self.body)[0]
        self.assertTrue(row["blocking"])
        self.assertEqual(row["also"], ["CKV_AZURE_10"])
        self.assertEqual(row["source"], "policy and model")

    def test_undoes_the_renderers_escaping(self):
        rows = comment_table.rows(self.body)
        self.assertEqual(rows[0]["suggestion"], "Allow SSH only from the bastion subnet | or a VPN range.")
        self.assertIsNone(rows[1]["suggestion"])
        self.assertEqual(rows[2]["title"], "Name `stlogs` has no environment")

    def test_reads_ticked_boxes_only(self):
        self.assertEqual(
            comment_table.dismissed(self.body),
            [{"address": "azurerm_storage_account.logs", "rule": "ADR-001"}],
        )

    def test_reads_a_partial_not_reviewed_line(self):
        self.assertEqual(
            comment_table.not_reviewed(self.body),
            ["Not reviewed by cost (the plan is too large): `azurerm_linux_virtual_machine.big`."],
        )

    def test_ignores_lines_that_arent_rows(self):
        self.assertEqual(comment_table.rows("| Severity | Resource | Rule | Finding | Suggestion | Source |\n| a | b |"), [])

    def test_service_eval_counts_blocking_and_folded_rules(self):
        self.assertEqual(
            service_eval.parse_findings(self.body),
            {
                ("azurerm_network_security_rule.ssh", "POL-002"),
                ("azurerm_network_security_rule.ssh", "CKV_AZURE_10"),
                ("azurerm_storage_account.logs", "CKV_AZURE_59"),
                ("azurerm_storage_account.logs", "ADR-001"),
            },
        )

    def test_either_bot_may_own_the_comment(self):
        self.assertIn("github-actions[bot]", comment_table.BOT_LOGINS)
        self.assertIn("tf-review-gergodrazsdik[bot]", comment_table.BOT_LOGINS)


if __name__ == "__main__":
    unittest.main()
