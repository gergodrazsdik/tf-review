# Review round, 2026-11-06: consolidated findings

Four Claude Code agents reviewed all four repos, each with a different posture:

| Reviewer | Posture | Findings | IDs |
|---|---|---|---|
| Security | An attacker with a PR, a write token or nothing at all | 18 | SEC-n |
| Correctness | A bug hunter in the service, the scripts and the Rego | 26 | BUG-n |
| Operations | The on-call engineer who inherits it tomorrow | 21 | OPS-n |
| Maintainability | A new owner who doubts the tests, evals and docs | 28 | MNT-n |

That's 93 raw findings, which merge into about 70 distinct issues once duplicates are folded together. The reviewers' full reports stayed out of the repo; the IDs name their findings, and a commit that fixes one cites its ID. A ✓ means the issue was re-checked in the code after the review.

All 692 tests pass and `just lint` is clean. That was checked by the maintainability reviewer.

## Tier 1: fix before anything else

| # | Issue | Source IDs | Why it matters |
|---|---|---|---|
| 1 | **PR code can rewrite production state.** ✓ The plan identity has Blob Data Contributor on its state container (`bootstrap/repos.tf:169`), Crypto User on its state key (`bootstrap/state_keys.tf:47`), and a federated subject `:pull_request` that matches any workflow a PR adds. A forged state entry is then destroyed by the next auto-approved apply. | SEC-1, SEC-13, OPS-14 | Anyone with write access to an onboarded repo can delete production resources, and the apply is approved without the approver seeing a plan. |
| 2 | **Failures after the 202 are silent.** ✓ `WorkerHost.cs:36-41` reads the Key Vault secrets before taking a message off the queue, with no error handling. A secret, Key Vault or image fault crashes the job each time, and the message is retried for 7 days. No "not reviewed" is posted (against D39), and the workflow never polls for the result. | OPS-1, MNT-12 | The PR keeps the previous commit's comment, and nobody is told. |
| 3 | **No alerting at all, and the dashboard can't show health.** No metric or log alerts exist. A review that fails entirely emits no metric, the API has no telemetry, the job-failure tile has never matched real data, and there's no tile for queue depth. | OPS-2, OPS-6, OPS-18 | Together with #2, outages are invisible. |
| 4 | **No hard stop on spending.** The budget emails on actual cost only, and the 60-per-hour limit lives in memory, so it resets when the API scales to zero. Orchestrator, arbiter and per-finding suggester calls are uncapped. `/healthz` can keep the API awake. Live, €16.68 of the €30 budget is spent, almost all of it by **evals on the production deployment**: 9.76M prompt tokens on 11-04, against $0.05 for the service itself. | OPS-3, SEC-5, SEC-7, OPS-4, OPS-11 | The evals are the real cost driver, and they share production's 100K TPM deployment with no concurrency group. |
| 5 | **The eval numbers are weaker than `baseline.md` says.** C20, C25 and C27–C30 still say `held_out: true` after being tuned on. Concept regexes match generic words ("remove" matches `move`, "standard" matches `Standard`), and C27's concepts match the replacement-forcing attribute. The "mini range 88–92" can't be traced. | MNT-1, MNT-2, MNT-10 | The scale of sol's lead (D4) and the D50 conclusion rest on inflated scores. |

## Tier 2: real bugs, found by more than one reviewer

| # | Issue | Source IDs |
|---|---|---|
| 6 | The plugin guard is easy to get around: `/usr/bin/tofu apply`, `"tofu" apply`, `\tofu`, a line break before `apply`, `$T apply` and `xargs` all pass. `tofu test` and `init -migrate-state` aren't blocked, and input it can't parse is allowed. | SEC-4, MNT-7, BUG-3 |
| 7 | A give-up for an old commit posts "not reviewed" without a head check, and overwrites the newer commit's comment and ticks. | BUG-1, MNT-3 |
| 8 | Dismissal ticks are lost on any "not reviewed", "no findings" or disappearing-finding rewrite, against D56. A re-tick or retry counts the metric twice, and the metric is recorded before the comment write succeeds. | BUG-2, MNT-4, BUG-low |
| 9 | Per-review pacing is hard-coded to 90K, with 3 concurrent jobs against a 100K deployment, so demand can reach 270K. The mini fallback still paces at 90K against its 50K, and isn't actually wired up. | MNT-11, OPS-5, BUG-low |
| 10 | `ReferenceSuggester.Accept` never checks that the added sentence contains the validated term, against D50; a punctuation-only term passes. | BUG-4, MNT-6 |
| 11 | One agent's worst case (4 batches with retries) can run past the 14-minute deadline. The whole review is then retried, and ends "not reviewed" instead of D54's partial result. | BUG-5, OPS-13 |
| 12 | A Checkov failure makes `DistinctBy(scope)` drop the security agent's own not-reviewed entry and its resource list. | MNT-13, BUG-low |
| 13 | No cap on the comment's size, so a large review can exceed GitHub's 65,536-character limit and end "not reviewed". | SEC-11, BUG-low |
| 14 | `thresholds.comment_min_severity` is in the contract and accepted, but never used or validated. | MNT-5, BUG-low |

