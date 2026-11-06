# tf-review: project lessons

A retrospective of building tf-review, a multi-agent, RAG-backed reviewer for OpenTofu pull requests, from the first commit on 2026-11-01 to v1 on 2026-11-06. One developer built it with Claude Code agents across four repositories under `~/projects/tf-review-board/`: `tf-review` (the reviewer, its infrastructure and its contracts), `tf-review-demo` (the template repo, whose seeded PRs double as eval cases), `tf-review-demo2` (the second onboarded repo) and `tf-standards` (ADRs and Rego policies).

**Sources.** `tf-review/docs/plan.md` (decisions D1–D59, milestones and tasks), `docs/evals/baseline.md` and `docs/evals/retrieval.md`, the git history and PRs of all four repos, the agent memory notes (`m2-pause-state.md`, `m3-state.md`, `hand-over-destructive-git.md`, `commit-with-message-file.md`) and the session transcripts. A bare PR number such as #106 is `tf-review`'s; the others are written demo #44, demo2 #2 and tf-standards #4. Dates are the project's own; transcript references give the day.

---

## 1. Executive summary

In six days the project went from empty repositories to a running service. A reusable workflow plans each PR with the repo's own identity and submits a redacted plan to an API on Azure Container Apps. A queue-driven job runs Checkov, conftest and Infracost, then the agents, and posts a comment and check run through a GitHub App. All five milestone gates passed on real PRs, a second repo was onboarded with one map entry, and a small review costs well under a cent.

The five lessons that matter most for the next project:

1. **Let code decide and the model advise.** Whenever a model was trusted with a decision code could make, such as dropping a finding, applying a cost threshold, re-reporting a policy finding or routing resources, it eventually failed in the evals, and prompt wording never held for long. Structure held: code sets rule, severity and blocking, the model answers its judgement field last, the arbiter can't drop, and policy findings are answered by ID (D35, D36, D51, D55).
2. **Prove the unknowns first, and call a milestone done only on a real PR.** Spikes against the real platform opened every milestone, and 22 draft demo PRs proved the releases. That kept a 189-PR plan in step with reality.
3. **Evals keep a project honest, and fail in their own ways.** They gave honest negatives: references didn't improve suggestions, gpt-6-luna lost to gpt-5.4-mini, and 3-of-3 gates misled twice. But regex scoring under-scored gpt-6.1-sol until #111, each experiment used up its held-out cases, and the configuration that ships was never measured as a whole.
4. **Assume tools and platforms fail silently.** conftest, Checkov, Infracost, GitHub's handling of conflicting PRs, frozen workflow variables and OpenTofu's key provider all did nothing without saying so. Canaries and a "not reviewed" comment caught the reviewer's own cases by design; the platform cases cost hours.
5. **The split between agent and developer worked, and the developer's attention was the limit.** The agent built, proved and documented; the developer decided, merged, applied and tagged. About 30 of 126 `tf-review` PRs were release bookkeeping, and the median PR merged nine minutes after it opened, so correctness rested on tests, evals and proofs. Automate the bookkeeping, and schedule independent review at each gate.

---

## 2. Timeline in brief

The work ran in two Claude Code sessions: a short one for M0 in the early hours of 2026-11-01, then one long session to the end, compacted five times (twice by hand) and interrupted once. Across the four repos that came to 189 PRs. `tf-review` had 126, of which 125 merged; GitHub closed the other, #80, when the PR it was stacked on merged. The demo had 54: 24 merged, 22 draft proofs closed and 8 seed PRs kept open as eval cases. demo2 had 3 and `tf-standards` 6. `tf-review` now holds about 8,600 lines of C# in ten projects, 9,000 lines of tests (519 test methods), 1,400 lines of OpenTofu, seven prompt roles (three at version 6), 31 eval cases and 59 recorded decisions, up from 18 in the first plan (`bfab978`). There were 19 image release runs, two of which stopped at the Grype gate, 12 workflow versions and one plugin release.

