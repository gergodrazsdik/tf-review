# Eval baseline

What later prompts, models and retrieval changes have to beat. Each number is from `e2e-live`, which runs every case in `tests/fixtures/cases` five times against the real model. A case passes only if the whole set of findings matches, so an extra finding fails it like a missed one.

## Current: 2026-11-02

Model gpt-5.4-mini at low reasoning effort. Prompts conventions v4, security v4, cost v4, orchestrator v1, arbiter v2. `tf-standards` at `adf50af`. Five runs per case, because three misled twice while this was being built.

| Measure | Result |
| --- | --- |
| Runs passing | 337 of 340, or 99%, over four samples of this code: 83, 85, 85 and 84 of 85 |
| Failures | C4 and C15 once each: the cost agent marked a real increase as not breaking ADR-003. C13 once: the security agent reported private SSH, assuming an internet-facing source that wasn't there |
| Input tokens cached | About a quarter, because the same prompts repeat within minutes. Real reviews rarely do |
| Estimated cost of a full run (85 reviews) | About $0.21, so about $0.0025 a review |

**Read this as "about 99% on small, known cases", not as solved.** At 5 runs per case, `e2e-live` will still fail now and then with no change to blame, so it isn't a required check, and a single failure needs reading rather than reverting. The known weak spots are the cost agent on ADR-003's rules and the security agent on C13-style near misses. Nothing about them can block a merge, because model findings never do.

## Through the real service: 2026-11-03

`just service-eval` runs the seeded demo PRs through the real workflow, API, job, scanners, model and App, with nothing replayed. All 8 passed: C1, C2, C3, C5, C6, C8, C9 and C10. It's one run per case, so it proves the path works, and `e2e-live`'s five runs per case remain the measure of the agents. Eight pushes at once all got reviewed, one at a time as the job picked them up, after C8 was re-run because its plan lost a race on the state lock.

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

So 5 of the first 6 passed on their first measurement, which is the fair estimate of how the agents did on new cases, from a small sample. Later samples showed those 3-of-3 results were partly luck, as the next table shows.

**C13:** the model judged correctly, and then listed the resource anyway, with a title saying the private range "does not violate" the rule. A prompt telling it not to didn't hold: 3 of 3, then 1 of 3 on the next sample. What held was a required `breaks_standard` field, last in the schema, with code removing anything marked false. As the first field it failed too, because the model answered before reasoning and wrote a contradicting title. C13 was tuned against, so it's a regression case now, not held out. C17 was written afterwards to test whether the fix generalises.

**C11:** passed at first, then on a later sample the arbiter dropped C9's correct model-only finding. It can't drop findings any more (D36).

## Earlier results

| Date | Change | Result |
| --- | --- | --- |
| 2026-11-01 | Conventions prompt v1, no reasoning | C8 passed 1 of 3, judging an update's unchanged name and reading examples as limits |
| 2026-11-01 | Conventions prompt v2, no reasoning | C8 fixed, C5 passed 0 of 3, reporting a valid name |
| 2026-11-01 | Low reasoning effort | All of C5, C6, C8, C9 and C10 passed 3 of 3 |
| 2026-11-02 | Held-out cases, security prompt v1 | C13 passed 0 of 3, everything else 3 of 3 |
| 2026-11-02 | Security prompt v2 | All 17 passed 3 of 3, and then C13 passed 1 of 3 on the next sample |
| 2026-11-02 | `breaks_standard`, first in the schema | C13 passed 1 of 3, C11 2 of 3 from the arbiter's drop, everything else 3 of 3 |
| 2026-11-02 | Field last, arbiter can't drop, 5 runs per case | 84 of 85: C14 failed once, flagging a 20% rise |
| 2026-11-02 | Code supplies the percentage (cost v4) | 83 of 85: C4 and C15 failed once each, retracting real increases |
| 2026-11-03 | v5 prompts, saying how to treat reference material, run without any | 85 of 85 |

## What this doesn't measure

- **Real plans at scale.** Every case is one small change, and most plans here are hand-built or come from a tiny demo.
- **The arbiter's merging across many findings.** It has only seen C2 and C11.
- **Infracost's prices.** C14 and C15 use hand-written prices to test ADR-003's arithmetic, and all scanner output is replayed from recordings.
- **The rendered comment,** beyond the unit tests on it and reading real ones.