## Tier 3: security hardening (medium, single source)

| # | Issue | Source |
|---|---|---|
| 15 | The image signature check accepts `release.yml` at any `image-v*` tag, and there's no tag ruleset. Anyone with write access can produce a signed image that skips the Grype gate. | SEC-3 |
| 16 | The eval identity can read the whole `tfreview` database, and `e2e-live` runs PR-built code as that identity, so cached answers from every repo are readable. | SEC-2 |
| 17 | The public API and the worker share one identity, which can read the GitHub App key and write every Cosmos container. | SEC-6 |
| 18 | `config-path`, `api-url` and `working-directory` are PR-controlled, so the API token can be sent to any host. A PR can also force a neutral "not reviewed" to get past blocking mode. | SEC-9, SEC-10 |
| 19 | Smaller ones: the plugin is pinned by a git tag that can be moved; the skipped required check passes; dismissal markers can be forged by writers; the App Insights string is a plain env var; no purge protection on the service vault. | SEC-8, SEC-14, SEC-15, SEC-16, SEC-17/OPS-16 |

## Tier 4: operations and release (medium)

| # | Issue | Source |
|---|---|---|
| 20 | GHCR and Sigstore are on every critical path. A GHCR outage blocks merges in the demo repos, because the required check pulls and verifies the image. | OPS-7 |
| 21 | No post-deploy smoke test or canary. The plan's risk table claims a revision smoke test that doesn't exist, and rollback means a full PR plus approval. | OPS-8 |
| 22 | The `api-url` default is the environment's generated FQDN, baked into released workflow commits. | OPS-9 |
| 23 | No runbooks, and one person holds every role. | OPS-10 |
| 24 | Infracost's free tier will run out at about 1,600 scans a month against roughly 1,000; scans aren't cached. | OPS-11 |
| 25 | Scale ceilings at 5–50 repos. | OPS-12 |
| 26 | The result-cache key has the deployment name but not the model version, so an in-place version bump serves stale answers for up to 7 days. | BUG-low |

## Tier 5: test and eval coverage gaps (medium)

| # | Issue | Source |
|---|---|---|
| 27 | `llm-smoke.cs` still tests the M1 schema, yet new models are admitted on it. | MNT-8 |
| 28 | The e2e-live path filter skips Rag, Scanners, Service, `Directory.Packages.props` and `global.json`, so the .NET 11 GA re-pin won't run the live evals. | MNT-9 |
| 29 | The replay gate passes with orchestrator or arbiter answers missing, and never runs the reference suggester, which is the production default. | MNT-14 |
| 30 | No test validates real serialized results against the `review-result` and `finding` schemas. | MNT-15 |

## Tier 6: standards gaps (tf-standards)

- POL-003 misses Owner granted through `role_definition_id`, made worse because redaction replaces the well-known role GUIDs.
- POL-004 misses common data-holding resource types.
- POL-002 flags UDP 22.

## Low and info

There are about 35 more, each in its report: stale docs and comments, the legacy `tfreview review` path, Python comment parsers mis-reading blocking rows, `feedback_cases.py` and its pipe-in-suggestion bug, data-source reads counted as changes, the cache counted as model calls in `usage`, the arbiter changing dismissal keys, no retry on the standards download, the state version rule counting from when a version was written, no checks against empty allowlists, review results not kept, review statuses never expiring, and date-driven maintenance with no reminders.

## What held up (agreed across reviewers)

- The API's token checks (D45) and repo partitioning of statuses and the cache.
- The worker's head checks before and after a review (only the give-up path lacks one).
- Comment rendering has no Markdown, HTML or mention injection, and the prompts are fenced.
- `TokenPacer` arithmetic, `ReviewAgent.Batch`, `StructuredCall` retries, the D55 gate, the arbiter limits, the evidence check, and the cost math.
- Per-repo state keys with purge protection, versioning and locks.
- Action and tool pinning, the corpus never serving a half-built index, and identical `install-ci-tools.sh` and schema copies across the repos.
