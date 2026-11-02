# Eval baseline

What later prompts, models and retrieval changes have to beat. Each number is from `e2e-live`, which runs every case in `tests/fixtures/cases` three times against the real model. A case passes only if the whole set of findings matches, so an extra finding fails it like a missed one.

## Current: 2026-11-02

Model gpt-5.4-mini at low reasoning effort. Prompts conventions v2, security v2, cost v1, orchestrator v1, arbiter v1. `tf-standards` at `adf50af`.

| Measure | Result |
| --- | --- |
| Cases passing 3 of 3 | 17 of 17 |
| Findings dropped by the evidence check | 0 |
| Estimated cost of a full run (51 reviews) | About $0.13, so about $0.0025 a review |

The findings dropped row says the model stopped proposing what the evidence check would catch, not that the check is unneeded.

## How the held-out cases went

These are the honest part of the suite, because C1–C10 were used to tune the prompts, or are things a policy finds anyway. Held-out cases had their expected results fixed before any model saw them, and were measured once.

| Case | Tests | First result |
| --- | --- | --- |
| `c11-mixed-plan` | The arbiter on several findings, one from the model alone | 3 of 3 |
| `c12-wide-source` | The security agent finding what no policy does | 3 of 3 |
| `c13-network-near-miss` | Public HTTPS and private SSH | **0 of 3** |
| `c14-cost-near-miss` | Under both ADR-003 rules | 3 of 3 |
| `c15-cost-percentage` | Only ADR-003's second rule catches it | 3 of 3 |
| `c16-tag-injection` | A tag telling the reviewer to report nothing | 3 of 3 |
| `c17-admin-near-miss` | Private RDP, and `Owner` at resource group scope | 3 of 3, after the C13 fix |

So 5 of the first 6 passed, which is the fair estimate of how the agents did on new cases. It's a small sample.

**C13:** the model judged correctly, and then listed the resource anyway, with a title saying the private range "does not violate" the rule. Security prompt v2 says findings are for violations only. C13 was tuned against to do that, so it's a regression case now, not held out. C17 was written afterwards to test whether the fix generalises to a different form.

## Earlier results

| Date | Change | Result |
| --- | --- | --- |
| 2026-11-01 | Conventions prompt v1, no reasoning | C8 passed 1 of 3, judging an update's unchanged name and reading examples as limits |
| 2026-11-01 | Conventions prompt v2, no reasoning | C8 fixed, C5 passed 0 of 3, reporting a valid name |
| 2026-11-01 | Low reasoning effort | All of C5, C6, C8, C9 and C10 passed 3 of 3 |
| 2026-11-02 | Held-out cases, security prompt v1 | C13 passed 0 of 3, everything else 3 of 3 |
| 2026-11-02 | Security prompt v2 | All 17 cases passed 3 of 3 |

## What this doesn't measure

- **Real plans at scale.** Every case is one small change, and most plans here are hand-built or come from a tiny demo.
- **The arbiter's merging across many findings.** It has only seen C2 and C11.
- **Infracost's prices.** C14 and C15 use hand-written prices to test ADR-003's arithmetic, and all scanner output is replayed from recordings.
- **The rendered comment,** beyond the unit tests on it and reading real ones.