| Date | Gate | What was built | Turning points |
| --- | --- | --- | --- |
| 2026-11-01 | M0 (demo #2), M1 (demo #15) | Repos, contracts and bootstrap with its state migrated (#1–#4); the .NET 11 RC spike (#5); the M1 design council (D19–D29, #6); Foundry and a live model call from the container (#11); a counts-only comment before any agent existed (demo #5); redactor, condenser, conftest runner and the conventions agent (#17–#25); the eval harness (#27) | OIDC subjects needed numeric IDs (`d577cc4`); Azure IDs in public logs became secrets (D30, #9); conftest had to be built from source to pass Grype (#15); low reasoning effort fixed C5 (#27); quota for stronger models was declined (#31) |
| 2026-11-02 | M2 | The cost spike (D31–D34, #32); Infracost, Checkov and the security and cost agents (#33–#38); the orchestrator and arbiter (#40); held-out cases (#41); Terraform input (#44); M3 planned (#45, D37–D45) | C13 led to `breaks_standard`, and the arbiter lost the right to drop after it dropped C9's correct finding (#41); runs per case went from 3 to 5; the developer chose the full M3 scope and replaced the proposed `tf-references` repo with storage in Cosmos only (D37, D43) |
| 2026-11-02 evening to 11-03 | M3 parts 1–5 | Infrastructure, identity, API, worker, `submit` and deployment in about four hours (#47–#59); the first review through the service in 42 seconds (demo #32); the corpus imported and indexed overnight, 7,627 chunks (#70–#76); retrieval wired in (#78–#81) | azurerm reads secrets, so azapi, twice (D46, #49, #58); `azurerm_client_config` leaked IDs in base64 (#50); a newline in the Infracost token (#60); eight seed pushes raced on the state lock (D48, #67) |
| 2026-11-03 to 11-04 | M3 (demo #38, #39) | Retrieval evals and re-ranking (#85, #86, D49); references on in the demo (demo #37); suggestion scoring on held-out cases (#92–#94) | References didn't improve suggestions; per-finding references (D50) and policy findings answered by ID (D51, #95); cost v6 (#96) |
| 2026-11-04 to 11-05 | | Self-review on `tf-review`'s own infra (D52, #98–#100); the result cache (D53, #101); batches for large plans (D54, #104); the model comparison (#108–#110) | Batching outran the 30K TPM deployment on demo #44, so 100K TPM and a pacer (#106); gpt-6.1-sol adopted for every role (#110) |
| 2026-11-06 | M4 (demo2 #1) | C20, C25 and C27 rescored (#111); opt-in blocking (D55, #112); dismiss checkboxes (D56, #115); the Claude Code plugin (D57, #118); demo2 onboarded (#119); after v1, the dashboard (D58, #121) and state encryption (D59, #122–#126) | New Grype findings, one on CISA's known-exploited list, stopped image-v0.0.17 (#113); the infra apply read the old allowlist (#119); the key provider ignored `ARM_USE_OIDC` (demo2 #2); plaintext state versions refused deletion (#126) |

Still open at the time of writing (plan.md, Accounts and Backlog): reviewing the Infracost parser plugin's Grype exceptions by 2026-12-06, re-pinning to .NET 11 GA after 2026-11-10, returning conftest and the Infracost CLI to release binaries once upstream fixes their findings, checking that the lifecycle rule removed the plaintext state versions, and self-hosted Infracost pricing before any client repo.

---

## 3. What worked well

### 3.1 Architecture: code decides, the model advises

The plan's summary set the rule on day one: "Deterministic tools find issues; LLM agents explain them, rank them and tie them to the team's own standards." The project kept to it, and tightened it each time an eval showed a model with too much say.

- **The model fills a flat schema, and code writes the finding.** Code maps the wire answer to the finding contract and sets `rule`, `severity`, `agent`, `deterministic` and `evidence`, so "a model must never be able to claim `deterministic: true`" (plan, Agents and prompts). The council's skeptic proposed this before any code existed (subagent report, 2026-11-01).
- **Evidence is checked by code before the arbiter sees it.** A finding needs a plan pointer that resolves and matches its address and, if no scanner backs it, an accepted doc that was retrieved for this prompt, applies to the resource type and points at an attribute the plan changes. Failures are dropped and counted by reason (#23; the unchanged-attribute rule came from C8 in #27).
- **The model's roles shrank to what is safe to get wrong.** The first plan had an LLM routing resources and an LLM arbiter with full say (`bfab978`). D35 made code route, with the model only ordering changes inside rule-based risk tiers. D20 kept the M1 arbiter rule-based, and D36 limited the M2 one: it can propose merges and an order, and code refuses anything else, including drops after it dropped C9's correct finding (#41).
- **Blocking is exact.** D9 said no merge is ever blocked by an LLM opinion alone, and D55 made that precise: only a finding a policy or allowlisted Checkov check reported as high itself, judged before any merge, can block, and "not reviewed" is neutral in every mode.
- **Reference material can't create or change findings.** D42 kept imported docs as background, and D50's per-finding placement can't touch a finding at all. When the model ignored the prompt's ban on Azure Policy advice, code started dropping those sentences (D50).

This worked because it turned every model failure into a bounded one. A wrong answer could cost a suggestion or a model-only finding, but never a deterministic finding and never a block. That let the project run on gpt-5.4-mini for most of its life, ship several releases a day and still adopt a new model in one evening (#108–#110).

Four smaller choices paid off the same way:

- **Honest failure.** Every failure posts "not reviewed" for that commit instead of leaving an older result standing (plan, Rules every agent run follows). The stub did so before the agent existed (demo #5), the worker checks the head commit before and after a review (#53), and the outage drill posted "Not reviewed" after five minutes of retries (demo #39).
- **Address-keyed evidence pointers (D22).** They were chosen in M1 for a cache that M4 would build. The result cache (D53), the batches for large plans (D54) and the dismiss marker (D56) all used the same keys with no migration.
- **One image everywhere (D3).** The move to the service "changes hosting, not logic" (plan, Review service internals): the worker runs the same `ReviewRunner` the runner did (#53).
- **The cache key is the whole input (D53).** Hashing everything a model call is given "needs no list of what counts, so nothing stale can come back". On demo #43 a re-push got all four calls from the cache, with no tokens, in 177 ms of cache reads, and the findings stopped flipping wording between pushes (session transcript, 2026-11-04).

### 3.2 Delivery: prove the unknowns, then gate on a real PR

Each milestone's work order began with "Prove the unknowns" or "Decide the unknowns" (plan, M1–M3), and the spikes changed the design before code depended on it:

- `llm-smoke` (#11) sent the flat strict schema through the OpenAI SDK, `IChatClient` and Agent Framework from a non-root, read-only container with a federated token. It also found that Agent Framework hides the finish reason, which the truncation retry needed.
- The scanner spike (#32) found that Infracost and Checkov exit 0 with an empty result on a redacted plan, which became D31 and D32 before M2's code.
- The OIDC claims spike (demo #30) showed with real tokens which claims tell the review job apart from the plan job and from a job a PR adds, and that a fork's commit resolves through its parent. That produced D45's list of released commits before the API was written.
- The App token spike (demo #31) and a queue-scaled job (demo #32) proved the service's moving parts. Re-pricing the corpus before indexing estimated about 11,000 chunks and 10 cents. The real index came to 7,627 chunks and 2.24M embedding tokens, about 4.5 cents (plan, M3 parts 1 and 4; transcript, 2026-11-03).

M1 ran as a walking skeleton: a real PR comment, saying honestly that nothing was reviewed, landed before any agent existed (demo #5), and real policy findings before the agent (#21). The plan's rule was "A milestone is done when its gate passes on a real PR, not when its tasks are ticked." Every release candidate and feature was then proved on a draft demo PR that was closed afterwards: 22 of them, from demo #6 to demo #50. Those proofs found the agent's own invalid `TLS1_0` (demo #32), the token newline (#60), the arbiter merging differently from the evals and a weak "Look at first" (demo #26), and the rate-limit overrun (demo #44).

This worked because unknowns were cheap to find early and expensive to find late, and because a gate on a real PR put the product, not the task list, in charge of "done".

### 3.3 Evals as a gate

- **Three tiers, each with a job.** `just e2e` replays recorded answers offline: it is free, runs in CI's `test` job and is the Stop hook (#30). `just e2e-live` runs every case five times against the real model on PRs that change the agents, prompts, fixtures or `TfReview.Core`; CI ran it 58 times. `just service-eval` checks what the deployed service actually posted on the demo's eight seed PRs (#66).
- **Whole-set matching.** "A case passes only if the whole set of findings matches, so an extra finding fails it like a missed one" (baseline.md). False positives count.
- **Each tier caught something the others couldn't.** The replay kept recorded answers in step with prompt versions: the Stop hook blocked 14 turns, nearly all work in progress such as a prompt bumped before its answers were re-recorded (transcript, 2026-11-01 to 11-04). The live tier found model behaviour (C13, the arbiter's drop, the cost retractions). The service tier found the state lock race (D48) and a bug in its own script (transcript, 2026-11-03).
- **Held-out cases, fixed before any model saw them.** C11–C17 (#41), C18–C21 (#92), C22–C26 (#93) and C27–C31 (#94) were the honest part of the suite, because C1–C10 had been used for tuning (baseline.md, How the held-out cases went).
- **A baseline that records failures and limits.** The first version's "17 of 17" was corrected when later samples didn't repeat it (#41), and the document lists what it doesn't measure.
- **Models chosen through the suite.** D4 said "Luna must beat mini on the evals, not just on price". It didn't: 154 of 155 runs and 81 of 210 concepts. gpt-6.1-sol passed 465 of 465 runs over three samples, at about 1.1 to 1.5 times mini's cost, and was adopted for every role (#109, #110).
- **Reading the answers, not just the score.** `--show-answers` (#96) found the ambiguous question behind the cost retractions. Reading answers also found `"reason":"null"` as a string (#40), C13's title saying the resource "does not violate" the rule (#41), and the off-point sentences behind v6's better score (#93).

This worked because the cheap tier ran on every turn, so the expensive tiers could be kept for questions worth paying for, and because writing the baseline as a document, failures included, gave each later change something honest to beat.

### 3.4 IaC and bootstrap

- **One bootstrap, run once by hand, and CI for everything after.** Bootstrap ran from the developer's laptop with local state and then migrated it into the storage account (D12, #3). Everything else deploys through CI with OIDC, and every apply waits for the `prod` approval.
- **No stored Azure secrets.** Federated credentials use GitHub's immutable subjects (`d577cc4`), Azure services use managed identity, key auth is off on Foundry and Cosmos, and the Infracost token and App key live in Key Vault (D18, D38).
- **Least privilege held.** Plan identities are Readers plus their own state container, a job that runs PR code never holds LLM credentials (D21), scanners get only the environment they need (D47), and App tokens are scoped to one repo for one review (#53).
- **Onboarding is data.** A repo joins through one entry in bootstrap's `repos` map (D40), which demo2 proved (#119, demo2 #1), with the caveats in section 4.4. What demo2 taught went back into the template: names from one locals block (demo #52), with no plan change.
- **Risky changes ran in reversible steps.** State encryption (D59) went to three repos in one evening: keys first (#122), then encryption with a fallback that reads plaintext (#124, demo #53, demo2 #2), then enforcement (#125, demo #54, demo2 #3). It was verified by reading only the blobs' top-level keys (`m3-state.md`).

This worked because OpenTofu in CI with a human approval made every infrastructure change a reviewable plan, and the map made onboarding a one-line change.

### 3.5 Supply chain and public-repo hygiene

- **A vulnerability gate from the first release, with patching before excepting (D28).** Grype stopped image-v0.0.1 and image-v0.0.17, 2 of 19 release runs, before anything shipped. The response was to patch: conftest built from source with fixed Go dependencies (#15), the Infracost CLI likewise (#34), and the base image re-pinned (#113). Only Infracost's closed-source parser plugin got exceptions, each scoped to one finding on one file with a review date in the plan (`.grype.yaml`).
- **Signed and pinned artifacts.** Images are signed keyless by the release workflow, callers pin them by digest, and `cosign verify` runs before any PR code (D17, D23). Actions are pinned to SHAs with `pinact`, and commits and tags are signed. Trivy was avoided after its compromise (D10).
- **Public logs designed for.** The Azure IDs are Actions secrets (D30), no raw plan reaches a comment or artifact, GUIDs are masked in redacted plans (#28), and the demo's plan comment went (demo #4).
- **Trust tested with real tokens before it was enforced.** D45's claims were read from real tokens before the API refused anything (demo #30, #46). Sealing the scanners (D47) came from a test: "A test showed Rego reading an environment variable and calling out over the network."

This worked because public repos made every leak path visible from day one, and the rule that an exception covers one finding on one file kept exceptions from becoming a habit.

### 3.6 The human and agent workflow

Section 7 covers this in detail. In short: the agent implemented, opened PRs, proved changes on draft PRs and kept the docs current. The developer answered decision questions, merged, ran bootstrap, approved `prod` and pushed signed tags. Twelve rounds of questions, 27 in all, each with a recommendation and its trade-off, settled the big choices, and each answer went into the plan with its reason. A four-member council planned M1, including an adversarial reviewer. Privileged steps came to the developer as exact one-line commands. The plan (103 revisions) and the memory notes carried the work across five compactions and one interrupted session. This worked because every irreversible or credentialed step stayed with a person, while everything else moved at the agent's speed.

### 3.7 Cost

- **Measured cost per review.** The first real review cost about $0.003 for 2,136 input and 231 output tokens (plan, M1 part 5). The evals put a review at about $0.0025 on gpt-5.4-mini without references and $0.0066 with references per agent, and a full 155-review suite at $0.36 to $0.64 (baseline.md).
- **Hosting that costs nothing when idle.** Compute scales to zero (D16; an App Service B1 plan would have cost $12.41 a month idle), Cosmos runs on the free tier, and the whole index cost about 4.5 cents to embed.
- **Re-pushes are free.** The result cache makes a re-push that changes nothing cost no tokens (D53). Prompt caching had helped in the evals, where a quarter of input was cached, but not in real reviews, which rarely repeat within minutes (plan, M2 part 6).
- **Cost measured in every eval.** Every eval run reported its estimated cost, so cost was a measured property of each change, not a forecast. The first plan estimated $5–25 a month in tokens for about 100 PRs (`bfab978`). At the measured rate, 100 small reviews cost under a dollar; large plans are bounded at about 80K input tokens per agent (D54) and haven't been priced on real repos.

One caveat holds throughout: every figure is a list-price estimate from `ModelPrices`, which leaves out the GPT-6 models' cache writes (baseline.md, Candidate models). No figure was checked against an Azure invoice.

---

## 4. What failed expectations

Some of these were caught by the project's own checks within minutes; others cost hours. Each lists its root cause and what it cost.

### 4.1 Reference material didn't improve suggestions

**Expected.** Vectors and a grown corpus would make reviews better (D5, D37). M3 imported Azure Policy, the Cloud Adoption Framework and the azurerm docs (D41–D44), built Cosmos vector and full-text search, and added a model re-ranker (D49).

**What happened.** Retrieval improved: hybrid search gave 13 of 15 agents something relevant, against 9 for filtering alone, and re-ranking lifted the labelled docs found from 27% to 34–36% (retrieval.md). Suggestions didn't follow:

- On C18–C21 they scored 32 of 60 concepts without references and 31 of 60 with them, and the concepts only the references carry scored 0 and 1 of 25 (#92).
- A trial prompt that allowed a second sentence raised C22–C26 from 31 to 37 of 75 with or without references, and with references about 8 of 19 added sentences were redundant or off the point. It wasn't shipped (#93).
- References per finding (D50) scored 27 of 75 on C27–C31, against 28 without references (#94).

**Root causes.**

- With seven short team docs, filtering already returned every relevant rule. The agent's question on M3's scope had said so in advance: "metadata filtering already returns every relevant one under the token cap, so vectors can't beat it on the current evals" (transcript, 2026-11-02).
- The prompts asked for the standard's fix in one sentence, which left references nothing to add (baseline.md).
- References chosen per agent were about the resources, not the problem a finding is about (#93).
- The search query states facts but not what they mean (retrieval.md).

**Cost.** Roughly a day and a half of the six. References raised a review's cost about 2.5 times per agent ($0.56 against $0.22 for 85 reviews) and about 1.7 times per finding ($0.63 against $0.37 for 155). In return the project got a working RAG pipeline with an honest measurement, and two structural fixes found on the way (D50, D51). The developer chose learning over the cheapest path on purpose, answering that the learning goal mattered more than the rational operation (decision answer, 2026-11-02). That is a legitimate trade, and the docs state plainly that the measure didn't move.

### 4.2 Prompts didn't hold, but structure did

**Expected.** Clear instructions would control the agents' behaviour.

**What happened.**

- **C13.** The security agent judged private SSH correctly and then listed it anyway, under a title saying it "does not violate" the rule. A prompt telling it not to held "3 of 3, then 1 of 3 on the next sample". A required `breaks_standard` field failed as the first field, because the model answered before reasoning, and held as the last field, with code removing anything marked false (#41, baseline.md).
- **The cost agent.** ADR-003's thresholds are arithmetic and have no Rego, so a model applied them. It flagged a 20% rise, which ended when code supplied the percentage (cost v4). Then it retracted real increases, because its last field asked whether a title made only of figures "says the resource breaks the standard". How often varied from one day to the next: `main` missed 4 of 15 runs in one sample and 1 of 30 in the next, with nothing changed. Cost v6 names the rule in the title and checks the figures against it (#96).
- **C28 and C29.** Re-reporting a policy finding needed an attribute to point at, and a deletion has none. C28 failed 15 of 15 runs and C29 4 of 15, until D51 answered policy findings by ID.
- **Governance advice.** "The model ignored the prompt's ban on governance advice, so code enforces it" (D50).
- **The arbiter.** It dropped C9's correct finding (#41), and it merged C2's storage rows in the evals but not on demo #26 (transcript, 2026-11-02).

**Root cause.** Asking a model to make a decision whose inputs code already had, or to answer a question that its own output couldn't settle.

**Cost.** Six versions each of the conventions, security and cost prompts, and several rounds of live evals. No wrong result reached a repo, because the gates held.

### 4.3 Concurrency and rate limits

- **Batching outran the TPM limit (D54).** Batches kept each call under the per-call limit, but on demo #44 (20 large NSGs) two agents batching at once outran the 30K TPM deployment. 429 retries took 12 of the job's 14 minutes and cut the security agent's last batch, leaving 8 of 20 resources unreviewed (transcript, 2026-11-05). The capacity had been set low as a brake on runaway spend ("which limits how fast a runaway loop can spend but isn't a budget", plan, M1 part 1), and per-call caps don't add up across parallel agents. The fix was 100K TPM and a `TokenPacer` per review at 90K (#106). On demo #45 (24 NSGs) the review ran in 8 batches in about 3 minutes, and `e2e-live` dropped from about 16 minutes to 5. It cost a release, two proof PRs and a decision round.
- **A sharp edge remains.** The pacer's 90K is a code constant (`TokenPacer.DefaultTokensPerMinute`), while the gpt-5.4-mini fallback now has 50K TPM (#110). Switching `TF_REVIEW_OPENAI_DEPLOYMENT` to the fallback would pace against the wrong limit.
- **Plans raced on the state lock (D48).** Eight seed pushes at once raced on the state blob. The azurerm backend fails at once when it sees a lease with no lock details, whatever `-lock-timeout` says, and GitHub's concurrency groups keep only one waiting run, so queueing wasn't possible. Review plans now run with `-lock=false` (#67).
- **The live evals hit the job timeout.** With references, `e2e-live` took about 36 minutes and was killed at the 20-minute limit (#89).

### 4.4 Silent no-ops

The most expensive surprises were tools and platforms that did nothing and said nothing.

| What | How it stayed silent | Found by | Fix |
| --- | --- | --- | --- |
| conftest | Without `--all-namespaces` it reads only `main`, finds nothing and exits 0; exit 1 means both "policies failed" and "conftest errored" | The council, before code (#19) | Fail when results show only `main`; only valid JSON counts as a run |
| Checkov and Infracost | Exit 0 with an empty result on a plan they don't recognise | The M2 spike (#32) | Scan a plan rebuilt from the redacted one; Infracost finding no resources is a failure; a Checkov canary that must fail (D32) |
| A Rego helper named `violation` | `conftest verify` passed; `conftest test` treated it as a policy and would have failed every review | The reviewer's replay (tf-standards #4) | Rename it; `just policy-test` now runs `conftest test` too |
| Seed PRs | GitHub runs no `pull_request` workflow on a PR that conflicts with its base, and says nothing. Every demo repin edits the line the seeds pin | 20 minutes lost on a release candidate (#84); six seeds conflicted for about a day after demo #38 (`m3-state.md`) | `service-eval` refuses conflicting seeds; merge the demo's `main` into the seeds after each repin |
| The infra apply after onboarding | A run's `vars` are fixed when it's created, so the apply started by #119's merge read the old allowlist | The M4 gate | `workflow_dispatch` and a manual run (#119) |
| OpenTofu's `azure_vault` key provider | Reads `use_oidc` only from its block, ignores `ARM_USE_OIDC`, and falls through to the Azure CLI | The first plan on demo2 #2 | `use_oidc = true` in the block |
| `just secrets-sync` | Stored the Infracost token with a newline, so every cost scan in the service failed | The first reviews through the service (demo #32, #60) | No newline, and the worker trims; the developer pushed for fixing the stored value too (transcript, 2026-11-02) |

**Root cause.** "No result" looked like "clean result". The reviewer's own core got this right early, because the council named the conftest case before any code existed. The platform cases cost time because nothing in the process asked "how would we know if this did nothing?"

### 4.5 Least privilege against the providers, and public logs

- **azurerm reads secrets on every read (D46).** It lists keys or secrets for Cosmos accounts and Container Apps, even when there are none, so the Reader plan identity got 403s. The first plan of a new resource can't show this, so it bit twice: Cosmos (#49), then the Container Apps, after which "every PR touching `infra/` now fails its plan" until #58 and #59. The agent's own note: "I should have checked for it after D46" (transcript, 2026-11-02). The fix was to move those resources to azapi with `import` and `removed` handovers, instead of letting an identity that runs PR code list secrets. D46 now requires a plan with state for each new resource type.
- **Azure IDs in public logs, twice (D30).** They were in every step's env block and in tofu's resource IDs, which the developer asked the agent to check on 2026-11-01. They became secrets (#9, #10, demo #4), and the old runs were deleted. The next day #47 added `azurerm_client_config`, whose printed ID holds the same IDs in base64, out of the masking's reach, and it reached five runs' logs before #50. `tf-review`'s CLAUDE.md now forbids that data source.
- **A weaker runtime.** Container Apps has no read-only root filesystem option, so the service's job runs only as non-root, unlike the runner image (plan, M3 part 1).

### 4.6 State encryption left plaintext history behind

After D59 rewrote each state encrypted, blob versioning still held the plaintext versions: 217 for `tf-review`, 104 for the demo and 7 for demo2 (`m3-state.md`). The permission check blocked the agent's mass delete. The developer's run of the script failed with `OperationNotAllowedOnAutomaticSnapshot`, cause unknown. A lifecycle rule now deletes previous versions a day after they're superseded (#126). Soft delete keeps a deleted version restorable for 30 days, so the plaintext history stays recoverable for about a month after the rule runs. The check after 24 to 48 hours is still open, and the script it needs lives in `/tmp` and may not survive a reboot (`m3-state.md`). An in-place encryption of versioned storage isn't finished until its history is gone.

### 4.7 Release bookkeeping and seed upkeep

D23 (two tags) and D45 (a list of released commits) are sound controls, but a workflow release took many steps (plan, Releasing):

1. Merge the change and tag the image.
2. Pin the digest in a PR, and apply.
3. Add the commit to the released list in another PR, and apply.
4. Tag a release candidate and test it on a draft demo PR.
5. Tag the release and repin the demo.
6. Merge the demo's `main` into eight seed branches.
7. Retire the old commit.

In `tf-review` that came to 15 "Pin the service" or "Pin the review workflow" PRs, 7 PRs editing the released list and 8 that only record results: about 30 of 126. The demo had 11 "Pin the reviewer" PRs.

Three other costs came with it. Every repin made the seeds conflict, which led to the silent skipping in section 4.4. With applies serialised, a waiting approval held up the next apply until the run was cancelled (#81, #82; transcript, 2026-11-03). And `dev-image.yml`, part of D23, never ran: release candidate tags did its job (no runs on GitHub). The cost was the developer's attention, which is where many of their short "merged" and "tag pushed" messages went.

### 4.8 Agent operations: sandbox, signing, permissions and the agent's own mistakes

- **The sandbox.** A command runs outside the sandbox only if the whole call matches an exclusion. A heredoc `git commit` failed GPG signing with "No secret key" (`commit-with-message-file.md`). A pipe, a `2>/dev/null`, a `| tail`, a chained `git switch && git pull` or a `sleep` before `gh` kept other calls sandboxed without warning (transcripts, 2026-11-01 to 11-04). `dotnet build` of a solution hangs without `-m:1`, `dotnet test` and workspace `dotnet format` must run outside, and `tofu validate` and `tflint` can't start their plugins, hence a separate `just validate`. Working this out took five probe sessions and three hand-offs to the home-directory session in M0 and M1. Asked whether to drop the sandbox, the agent recommended keeping it and paying for `-m:1` (transcript, 2026-11-01).
- **The permission check.** It denied force pushes, remote branch deletes, `gh run delete` and `gh pr edit` (2026-11-02) and a mass delete of blob versions (2026-11-06), so those went to the developer as exact commands (`hand-over-destructive-git.md`).
- **The agent's own mistakes, which it reported itself:**
  - `tofu console | head -0` left the bootstrap state locked, and the developer had to force-unlock it.
  - Stacking #80 on another PR closed it when that PR merged.
  - It pushed `TLS1_0` without running `tofu validate` (demo #32).
  - It remembered a preview Cosmos capability that broke an apply (#75).
  - It added `azurerm_client_config` (#50).
  - It repeated the D46 problem on the Container Apps (#58).
  - A discarded experiment's edits resurfaced on the next branch (`hand-over-destructive-git.md`).
  - Its `service-eval` script read only the first rule of a merged row.
  - It pushed a release candidate tag without asking (transcript, 2026-11-01).
  - Its first baseline claimed 17 of 17 (#41).

Each mistake became a rule, either in memory (no lock-taking tofu through a closing pipe, no stacked PRs, `git status` before discarding) or in the project (D46's new-resource rule, the ban on `azurerm_client_config`). Each cost minutes to an hour, and two needed the developer to run a forced action.

### 4.9 Parts that haven't earned their place yet

- **"Look at first".** On its first real review "the reasons are generic ... It also left out the public storage account, because the orchestrator never sees values" (transcript, 2026-11-02, demo #26). Whether it earns its model call is tracked only in `m2-pause-state.md`, not in plan.md.
- **The arbiter's merging.** It has been exercised only on C2 and C11 (baseline.md, What this doesn't measure).
- **Unused inputs.** The Terraform `tool` input (D1, #44) was proved on one closed PR (demo #28) and no repo uses it. `dev-image.yml` never ran.
- **What the model finds on its own.** Of the 39 findings the 31 cases expect, 8 come from the model alone:
  - four ADR-003 cost thresholds, which are arithmetic;
  - three `environment` values that ADR-002 forbids but its Rego doesn't check (C9, C11, C16);
  - one wide public range that POL-002's Rego doesn't match (C12).

  Two of those gaps were kept on purpose, so the model had something to find (D19; tf-standards #6: "That gives it something to find that no policy does"). So the suite shows that the model doesn't spoil deterministic findings and writes useful suggestions. It hasn't shown the model finding much that a team wouldn't write in Rego.

### 4.10 Documentation drift

- **plan.md.** The decision log still opens with "All 49 decisions are settled as of 2026-11-02", and there are 59. The agents table still lists gpt-5.4-mini and "Stronger model once granted", though D4 moved every role to gpt-6.1-sol on 2026-11-05, and still says the arbiter proposes drops, which D36 removed. The eval table's C2 still cites a placeholder, `POL-012`.
- **baseline.md.** Its "Current" section still describes gpt-5.4-mini with v4 prompts on 2026-11-02, which is not what runs.
- **Config comments.** The `.tf-review.yml` comment in `tf-review`, the demo and demo2 says references are "chosen per agent", while the schema and D50 say per finding. The template carried the stale comment into demo2.
- **Dates.** Some sections are dated a day after the work: baseline.md, plan.md and `m3-state.md` date D50's default, D51 and cost v6 to 2026-11-05, but #95 and #96 merged on 2026-11-04.
- **Memory.** `m3-state.md` grew into one 11 KB paragraph with "OLD NEXT" lists, and its description still reads "Where M3 stood on 2026-11-03".

**Root cause.** Each decision row was updated in the same PR as the work, as the plan requires, but the same facts restated elsewhere weren't. A 111 KB plan that says one thing in several places invites this.

### 4.11 Supply chain gaps

PR CI builds and smoke-tests the image but doesn't run the release's Grype scan, and no workflow runs on a schedule. So the deployed image isn't rescanned between releases: the findings that stopped image-v0.0.17 were already in production on image-v0.0.16 (transcript, 2026-11-06). Dependencies that only upstream can fix stay a standing cost. Infracost's closed-source parser plugin carries three dated exceptions, one on CISA's known-exploited list, to review by 2026-12-06. conftest and the Infracost CLI run as patched source builds until upstream releases fix their findings. Infracost's telemetry also can't be turned off, and self-hosted pricing is still in the backlog (plan, risks and backlog).

---

## 5. Decisions in hindsight

The plan began with D1–D18 (`bfab978`) and ended with D59. D19–D29 came from the M1 council, D31–D34 from M2's spike, and most of the rest from building or measuring something. Each row says why. Reversals landed in the same PR as the evidence for them, so the log reads as a history of what was learned.

### Held up well

| Decision | Why it held |
| --- | --- |
| D3, one image everywhere | The service reused the runner's logic unchanged |
| D9 and D55, advisory with blocking only on deterministic highs | No model output can block; blocking was proved on demo #48 and reverted in demo #49 |
| D21, two jobs and a separate review identity | PR code never holds LLM credentials; the skeptic's top risk |
| D22, address-keyed pointers | The cache, batches and dismiss markers used them without change |
| D27, config read from the base commit | A PR can't disable its own review or swap the corpus |
| D28, a Grype gate with narrow exceptions | It stopped two releases; the exceptions stayed narrow and dated |
| D30, Azure IDs as secrets | Logs are masked; extended to `azurerm_client_config` |
| D32, canaries and no-resource checks | Silent scanners became failures |
| D35, code routes | No review can be silently skipped by a model |
| D36, the arbiter's limits | It no longer drops correct findings |
| D42 and D50, references can't create or touch findings | A poor retrieval costs a sentence at most |
| D45, OIDC claims plus released commits | Proved with real tokens before enforcement |
| D46, azapi where azurerm reads secrets | The plan identity stays a Reader |
| D47, sealed scanners | One repo's Rego can't reach the service's identity |
| D48, review plans without the lock | Eight seed pushes at once now pass |
| D53, the whole-input cache key | Nothing stale can come back; re-pushes cost nothing |
| D59, a state key per repo, rolled out in steps | Three repos encrypted in one evening without an outage |

### Reversed or reshaped, and what triggered it

| From | To | Trigger |
| --- | --- | --- |
| .NET 10 (D2) | .NET 11 RC3 and C# 15 | A spike with the M1 packages (#5); the costs were CSharpier dropped, csharp-ls false errors and a GA re-pin still to do |
| gpt-5.4-mini for every role (D4) | gpt-6.1-sol for every role, mini as fallback, luna rejected | Three full eval runs (#108–#110) |
| An LLM router | Code routes, the model orders within tiers (D35) | The M2 design round: `applies_to` and topics already route |
| A full LLM arbiter | Rule-based in M1 (D20), then limited and unable to drop (D36) | One agent in M1; the C9 drop (#41) |
| A `tf-references` repo | Imports in Cosmos only, pinned from `corpus/sources.yml` (D43) | The developer's question (transcript, 2026-11-02) |
| References per agent | References per finding (D50) | The v6 trial's off-point sentences (#93) |
| Re-reporting policy findings | Answering them by ID (D51) | Held-out C28 and C29 (#94) |
| 30K TPM as a spend brake | 100K TPM and a pacer (D54) | Demo #44 (#106) |
| Feedback by reactions | A checkbox per finding (D56) | Reactions apply to a whole comment |
| A cache per resource change | A cache per model call (D53) | Agents review a slice in one call |
| `infracost diff` against a `main` baseline | Two scans over rebuilt plans (D31) | The spike: `diff` matches by file path |
| 3 of 3 runs per case | 5 runs per case | "3 has misled twice" (#41) |
| Permission rules in the plugin | Hooks (D57) | A plugin can't ship permission rules |

### Would decide differently

| Decision | In hindsight |
| --- | --- |
| D29, a 3-of-3 gate (the developer's pick over 10 of 10) | Cheap, but it misled twice. Start at five or more runs per case |
| D37, the service, Cosmos and vectors together | Right for learning, wrong as a product bet. Name the metric first, and build retrieval once a baseline shows room |
| ADR-003 applied by a model | Make thresholds machine-readable and compute them in code; the model kept the only arithmetic in the system and was its most persistent flake |
| D19's deliberate Rego gaps | Fine for testing the agent, but they inflate the model's apparent detection value. Measure the model on gaps that occur naturally |
| The orchestrator's LLM call | Measure "Look at first" on real PRs or remove it; the open question belongs in the plan |
| D23's dev image | It never ran. Build a path when it's first needed |
| D23 and D45 bookkeeping | Keep the controls and automate the PRs they cause |
| The pacer's constant | Keep a limit in one place, next to the deployment's capacity |

**The four overrides.** The developer took the recommended option on 23 of 27 questions. The four exceptions are worth reading together:

- **D25, redaction.** Marks plus a name denylist, over the recommended shape allowlist. No leak has been seen, and the known gap, an unmarked secret under an ordinary name, is pinned by a test (#17). Revisit it before onboarding client repos.
- **D29, the M1 gate.** Three runs per case, over the recommended ten. It passed, then misled twice, and M2 moved to five runs (#41).
- **D37, M3 scope.** Everything at once, over "service now, RAG next". The recommendation's prediction held for suggestions, and the learning goal was met.
- **D38, runner mode.** Replace the runner, over "keep both". It held: one path to maintain, a proven outage drill (demo #39), and #69 removed the old path's identities and secrets.

The two overrides of a recommendation built on evidence (D29, D37) are the two that cost something; the two overrides of a cautious one (D25, D38) have held so far.

---

## 6. Evaluation methodology lessons

1. **Score the whole set of findings.** An extra finding has to fail a case like a missed one, or the suite rewards a model that reports everything. Five of the 31 cases expect no findings at all (C8, C10, C13, C14, C17), and three of those are near misses written to tempt over-reporting.
2. **Three runs is anecdote.** 3 of 3 misled twice (#41). The first baseline claimed 17 of 17 and was corrected. Even 25 clean runs of each near miss on gpt-6.1-sol "can't rule out a rare miss" (baseline.md, near misses).
3. **Repeat across days.** With nothing changed, the cost agent missed 4 of 15 on one day and 1 of 30 on the next (#96). A single day's run can blame or clear a change wrongly.
4. **Separate the tiers by what they can see.** Replay checks consistency for free, live runs measure the model, and the real-service run measures the system. Only the last found the state lock race (D48).
5. **Treat held-out cases as a budget.** Each experiment used up the cases it was measured on: C11–C17 for the security prompt, C18–C21 for references, C22–C26 for the v6 trial and C27–C31 for D50. After #93 the notes read "any next suggestion experiment ... needs fresh held-out cases", and after #94 all of C18–C31 had been read (`m3-state.md`). C13 stopped being held out once it was tuned against, and C17 was written to test whether the fix generalised (baseline.md).
6. **Scoring free text with regex is brittle.** C20's `back ?up` missed "backed up". C25 and C27 missed "retain the existing" and "avoid replacement", which made gpt-6.1-sol look weaker on data stores for a day and put a prompt task on the list (#111). The repair was done well: the pattern was widened only to the same concept, every saved run was rescored, and gpt-5.4-mini's and luna's scores didn't move, so the change didn't favour the model it was read from. Better still is to test patterns against paraphrases, or several models' answers, before the first scoring.
7. **Read the answers.** Scores hid `"reason":"null"` (#40), C13's "does not violate" title (#41), v6's 8 of 19 off-point sentences (#93) and the ambiguous question behind the cost retractions (#96). `--show-answers` made reading the norm.
8. **A score can't prove a fix; removing the cause can.** Cost v5 passed 29 of 30 on the day v6 was measured, so the evidence for v6 was that it removed the ambiguity a failed answer showed (baseline.md, Cost v6).
9. **The order of schema fields matters.** `breaks_standard` as the first field made the model decide before reasoning; as the last, it decided after writing its title (#41).
10. **Retrieval needs its own evals, labelled before measuring, by someone who can't see the answers.** References can't create findings, so agent evals can't see retrieval quality (retrieval.md). Hand-written queries reached 59%, which the agent withdrew because it wrote them knowing the labels (transcript, 2026-11-03). One person picked the labels, the cases and the concept patterns, which the docs say plainly.
11. **Compare models fairly.** Use the same day, the same code and several runs to see each model's range (mini went 88 to 92). Count cache writes the price list leaves out, and break results down per case to see whether a split by agent would help. Here it wouldn't, because every difference sat inside the security agent (baseline.md, Candidate models).
12. **Measure the configuration you ship.** gpt-6.1-sol was measured without references, and per-finding references were measured on gpt-5.4-mini. Sol with per-finding references, which is what runs, was proved on draft PRs (demo #46) but has no full-suite result in baseline.md, whose sol runs say "these runs have none".
13. **Write down what the evals don't measure.** Real plans at scale, the arbiter's merging across many findings, Infracost's real prices (all scanner output is replayed) and the rendered comment (baseline.md). That list is the honest boundary of every number above it.

---

## 7. Working with AI agents on this project

### The division of labour

The agent implemented each piece, opened the PR with a description, ran the checks, proved the change on a draft demo PR and updated the plan and the eval docs. It ended each step with "For you:" and the exact commands. The developer answered the decision questions, merged with a squash merge, ran bootstrap from the laptop, approved `prod`, pushed signed tags, kept every secret in `pass` and created the GitHub App. Roughly half of the developer's 254 messages in the main session were acknowledgements such as "#104 merged and tag pushed", and about a fifth were command output pasted back. That rhythm was fast and safe, because every irreversible step had a person on it, and it made the developer's availability the pace of the project. Sessions stopped for sleep, several after midnight, and the agent waited.

### Decisions: questions with a recommendation, and a council

Decisions came as structured questions: twelve rounds, 27 questions, each option with its trade-off and one marked as recommended. Every answer went into the plan as a numbered decision with its reason, in the same PR. That made later reversals cheap to make and easy to follow.

For M1 the agent convened four read-only planners: a core pipeline architect, an agents and retrieval architect, a delivery and CI architect, and an adversarial reviewer. The skeptic's report listed risks that came true or shaped the design:

- branch push equals the plan identity (D21);
- a live Stop hook at $54 to $270 a month, hence the free replay;
- conftest's silent false-clean (#19);
- the config read from the PR head (D27);
- a 3-of-3 gate passing by luck (D29).

It also listed more than a dozen contradictions in the plan. The four members ran for about five minutes (subagent transcripts, 2026-11-01), which makes an adversarial council one of the cheapest checks in the project.

### Where the developer's questions changed the design

The developer's questions improved the project more than once:

- "Can you double check the tofu apply actions not to disclose any sensitive ids" exposed the IDs in public logs (D30).
- "Explain further why we would need the tf-references repo" removed a repo: "Honestly, we don't need it, and your design is better" (D43).
- "Shouldn't we fix the secret value?" replaced a workaround with a fix (#60).
- "Why needs the service to be running all the time?" corrected the agent's wrong description of D16.
- Questions about the embedding model, model-written queries and cleaning the corpus each became a measured experiment. Two were rejected with data (model-written queries at 27%, an index without rule JSON at 11%), and one led to re-ranking (D49).
- "Can the 6.1 sol model or 6.0 luna model have better results" started the model comparison.

### Continuity across compactions

`plan.md` (103 revisions) and the memory notes carried the state across five compactions and one interrupted session. After the interruption on 2026-11-04 the agent picked up from branch state and memory, restarted the interrupted measurement and found the cost agent's cause within minutes (transcript, 2026-11-04). Two weaknesses showed. `m3-state.md` became an append-only log that is hard to read and partly stale. And an open question, whether "Look at first" earns its call, lived only in memory, where the developer doesn't see it.

### Mistakes, and how they became rules

The agent stated its mistakes plainly ("This lock is my fault", "That's my fault for stacking", "I found a mistake of mine: Azure IDs ended up in public logs") and turned each into a rule in memory or the project. The habit mattered more than any single fix: by the end, the memory notes and the plan held the operational rules that the first days had to learn.

### The sandbox and the permission system

Both cost time and both earned it. The sandbox forced recipes that work inside it from the start: `just lint` without plugins, `just validate` outside, `dotnet build -m:1` and commits with `-F <file>`. Its exclusions match only whole calls, so a single pipe or redirect silently sandboxed a command several times. The permission check stopped force pushes, remote deletes and a mass delete of cloud blobs, and the answer every time was an exact command for the developer. Machine-wide fixes went to a separate home-directory session through a written prompt, which kept the project session from editing machine config but added round trips in M0 and M1.

### Review depth and pace

The median `tf-review` PR merged nine minutes after it opened, CI included, and 70 of 125 merged within ten minutes. Fifteen PRs over 500 added lines merged within fifteen minutes, among them the redactor (#17, 1,192 lines, 8 minutes) and the review API (#52, 1,183 lines, 6 minutes). The counts include fixtures and recorded answers, so they overstate code. At this pace the developer's review checked intent, CI and proof rather than reading every line, which was a reasonable choice for a six-day build. It means correctness rests on the 519 tests, whose redaction tests prove each layer catches what the others don't, on the evals, on the proofs on real PRs, and on independent review, which the developer commissioned at the end with parallel review agents.

---

## 8. Recommendations for the next project

### Keep

1. **A plan with a decision log** (ID, choice, why) that changes in the same PR as the work. Elsewhere, refer to a decision by its ID instead of restating it.
2. **Unknowns first, real PRs last.** Start each milestone with spikes on the real platform, and end it on a real PR.
3. **A walking skeleton** that posts an honest "not reviewed" before the first real feature.
4. **Code decides.** The model fills a flat schema and answers judgement fields last. Code sets severity, blocking and evidence and checks evidence before anything else sees it.
5. **Failure is visible.** Canaries, refusals on empty results, and a "not reviewed" that replaces any older result.
6. **Three eval tiers.** The free replay in the Stop hook and CI, whole-set matching, and a baseline document that records failures and limits.
7. **Proof PRs** on a demo repo for every release.
8. **A supply-chain gate from the first release.** Patch before excepting, and keep exceptions to one finding on one file with a date.
9. **Identity and log hygiene from day one.** OIDC everywhere, Reader plan identities, separate identities for PR code and model access, and Azure IDs as secrets in public logs.
10. **Decisions as questions with a recommendation and its trade-off.** The developer holds every irreversible step, and gets it as an exact one-line command.
11. **A council with an adversarial member** before any milestone that sets contracts or security boundaries.

### Change

1. **Automate the release bookkeeping.** A workflow can open the pin PR after an `image-v*` release, the released-commits PR and the demo repin after a workflow tag, and merge `main` into the seeds afterwards. That removes about 30 PRs and the seeds' silent skipping.
2. **Scan earlier and more often.** Run the release's exact vulnerability scan in PR CI, and rescan the deployed digest on a schedule.
3. **Budget the held-out cases.** Write a larger sealed pool at the start, by an author separate from the prompt work (the developer, or an agent that can't see prompts or answers), and log which cases have been read.
4. **Make concept scoring robust before the first run.** Test the patterns against paraphrases or several models' answers, or use a model as judge with spot checks. When a pattern widens, rescore every run, as #111 did.
5. **Start live evals at five or more runs per case**, and rerun near misses on different days.
6. **Put arithmetic in code.** Give standards machine-readable thresholds, and keep the model for judgement.
7. **Name the measure before building a capability**, together with the cheapest experiment that could move it. Learning goals included.
8. **Plan a new resource type under the least-privileged identity, with state,** before relying on it.
9. **Keep each limit in one place**, so the pacer reads the deployment's capacity rather than a constant.
10. **Keep memory notes as short, dated state files**, and move open questions into the plan's tasks.
11. **Decide what the developer reads line by line**: contracts, identity and IAM changes, redaction, prompts and infrastructure plans. Run independent agent reviews at every gate, not only at the end.
12. **Track real spend** from Azure billing next to the list-price estimates.
13. **Plan history clean-up as part of any migration of data at rest**: blob versions, soft delete and their retention.

### Add

1. **One question in every design review:** "How would we know if this did nothing?" Each silent no-op in section 4.4 would have failed it.
2. **Measure the shipped configuration as a whole** after any change to the model, prompts or placement, and make that the baseline's "Current" section.
3. **Self-cleaning proof PRs**, closed and their branches deleted by the job that proves them.
4. **The gotchas below**, in the next project's CLAUDE.md or plan, so the next project starts where this one ended.

---

## Appendix: platform gotchas worth carrying forward

| Area | Gotcha | Source |
| --- | --- | --- |
| GitHub OIDC | Repos with immutable subjects present `repo:owner@owner-id/repo@repo-id:…`, so federated credentials need the numeric form | `d577cc4` |
| GitHub Actions | A run's `vars` are resolved when the run is created; a run waiting for approval keeps the old values | #119 |
| GitHub Actions | A PR that conflicts with its base gets no `pull_request` runs, silently | #84 |
| GitHub Actions | A concurrency group keeps only one waiting run, so it can't queue plans | D48 |
| GitHub Actions | With applies serialised, a run waiting for approval holds up the next one | #81, #82 |
| GitHub | Merging a PR deletes its branch and closes PRs stacked on it | #80 |
| Actions logs | Only secrets are masked, and `azurerm_client_config` prints the Azure IDs in base64 | D30, #50 |
| pinact | Refuses branch refs, so testing an unmerged workflow needs a release candidate tag pinned by SHA | #16 |
| azurerm | Lists keys or secrets when reading Cosmos accounts and Container Apps, so a Reader's plan gets a 403 | D46, #49, #58 |
| azurerm backend | Fails at once on a lease with no lock details, whatever `-lock-timeout` says | D48 |
| azurerm 5.x | A Container Apps environment needs `logs_destination`, and `TLS1_0` is no longer accepted | #49, demo #32 |
| Cosmos DB | Full-text search needs only the container's policy and index, and the old capability makes the request invalid; vector and full-text policies can't change later | #73, #75 |
| OpenTofu | The `azure_vault` key provider ignores `ARM_USE_OIDC`, so set `use_oidc` in the block | demo2 #2 |
| OpenTofu | A lock-taking command piped into something that closes early can leave the state locked | `hand-over-destructive-git.md` |
| Azure Storage | Deleting previous blob versions can fail with `OperationNotAllowedOnAutomaticSnapshot`, but a lifecycle rule works | #126 |
| conftest | Needs `--all-namespaces`; exit 1 means failed or errored; `conftest test` treats a helper named like `violation` as a policy | #19, tf-standards #4 |
| Checkov and Infracost | Exit 0 with no results on a plan they don't recognise, and Checkov needs `terraform_version` | D32 |
| Infracost | `infracost diff` matches by file path; the CLI runs an org tagging policy of its own; telemetry can't be turned off | D31, plan M2 part 1, plan risks |
| Azure OpenAI | gpt-5.4-mini's default reasoning effort is none, and low fixed C5 | #27 |
| Agent Framework | Hides the finish reason, which a `DelegatingChatClient` can expose | plan, M1 part 5 |
| JSON Schema | An `$id` made a `$ref` resolve against the published copy instead of the local file | #13 |
| hadolint | DL3066 rejects a non-numeric `USER` | #14 |
| .NET in the Claude sandbox | `dotnet build` of a solution hangs without `-m:1`, and `dotnet test` must run outside | plan, Working with Claude Code |
| Claude Code sandbox | Exclusions match the whole call: a heredoc, pipe, redirect, `cd` or chain keeps a command sandboxed, so signing fails | `commit-with-message-file.md` |
| Claude Code plugins | A plugin can't ship permission rules, so guardrails become hooks | D57 |
