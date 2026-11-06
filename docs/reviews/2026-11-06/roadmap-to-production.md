# tf-review: roadmap to production

Prepared 2026-11-06, the day v1 completed, from tf-review at `e5cf681`, tf-review-demo at `ebe24a0`, tf-review-demo2 at `1491e3d` and tf-standards at `adf50af`. Decision IDs (D1–D59) are the rows of the decision log in `tf-review/docs/plan.md`. Paths are relative to the repo named, or to `tf-review` when none is named.

**The scenario.** A company of 20–80 engineers adopts tf-review to review the OpenTofu (and some Terraform) PRs across 10–40 infrastructure repos in one GitHub organization. It has one Azure tenant, perhaps some AWS or GCP, a modest budget, and no team whose job is to run this.

## Contents

1. Executive summary
2. Current-state assessment
3. Phased roadmap: phases 0–3, then design notes by area
4. Risks, open decisions, and build versus buy
5. Cost projection
6. Appendix: code and config changes by file

---

## 1. Executive summary

### Verdict

**Not production-ready for this company today, and a sound base for a pilot.**

The review path is more careful than most v1s:

- PR code never holds model credentials (D21).
- The API trusts only a GitHub OIDC token from an allowlisted repo's review job running a released workflow commit (D45).
- Scanners run sealed (D47), and plans are redacted before they leave the plan job (D25).
- Only deterministic high findings can ever block (D9, D55).
- Every failure posts "not reviewed" for that commit instead of leaving a stale result standing (D39).

The quality is measured, not claimed: 519 test methods, a 31-case eval suite run five times per case with held-out cases (gpt-6.1-sol passed 155 of 155 on three full runs, `docs/evals/baseline.md`), and a real-service eval over eight seeded PRs (`just service-eval`).

What it isn't yet is a service someone other than its author can own and run:

- It's wired to a personal GitHub account and App, a personal pay-as-you-go subscription and a laptop bootstrap.
- It has one environment, no alerting beyond a budget e-mail, no runbooks and one maintainer.
- Its onboarding, data handling and capacity were sized for three public demo repos.

### Top blockers (clear these before the first company repo)

1. **A personal identity is part of the trust chain.**
   - The API accepts only `gergodrazsdik/tf-review/.github/workflows/tf-review.yml@…` (`src/TfReview.Api/CallerPolicy.cs:28`).
   - The sticky comment trusts only `tf-review-gergodrazsdik[bot]` (`src/TfReview.GitHub/StickyComment.cs:18`).
   - The workflow verifies images against the personal repo's signing identity, and defaults to the personal image and API URL (`.github/workflows/tf-review.yml:27–31, 88–95`).
   - The App (ID 5584299) and its private key (in the author's `pass`), the subscription and the bootstrap's GitHub token are all personal.
2. **Data governance isn't cleared.**
   - There's no data protection impact assessment (DPIA), record of processing, or review of the data processing agreements (DPAs).
   - Infracost v2 telemetry can't be switched off and sends the repo URL and branch with every scan (plan.md, "Costs and risks"). The plan's own backlog puts self-hosted pricing "before any client repo is onboarded".
   - Review statuses never expire (`infra/service.tf`, `default_ttl = -1`).
   - The redactor catches marked values and secret-like attribute names, but has no pattern or entropy detection (D25's known gap).
   - A private standards repo can't be read: `GitHubTarballSource` downloads from `codeload.github.com` without a token.
3. **No environments and no operations.**
   - Only `prod` exists.
   - The only alerts are the subscription budget's 50/80/100% e-mails to `git config user.email` (`bootstrap/main.tf`, `justfile`).
   - There's no SLO, no runbook and no second maintainer.
   - Two failure paths break D39's promise. When the API is unreachable, the workflow's fallback posts a comment but no check run (`src/TfReview.Cli/Program.cs`, `Publish`). And a review accepted with `202` whose job never runs leaves no trace on the PR at all.
4. **Onboarding treats the reviewer as the IaC platform, and assumes one shape of repo.**
   - A bootstrap `repos` entry creates the repo's state container, target resource group, plan and apply identities, state key, and Actions secrets (`bootstrap/repos.tf`, `bootstrap/state_keys.tf`). It then needs a bootstrap apply, a manual `infra.yml` dispatch and an App install (the M4 gate).
   - The reusable workflow hard-wires the azurerm backend, GitHub-hosted runners and one project per PR.
   - Two projects in one PR share a concurrency group, an artifact name, a sticky comment and a check run.
   - Projects with the same leaf directory share a state key (`key=${PROJECT##*/}.tfstate`).
5. **Free tiers and quota are sized for one person.**
   - Cosmos's free tier allows one account per subscription (D13).
   - Infracost's free tier is reportedly about 1,000 pricing runs a month, and each review uses two (D31).
   - Log Analytics is capped at 0.2 GB a day.
   - Up to three review jobs run at once, each pacing itself to 90K TPM against a 100K TPM deployment (`TokenPacer`, `infra/apps.tf`, `infra/main.tf`).
6. **A release-candidate runtime and a dated security exception.**
   - The SDK and image are .NET 11 RC3 until the GA re-pin (GA is scheduled for 2026-11-10).
   - The Infracost plugin's Grype exceptions include brotli GO-2026-6888, which is on CISA's known-exploited list and due for review by 2026-12-06 (`.grype.yaml`).

### The plan

| Phase | Goal | Calendar | Engineering effort | Gate to leave it |
| --- | --- | --- | --- | --- |
| 0 | Re-home in the company, govern the data, make it operable | 6–8 weeks | 40–60 person-days | Nothing personal left in the trust chain; nonprod and prod deployed from CI; DPIA approved; alerts tested |
| 1 | Pilot on 3–5 repos | 8–10 weeks, 6–8 of them piloting | 35–60 person-days | Go/no-go on measured value, noise, reliability and cost |
| 2 | Roll out to every IaC repo | 3–4 months | 50–80 person-days (35–55 without AWS/GCP) | SLOs held two months; pentest highs fixed; onboarding self-service |
| 3 | Maturity | The following 6–12 months | 35–65 person-days | Quarterly value review |

**Total effort.** Production-grade across the estate (the end of Phase 2) takes about **125–200 person-days, or 6–10 person-months**. That's roughly 7–9 calendar months with one to one and a half engineers, plus part-time security and standards authors. Phase 3 adds 35–65 person-days over the following year, and running it takes 2–4 person-days a month.

The figures assume an engineer new to the code, using the AI-assisted workflow it was built with. v1 went from nothing to its M4 gate between 2026-11-01 and 2026-11-06, so the original author could likely halve the engineering part. That speed doesn't shorten the approvals, the model-quota lead time, or the pilot's measuring window.

**Running cost.** About $30–80 a month for the small end of the scenario, $55–230 in the middle, and $100–430 at 80 engineers and 40 repos (planning figures about $55, $145 and $280), against today's €30 budget (D15). Model tokens dominate (§5). People dominate the total cost of ownership.

**Checkpoint.** At the end of Phase 1, decide build versus buy against the criteria in §4.3. Stopping doesn't lose everything: the standards repo's Rego and tests run under conftest in any CI, without the service.

---

## 2. Current-state assessment

**Ratings:**

- **Ready:** the company can use it as it is.
- **Partial:** the mechanism is sound, but the owner, scale or coverage is wrong.
- **Missing:** not there.

| Area | Rating | What exists | Gap for the company |
| --- | --- | --- | --- |
| GitHub ownership and App | Missing | App `tf-review-gergodrazsdik`, private to the personal account. Each review gets a token for one repo, with `contents: read`, `pull_requests: write` and `checks: write`, revoked afterwards (`GitHubApp.cs`, `RepositorySession.cs`) | Org-owned Apps; the bot login and trusted workflow as configuration; an installation policy |
| Azure tenancy and Entra ID | Partial | OIDC everywhere and no stored cloud secrets; user-assigned identities per repo with federated subjects built from immutable IDs (`bootstrap/repos.tf`); key auth off on Foundry, Cosmos and storage | One personal PAYG subscription (D13). Bootstrap runs from a laptop with `az login` and a fine-grained PAT from `pass`. Personal `deployer_*` role assignments. One user approves every `prod` environment |
| Environments for the service | Missing | `infra/` deploys `prod`; `dev-image.yml` builds signed dev images | No nonprod deployment, promotion path or test App |
| Least privilege | Partial | Plan identities are Readers on their resource group; LLM credentials stay out of PR jobs (D21); scanners sealed (D47); App tokens limited to one repo | Service roles at resource-group scope (`bootstrap/service.tf`). Plan identities hold Blob Data Contributor on state, so PR code can overwrite it (plan.md risk table). `kv-tf-review-prod` has purge protection off and 7-day soft delete. No Key Vault audit logs. Rulesets need 0 approvals (`.github/rulesets/main.json`). No CODEOWNERS |
| Onboarding at scale | Partial | One `repos` entry plus the template, proved on demo2 (M4 gate, D40) | Four steps through one person; the reviewer is also the state platform; azurerm backend, GitHub-hosted runners and one project per PR only; the template hardcodes the reviewer's state vault (`tf-review-demo/infra/versions.tf`) |
| Data governance | Partial | Redaction (D25): marks, names, a sweep, GUIDs and a verifier, refusing on doubt, with the HCL `configuration` dropped. Plans kept 7 days, cached answers 7 days (D53), a 1-day artifact. Logs hold names and counts only. EU Data Zone (D4, D8) | No DPIA or DPA review; Infracost telemetry; statuses kept forever; full plan text in Actions logs; no pattern-based secret detection; public-only standards; personal data in tags (`owner`) not considered |
| Security assurance | Partial | Threats reasoned decision by decision (D21, D25, D36, D45, D47, D55); C16 tests a prompt injection | No consolidated threat model or pentest. The App key is held in worker memory (D47). That PR code in the plan job can feed the reviewer a doctored plan isn't stated as a limit of blocking |
| Supply chain | Partial | Digest-pinned bases, hadolint, Syft SBOM, a Grype gate with one-file exceptions (D28), cosign keyless signing plus an SBOM attestation, consumers verifying the signer before PR code runs (D23); actions pinned by SHA (`pinact`); CI tools by SHA-256 (`scripts/install-ci-tools.sh`) | No SLSA provenance; no NuGet lock files; updates by hand through the author's `update-sources`; public Rekor entries would reveal a private fork; one exception is on CISA's known-exploited list |
| Reliability and operations | Missing | Honest failure (D39); queue retries (3 tries, 20-minute visibility); result cache (D53); dashboard (D58) | No SLO, alerts, runbooks, support model, staging or model failover in code; two failure paths that leave a PR without a check or without any answer (§2.2); a 0.2 GB/day log cap that drops data silently |
| Backup and DR | Partial | State has versioning, 30-day soft delete, a `CanNotDelete` lock, and keys with purge protection (D59). The corpus rebuilds from its pins (D43, D44). Everything else is code | LRS storage; no restore drill; one region; the App key and Infracost token live in the author's `pass` and Key Vault |
| Capacity, quota and models | Partial | Batching (D54), `TokenPacer`, gpt-5.4-mini deployed as a fallback | Pacing is per review across three concurrent jobs; switching to the fallback needs an infra change; Tier 1 quota; gpt-5.4-mini retires 2027-09-21 and gpt-6.1-sol 2028-03-11 |
| Cost model | Partial | Measured eval cost; `ModelPrices` estimate per call; dashboard tile; €30 budget with alerts (D15) | No cost per repo; estimates leave out GPT-6 cache writes; budget alerts on actual spend only; free-tier dependencies |
| Quality and evals | Partial | 31 cases with held-out discipline, 5 runs each; retrieval evals; `service-eval`; a dismissals metric (D56) | Cases are demo-sized Azure plans judged against tf-standards; the harness hardcodes its cases and standards repo; no company standards; no stored findings to measure value from; `comment_min_severity` is parsed and ignored; no suppressions |
| Product coverage | Partial | OpenTofu and Terraform through the `tool` input (D1); opt-in blocking (D55); references (D50); Claude Code plugin (D57) | Azure-only scanners, corpus and pricing; no monorepo, Terragrunt, Terraform Cloud or module-repo support; no suggested changes; making the check required is unsafe during an outage |
| Documentation | Missing | `docs/plan.md` is a thorough engineering plan; evals are written up | The README and the template's README are one paragraph each; no operator, onboarding, user or standards-authoring guide |
| Ownership, licensing, currency | Partial | Apache-2.0 (D14); corpus licences kept per chunk (D41) | Bus factor of one; .NET 11 RC; closed, unsigned Infracost plugins; conftest and the Infracost CLI built from source |

### 2.1 What's tied to one person, one region and the free tiers

| What | Where | Today |
| --- | --- | --- |
| Trusted workflow | `src/TfReview.Api/CallerPolicy.cs:28` | `gergodrazsdik/tf-review/.github/workflows/tf-review.yml@` |
| Bot login | `src/TfReview.GitHub/StickyComment.cs:18`, `scripts/service_eval.py:17`, `scripts/feedback_cases.py:22` | `tf-review-gergodrazsdik[bot]` |
| Image and API defaults, signer | `.github/workflows/tf-review.yml:27, 31, 88–95`; `infra/apps.tf:12` | Personal GHCR package; `ca-tf-review-api-prod.yellowpebble-7ff92100.germanywestcentral.azurecontainerapps.io`; identity `github.com/gergodrazsdik/tf-review` |
| App client ID | `infra/apps.tf:154` | A literal |
| Standards repo | `.tf-review.yml`; `ci.yml:39`; `e2e-live.yml:52`; `tests/TfReview.Eval/Program.cs:22`; the demo's `.tf-review.yml` | `gergodrazsdik/tf-standards` at `adf50af` |
| Owner and approvers | `bootstrap/variables.tf:15`; `bootstrap/repos.tf` (`github_repository_environment.prod` reviewers); `owner` tags in `infra/*.tf` and the demo | One user |
| Secrets source | `justfile` (`secrets-sync`, `bootstrap-*`) | Personal `pass` store |
| Region | `bootstrap/variables.tf:21`; the Cosmos `locationName` literal at `infra/service.tf:153` | Germany West Central |
| Resource names | `infra/*.tf`; `bootstrap/*.tf`; `justfile:74–83, 132–134`; `corpus.yml:80`; `e2e-live.yml:78`; the demo's `infra/versions.tf` (state vault) | `*-tf-review-prod`, `kv-tf-review-state-prod` |
| Free tier | `infra/service.tf:146` | `enableFreeTier = true` |
| Capacities | `infra/main.tf:66, 90`; `infra/service.tf:183, 207, 222`; `infra/apps.tf:78, 130`; `TokenPacer.DefaultTokensPerMinute` | gpt-5.4-mini at 50K and gpt-6.1-sol at 100K TPM; 400 + 600 RU/s; 1 API replica; 3 jobs; 90K per review |
| Logs | `infra/service.tf:97, 100` | 30 days, 0.2 GB a day |
| Budget | `bootstrap/variables.tf`; `bootstrap/main.tf` | €30 a month, actual-spend alerts to one address |
| Scanner and provider coverage | `CheckovRunner.cs:67–71`; `References.cs:81`; `corpus/sources.yml`; the `Containerfile` | Five `CKV_AZURE` checks; azurerm docs only; Azure pricing plugin only |

### 2.2 Where D39's promise breaks

D39 promises that every failure posts "not reviewed" for that commit. Three paths don't keep that promise yet:

- **The API is unreachable.** The review job's "Say not reviewed" step runs `tfreview publish`, which only upserts the comment (`src/TfReview.Cli/Program.cs`). Only the worker posts check runs (`ReviewWorker.PostAsync`). If a ruleset required the `tf-review` check, every PR would block during the outage. D55's "an outage mustn't block a merge" holds only when the worker posts.
- **The queue never runs the job.** Examples are the scaler losing its role, or an image that won't pull. The API has answered `202`, the workflow has finished green, and the status stays `queued`. Nothing reaches the PR.
- **Retries take time.** A job that dies is retried after the message's 20-minute visibility, up to three times (`ReviewWorker.Visibility`, `MaxAttempts`), so "not reviewed" can arrive about an hour late.

---

## 3. Phased roadmap

**Principles that carry through every phase:**

- Models advise, and only deterministic findings may block (D9, D55).
- Every prompt, model or standards change is measured by the evals before it ships, as `docs/evals/baseline.md` does today.
- The deterministic core stays usable on its own.
- Anything specific to a deployment becomes configuration, not a fork of the code.
- Each new decision becomes a row in `docs/plan.md` (D60 onwards), as the plan's own rule requires.

**Effort scale:**

- **S:** up to 2 person-days.
- **M:** 3–8 person-days.
- **L:** 9 or more person-days.

**Item types:**

- **Change:** existing behaviour has to change.
- **Improve:** it works, and needs to be better.
- **Add:** it's new.

### 3.1 Phase 0: before the first company repo

Make tf-review the company's, decide what data may leave, and make sure someone hears when it breaks.

| ID | Type | Item | Why | Effort | Depends on |
| --- | --- | --- | --- | --- | --- |
| P0-01 | Add | **Ownership and hosting decision.** Owning team (platform), co-owner (security), a product owner and two maintainers; fork into the org; repo and image visibility; the GitHub plan; IP sign-off with the author. Record as D60 | Every later item needs an owner. Apache-2.0 (D14) allows the fork, but who owns the code, the App and the standards must be explicit | S (1–2) | – |
| P0-02 | Change | **Take the personal account out of code and trust.** The trusted workflow, bot login, image, API URL, signing identity, App client ID, owner tags, script constants and contract `$id`s become org values or configuration (Appendix 6.1) | They're the trust chain: D45's caller check, D39's comment ownership, D23's image verification | M (3–5) | P0-01 |
| P0-03 | Change | **Org-owned GitHub Apps.** One for prod and one for nonprod, with the same three permissions, installed on selected repos only. An App manager generates each key straight into Key Vault. Two App managers, and a rotation runbook | The personal App and its key in `pass` can't serve the company | S (1–2) | P0-01, P0-04 |
| P0-04 | Change | **The company's Azure landing.** Nonprod and prod subscriptions under the platform management group. Check the landing zone's Azure Policy (public network access, regions, tags, customer-managed keys) against `infra/` before building. Entra groups with PIM-eligible roles replace the personal assignments. Request Data Zone quota for gpt-6.1-sol and gpt-5.4-mini now | D13's single personal subscription. Quota took four days and one refusal last time (plan.md, Accounts). A policy denying public endpoints changes the design (§4.1, O7) | M (4–6), plus quota lead time | P0-01 |
| P0-05 | Change | **Bootstrap through CI** (plan.md, bootstrap step 4). Plan on PR, apply on `main` behind a team-approved environment, with its own identity. The GitHub provider authenticates as an org App instead of the PAT. `github_owner` becomes the org; environment reviewers become teams; the budget contact becomes a list. Split the `repos` map into reviewer onboarding and optional platform resources | Onboarding becomes a reviewed PR instead of one person's laptop, and the reviewer stops being the state platform by default | M (5–8) | P0-04 |
| P0-06 | Add | **Service environments** (§3.5.2). Parameterize `infra/` by environment: names, region, capacities, log cap, App, free tier. Deploy nonprod, then prod. Promote one image digest from nonprod to prod after `service-eval` passes on a sandbox repo | Today every release goes straight to the only environment | M (4–6) | P0-03–P0-05 |
| P0-07 | Add | **Data governance baseline** (§3.5.4). A DPIA and record of processing. Confirm the DPA, the Data Zone, and the abuse-monitoring position. Decide on Infracost: self-hosted pricing, paid with a DPA, or the cost agent off for the pilot. TTL on review statuses; retention for logs, Actions logs and artifacts | Company data leaves GitHub for Azure OpenAI and Infracost, and plan.md flags Infracost itself | M (3–5), plus DPO and security time | P0-01 |
| P0-08 | Improve | **Harden redaction** in `PlanRedactor`. Detect known secret formats: PEM keys, JWTs, cloud access keys, GitHub tokens, SAS signatures, credentials inside URLs. Add an entropy check for long opaque strings, and a configurable list of extra names and resource types. Tests fail when any layer is turned off, as now | D25's known gap: an unmarked secret under an ordinary name reaches the model, and possibly a comment | M (4–6) | – |
| P0-09 | Change | **Private standards.** Fetch the standards tarball through the App's token (`GET /repos/{owner}/{repo}/tarball/{sha}`), minted for the reviewed repo and its standards repo. Refuse standards repos outside the org | Company standards won't be public; D6 assumed they would be | S (2–3) | P0-03 |
| P0-10 | Improve | **Security baseline** (§3.5.5). A threat model document; the least-privilege changes in §3.5.1; purge protection and diagnostic logs on the service vault; rulesets with at least one approval, and CODEOWNERS on `prompts/`, `src/TfReview.Api/`, `infra/`, `bootstrap/` and `.github/`; an org Actions policy (SHA-pinned, allowlisted actions); `SECURITY.md` | Settings made for one maintainer (0 required approvals) have to become controls among several people | M (4–6) | P0-02 |
| P0-11 | Change | **Currency and supply chain.** The .NET 11 GA re-pin (`global.json`, both `Containerfile` digests), then `llm-smoke` and a full `e2e-live`. Clear or re-approve the brotli and compress exceptions by 2026-12-06. NuGet lock files restored in locked mode. SLSA build provenance in `release.yml`, verified in `tf-review.yml`. A signing decision for a private repo | An RC runtime in production; an exception on CISA's known-exploited list; provenance is the missing SLSA piece | M (3–5) | P0-02 |
| P0-12 | Add | **Minimum operability** (§3.5.6). An action group to the team's channel; the alerts A1–A10; the log cap from 0.2 to about 1 GB a day, with an alert when it's hit; `repository` on the cost and findings metrics; runbooks RB1–RB6; a daily canary review in each environment | Otherwise a silent failure is found by a developer, not the owner | M (4–6) | P0-06 |
| P0-13 | Change | **Quota and spend guardrails.** The `TokenPacer` budget from configuration, set to 90% of capacity divided by the job's `maxExecutions` (or one job at a time during the pilot); the model fallback written as a runbook; budgets with forecast alerts | Three concurrent jobs can ask for 270K TPM from a 100K deployment | S (1–2) | P0-06 |

**Phase 0 total: about 40–60 person-days.**

**Exit criteria:**

- `git grep gergodrazsdik` in the company forks finds nothing outside history and the plan's past entries.
- Every deployment-specific name comes from variables.
- Nonprod and prod are deployed from CI in company subscriptions.
- No role assignment names a person; bootstrap is applied from CI.
- The Apps are owned by the org and installed only on the sandbox (nonprod) and pilot (prod) repos, and their keys exist only in Key Vault.
- The DPIA is approved, the Infracost decision is recorded, and the retention settings are applied.
- Security has reviewed the threat model; the least-privilege table is done, or each deviation is accepted with a reason.
- A .NET 11 GA image is released.
- A full `e2e-live` passes at least 153 of 155 runs, and `service-eval` passes 8 of 8 in nonprod.
- The brotli exception is resolved or re-approved before 2026-12-06.
- A test alert has reached the channel, and runbooks RB1–RB6 have each been walked through once.

### 3.2 Phase 1: pilot on 3–5 repos

Prove value on real repos, with the measurements that decide build versus buy.

| ID | Type | Item | Why | Effort | Depends on |
| --- | --- | --- | --- | --- | --- |
| P1-01 | Add | **Pilot charter.** Choose 3–5 repos: a template-shaped Azure repo; an existing repo with its own pipeline and backend; a repo with several root modules; and a Terraform repo if the company has one. Write down the success metrics (the exit criteria below), and pick one or two comparable repos as a control | The pilot has to answer the build-versus-buy question, not just run | S (1–2) | Phase 0 |
| P1-02 | Add | **Reviewer-only onboarding and bring-your-own-plan.** A second reusable workflow, `submit.yml`, runs only the review job, on a redacted plan the caller's own plan job made with `tfreview redact`. `CallerPolicy` trusts both workflows. `tf-review.yml` gains `runs-on`, `backend-config` and `state-key` inputs | Existing repos keep their backends, runners and pipelines, and bootstrap only adds them to the allowlist | M (4–7) | P0-02, P0-05 |
| P1-03 | Change | **Several projects per PR.** The state key comes from the whole working directory, with today's default kept for existing repos. Concurrency groups and artifact names are per project. The project goes into the review ID, the comment marker, the check run's name and the result (`review-result.schema.json`) | Today two projects in one PR cancel each other's reviews and overwrite one comment, and `envs/dev/network` and `envs/prod/network` share `network.tfstate` | M (4–7) | – |
| P1-04 | Add | **Company standards v0, and how to write them** (§3.5.7). Port ADR-001–003 and POL-001–004 to the company's names and tags, and add its 5–10 most important rules. Every high-severity policy gets Rego and `conftest verify` tests. A doc template; CODEOWNERS (platform for ADRs, security for policies); the `proposed` → `accepted` lifecycle the frontmatter enum already has | The reviewer is only as good as the standards it's grounded in | M (5–8), plus subject experts' time | P0-09 |
| P1-05 | Add | **Company eval suite v0.** `tests/TfReview.Eval` takes its cases directory and standards source as options (both are hardcoded in `Program.cs`). 20–30 cases from the pilot repos' history: PRs where a human caught something, reverts, incidents. Include clean and near-miss cases, and a held-out third written by someone who doesn't tune prompts. The suite also runs on standards PRs | The 31 existing cases measure the demo's standards on demo-sized plans ("What this doesn't measure", `baseline.md`) | M (5–8) | P1-04 |
| P1-06 | Add | **Findings history and value metrics.** Store each review's findings: rule, severity, agent, deterministic or not, a hash of the address, dismissed or not. Keep them about 13 months. Derive per rule and per repo: addressed (gone on a later push while the resource still changes), dismissed and blocking. Add review duration and queue wait as metrics, and dashboard tiles | Value can't be measured from counters alone, and today only counts reach App Insights | M (4–6) | P0-12 |
| P1-07 | Add | **Noise controls.** Apply `thresholds.comment_min_severity` (parsed in `TfReviewConfig.cs` and dropped). Add suppressions in `.tf-review.yml` with rule, address pattern, reason and expiry, read at the base commit (D27). Add an org-wide switch in the service config that turns one rule off | Without a way to silence a known-wrong finding, trust erodes fast | M (4–6) | – |
| P1-08 | Add | **An answer even when things fail.** A scheduled watchdog job posts "not reviewed" and a neutral check for reviews stuck in queued or running. It does the same for open PRs whose head has no `tf-review` check some minutes after their workflow ran. A fallback deployment in code takes over on `rate_limited` and `unavailable` | Closes the two gaps in §2.2, and turns the fallback model into a code path instead of an infra change | M (4–6) | P0-12 |
| P1-09 | Improve | **Run the pilot.** Weekly triage of dismissals and "not reviewed" results; fixes through the evals; a survey at week 3 and at the end; cost per review tracked against §5 | The exit criteria need data | 6–10 over 6–8 weeks | P1-01–P1-08 |
| P1-10 | Change | **Pilot with `references: false`**, unless the company eval shows a gain | References made no measurable difference to held-out suggestions (C27–C31, D50), and cost about 1.7 times as much per full run (`baseline.md`) | S (<1) | P1-05 |

**Phase 1 total: about 35–60 person-days.**

**Exit criteria (the go/no-go):**

- **Coverage:** at least 99% of pilot PR head commits get a result (any verdict) within 60 minutes, and 95% within 15.
- **Completeness:** at least 90% of reviews have no "not reviewed" scope caused by the service.
- **Precision:** overall dismissal rate at most 15%, and no rule above 30% with 10 or more findings unless it's fixed or suppressed.
- **Value:** at least 5 addressed findings per 100 reviews, and at least a third of them from model findings. If the model layer adds nothing over conftest and Checkov, §4.3 applies.
- **Developers:** at least 60% find it useful, and at most 20% find it noisy.
- **Cost:** cost per review within ±50% of §5.1, and no budget alert above 80%.
- **Operations:** 3 person-days a month or less. A second maintainer has done a release, an onboarding and a runbook drill alone.
- **Evals:** the company suite has at least 20 cases and a recorded baseline, and pilot dismissals have become cases.

### 3.3 Phase 2: general rollout

Make onboarding self-service, make the check safe to require, and cover every repo shape the company has.

| ID | Type | Item | Why | Effort | Depends on |
| --- | --- | --- | --- | --- | --- |
| P2-01 | Change | **Self-service onboarding.** Trust a repo because it belongs to the org and has the App installed: the token's `repository_owner_id`, plus a cached installation check. That replaces the chain from bootstrap to an Actions variable to an `infra.yml` apply (D40). Add an onboarding PR generator or template; Renovate or Dependabot for the workflow SHA, image digests and standards commit; and an org-level default config | At 40 repos, D40's four steps through one person don't scale, and each onboarding redeploys the API | M–L (6–10) | P0-03, P1-02 |
| P2-02 | Add | **Monorepos and Terragrunt.** Plan only the projects a PR changes (a matrix from changed paths), report one aggregated check, and review Terragrunt units through bring-your-own-plan. Document the plan limits: 16 MB per request (`ApiOptions.MaxRequestBytes`), and four batches per agent (D54) | Common company repo shapes | M–L (6–10) | P1-02, P1-03 |
| P2-03 | Add | **Make the check required, safely.** In org rulesets, require the App's `tf-review` check, pinned to the App, for repos that opt into blocking. Prove the watchdog in an outage drill. Set bypass actors and an outage runbook. Stages: advisory everywhere, then blocking on deterministic highs in willing repos, then required on production repos | D55 is safe only if an outage can never leave a required check missing | M (4–6) | P1-08 |
| P2-04 | Improve | **Capacity.** One token budget shared by all job replicas (a reservation in Cosmos, or one deployment per job slot), and `maxExecutions` sized to quota. Ask for a quota increase. Check Cosmos request units under concurrent hybrid searches. Settle Infracost at volume | Bursts (Renovate across 40 repos) and real plan sizes | M (5–8) | P1-06 |
| P2-05 | Add | **AWS and GCP**, if the company has them: Infracost's `aws` and `google` plugins pinned in the `Containerfile`; the Checkov allowlist as data per cloud (today it's five `CKV_AZURE` checks in `CheckovRunner.cs`); provider versions for `aws` and `google` from the lock file (`References.cs` matches azurerm only); their provider docs in `corpus/sources.yml` (MPL-2.0); AWS and GCP standards, Rego and eval cases; OIDC sign-in to AWS and GCP in the plan job | Everything below the agents is Azure-only today | L (15–25, mostly content) | P1-04, P1-05 |
| P2-06 | Add | **Documentation and enablement** (§3.5.9): operator guide, onboarding guide (the template's README is one paragraph), developer guide and standards authoring guide; two short demos | The owners of 40 repos can't learn it from `plan.md` | M (6–9) | Phase 1 |
| P2-07 | Improve | **Security assurance.** A pentest (§3.5.5 scope) and its fixes. App JWTs signed by a non-exportable Key Vault key instead of a PEM in worker memory. For sensitive repos, org-required workflows or approval rules on workflow changes, because a branch push equals the plan identity | An independent check before the reviewer gates merges | M (5–8), plus the pentest budget | P2-03 |
| P2-08 | Add | **SLOs and recovery.** An SLO dashboard and burn-rate alerts. A DR drill that rebuilds nonprod from code and re-indexes the corpus. Zone- or geo-redundant storage for state, if tf-review's bootstrap still hosts any | Proves the recovery targets in §3.5.6 | M (4–6) | P0-12 |

**Phase 2 total: about 50–80 person-days, or 35–55 without P2-05.**

**Exit criteria:**

- At least 90% of IaC repos are onboarded or explicitly exempted.
- Onboarding a repo takes an hour or less, and needs no change to tf-review's code or infra.
- The SLOs in §3.5.6 have held for two consecutive months.
- No pentest high or critical finding is open.
- The required check is live on the production repos that opted into blocking. An outage drill blocked no merge, and there's at most one contested block a month.
- The documentation is published, there are two or more maintainers, and the support model is written down.
- Monthly cost is within budget, and Infracost's path at volume is in place.

### 3.4 Phase 3: maturity

Ongoing; pick items by measured need.

| ID | Type | Item | Why | Effort |
| --- | --- | --- | --- | --- |
| P3-01 | Add | **Fix suggestions as GitHub suggested changes** (backlog). The plan job also emits a map from resource address to file and line range, carried with the redacted plan. Findings become review comments with suggestions | The step from a finding to a fix is where developers spend the time | L (8–12) |
| P3-02 | Add | **Terraform Cloud and HCP plans as input** (backlog), through bring-your-own-plan: fetch the run's JSON plan and submit it | Repos that plan remotely | M (3–5) |
| P3-03 | Change | **Back to release binaries** for conftest and the Infracost CLI once releases fix their findings (backlog). Self-hosted Infracost pricing (backlog), if P2-04 didn't already settle it | Source builds are maintenance the company inherits | S–M (2–6) |
| P3-04 | Improve | **Machine-readable cost thresholds.** ADR-003's $50 and 20% become config, so cost findings are deterministic and can block. It's a contract change in two repos, which is why M2 deferred it | The cost agent was the flakiest role until cost v6 (`baseline.md`) | M (4–6) |
| P3-05 | Improve | **Model routing for cost.** Evaluate gpt-5.4-mini or a smaller model for the orchestrator and re-ranker roles (D4 needs an eval to split roles). Check whether the orchestrator's "Look at first" earns its call on real PRs, which was left open after M2 | Sol costs about three times mini per input token | M (3–5) |
| P3-06 | Change | **Model lifecycle.** Replace the fallback before gpt-5.4-mini retires (2027-09-21), and the primary before gpt-6.1-sol does (2028-03-11), each through `llm-smoke`, `e2e-live` and the company suite | Pinned versions stop working at retirement | S–M each |
| P3-07 | Change | **Runtime.** Move from .NET 11 to .NET 12 LTS before .NET 11's support ends, around November 2028 (D2) | .NET 11 is a short-term (STS) release | M (3–6) |
| P3-08 | Add | **Reviewed-versus-applied check.** The apply job compares its plan's resource changes with the reviewed plan's, and flags any difference: tampering or drift between review and merge | The plan job runs PR code, so the reviewed plan can be doctored (§3.5.5) | M (4–6) |
| P3-09 | Add | **New topics and agents** as standards grow. The `topics` enum in `corpus-frontmatter.schema.json` has six values and there are three agents; identity, data protection, resilience and observability are likely next | Coverage beyond naming, tags, security and cost | M each |
| P3-10 | Improve | **The Claude Code plugin for the company:** a marketplace in the org, a pin in managed settings, wording that fits Terraform too, `python3` portability | Developer guardrails before the PR (D57) | S–M |
| P3-11 | Add | **A quarterly value report**, and a yearly build-versus-buy review | Keeps the §4.3 decision honest | S, recurring |

**Phase 3 total: about 35–65 person-days over a year.**

**Steady state:**

- Model and runtime upgrades go through the evals with no incident.
- The quarterly report shows value per dollar holding or rising.
- Dismissal rate stays at or below 10%.

### 3.5 Design notes by area

#### 3.5.1 Tenancy and identity

##### GitHub

- **New org-owned Apps, not a transfer.** Create `<org>-tf-review` and `<org>-tf-review-nonprod` rather than transferring the personal App. Its name embeds a personal handle and its key's history is personal, and the bot login changes either way. So `StickyComment.BotLogins`, `scripts/service_eval.py` and `scripts/feedback_cases.py` read it from configuration.
- **Keep the permissions `GitHubApp.OpenAsync` mints:** contents read, pull requests write, checks write, with metadata read implied. Webhooks stay off. P0-09 adds the standards repo to each token.
- **Install on selected repositories, not all.** Installation becomes the onboarding control in P2-01, and it bounds what a leaked key reaches. Org admins install; at least two App managers rotate keys.
- **OIDC subjects.** The org's OIDC subject format must match what `bootstrap/repos.tf` builds: `repo:<owner>@<owner-id>/<repo>@<repo-id>`. With an org owner, bootstrap has to look up the organization's numeric ID instead of the user's (`data.github_user.owner`).
- **Private `tf-review`.** If `tf-review` is private, allow org repos to use its workflows (Settings → Actions → Access). D6 relied on the repos being public.
- **Secrets.** Tenant and subscription IDs become org secrets, and client IDs stay per repo. Prefer explicit `secrets:` over `secrets: inherit`, which passes every org secret to the called workflow.

##### Azure and Entra ID

- **Two subscriptions, nonprod and prod.** Each gets its own Cosmos free tier (if nothing else in it uses one) and its own model quota.
- **Workload identities stay** user-assigned managed identities with federated credentials: no secrets anywhere.
- **People get Entra groups instead of `data.azurerm_client_config.current.object_id`:**
  - `tf-review-operators`, PIM-eligible: Contributor on the service resource group, Key Vault Secrets Officer on the service vault, Cosmos data reader.
  - `tf-review-readers`: Reader, Log Analytics Reader, and the workbook.
  - `tf-review-state-admins`, PIM with break-glass: Key Vault Crypto Officer on the state vault, and Blob Data Contributor on state containers. Only needed if tf-review keeps hosting state (O4).

##### Least privilege

| Identity | Today | Target |
| --- | --- | --- |
| Service (`id-tf-review-service-prod`) | OpenAI User, Key Vault Secrets User, Blob Data Contributor and Queue Data Contributor, all at resource-group scope (`bootstrap/service.tf`); Cosmos Data Contributor at account scope (`infra/apps.tf`) | Each on its own resource: the Foundry account, the service vault (or each secret), the `plans` container, the `reviews` queue. Cosmos: contributor on the `reviews` container, reader on `references`. D24 chose resource-group scope because the apply identity can't assign roles. Give that identity Role Based Access Control Administrator with a condition limiting it to these roles for this principal, or keep the assignments in bootstrap with resource IDs |
| Plan, per repo | Reader on the target resource group; Blob Data Contributor on its state container; Crypto User on its key | Blob Data Reader, once a real plan with `-lock=false` (D48) proves it writes nothing. Keep Crypto User, because the key provider may wrap keys. The plan job still runs PR code, so the rest of the risk stays (§3.5.5) |
| Index | OpenAI User at resource-group scope; Cosmos contributor on the database | OpenAI User on the account; contributor on `references` only |
| Eval (`id-tf-review-review-prod`) | OpenAI User at resource-group scope; Cosmos reader | Nonprod Foundry and Cosmos only |
| Bootstrap | The author's `az login` and a PAT | A CI identity with role-assignment rights on the scopes it manages, behind two-person approval. On GitHub, an App with administration, secrets, variables and environments on selected repos |
| People | Personal assignments (`deployer_*`) | The groups above, through PIM |

#### 3.5.2 Environments for the service

**Two environments, not three.** Nonprod serves as both dev and staging, and dev otherwise stays local (`just review`, plus `dev-image.yml` images pointed at nonprod). A third environment would idle at almost no cost, but each one needs quota, an App, a sandbox repo and upkeep, which a team without dedicated ops won't keep up.

**Nonprod:**

- Its own subscription and resource group.
- A Foundry account with small deployments: gpt-6.1-sol and gpt-5.4-mini at about 30K TPM each, which caps what a bug can spend.
- Its own Cosmos account, on that subscription's free tier.
- Its own App, installed only on a sandbox repo that carries today's seed PRs (the demo repo moves there).
- Its own `released-commits` list, holding release-candidate commits.
- Applied automatically on merge.

**Prod:**

- Applied behind team approval.
- Its `released-commits` list holds only released commits.

**Promotion:**

1. A release's image digest goes into `infra/env/nonprod.tfvars`.
2. `service-eval` runs against the nonprod seed PRs.
3. A PR copies the same digest to `infra/env/prod.tfvars`.
4. Workflow releases follow the same path: the rc tag is tried in nonprod, and the final tag is listed in prod's `released-commits`.

Nonprod costs about $0–10 a month idle.

#### 3.5.3 Onboarding at scale

**Today (the M4 gate)**, onboarding takes these steps, all through one person:

1. Add a `repos` entry.
2. Run bootstrap plan and apply, with a PAT that needs secrets access on the new repo.
3. Run `gh workflow run infra.yml` and approve `prod`, so the API's allowlist picks up the repo.
4. Install the App.
5. The new repo renames the template's resources.

Each onboarding creates per-repo Azure resources (state container, resource group, plan and apply identities, a state key) and redeploys the API. At 40 repos the person is the bottleneck, and bootstrap's state grows to several hundred resources.

**Target: two modes, one trust model.**

| | Turnkey (mode A) | Bring your own plan (mode B) |
| --- | --- | --- |
| For | New repos from the template, on Azure | Existing repos with their own pipeline, backend, runners, Terragrunt or Terraform Cloud |
| Plan job | `tf-review.yml`'s plan job, with `backend-config`, `state-key` and `runs-on` inputs | The repo's own job: `tofu show -json`, then `docker run <image> redact`, then upload the artifact |
| Review | `tf-review.yml`'s review job | `submit.yml`, the review job alone |
| State and identities | The company's IaC platform; bootstrap's per-repo resources become an optional starter | Unchanged |
| Trust | Org ownership plus App installation (P2-01) | Same |

**Onboarding after P2-01, under an hour with no tf-review change:**

1. An org admin adds the repo to the App's installation.
2. The repo owner merges a PR from the onboarding template: the caller workflow and `.tf-review.yml`.
3. The next PR is reviewed.

**Several root modules per repo (P1-03, P2-02).** Each project gets:

- its own state key from the full path;
- its own concurrency group (today `tf-review-plan-<PR>` and `tf-review-review-<PR>`, which cancels sibling reviews);
- its own artifact name (today a fixed `tf-review-plan`);
- its own marker (today one `<!-- tf-review -->` per PR);
- its own check run name and review ID.

A matrix plans only the changed projects.

**Keeping 40 repos current.** An org default for the standards commit means one bump instead of 40. Renovate or Dependabot manage the workflow SHA in each caller. Have the API record which released commit each caller used, so a commit nobody pins any more can leave `released-commits.txt`, as plan.md's "Releasing" section intends.

**Private networking.** A `runs-on` input lets self-hosted runners plan against private state, Key Vault or clusters. The API then needs to be reachable from those runners (O7).

**Other CI** stays out of scope (a non-goal in plan.md). Azure DevOps or GitLab would need a new trust adapter for their OIDC issuers, because D45 checks GitHub's claims.

**Rough edges to fix on the way:**

- The workflow downloads providers on every run. plan.md specifies a provider cache keyed on `.terraform.lock.hcl` that `tf-review.yml` doesn't have.
- The template hardcodes `kv-tf-review-state-prod` (`tf-review-demo/infra/versions.tf`).

#### 3.5.4 Data governance

##### What leaves the repo, and where it goes

| Data | Leaves to | Where | Kept | Change |
| --- | --- | --- | --- | --- |
| Raw plan JSON (may hold secrets) | Nowhere: deleted in the plan job (`tf-review.yml`, Redact step) | Runner | Minutes | None |
| Human-readable plan text | Actions job log | GitHub | Org log retention; private repos default to 90 days | Set retention, for example 30 days; optionally print only the summary line (a `print-plan` input) |
| Redacted plan | Actions artifact, then the API, then Blob | GitHub; Germany West Central | 1 day; 7 days (lifecycle rule) | None |
| Condensed slices, standards text, policy findings, cost figures | Azure OpenAI, Data Zone Standard EU (D4) | EU data zone | Not stored by the service. Microsoft's abuse monitoring may keep prompts for up to 30 days unless modified abuse monitoring is approved; verify against current terms | Decide the abuse-monitoring position (O5) |
| Pricing queries and telemetry (command, flags, install ID, org, repo URL, branch) | Infracost | The vendor | The vendor's terms | Self-host, DPA, or cost off (O6) |
| Embedding queries (finding title, resource type, attribute name) | Azure OpenAI | EU data zone | Not stored | None (off if references are off) |
| Cached answers (can quote the plan) | Cosmos `reviews` | Germany West Central | 7 days (D53) | None |
| Review statuses (repo, PR, commit, verdict) | Cosmos `reviews` | Germany West Central | Forever (`default_ttl = -1`, no per-item TTL) | TTL of about 13 months |
| Findings history (new, P1-06) | Cosmos | Germany West Central | – | About 13 months; no plan values beyond a hashed address |
| Metrics and console logs (names and counts by design) | Log Analytics | Germany West Central | 30 days | 90 days for trends |
| Findings in the PR comment (quote names and attributes) | GitHub | GitHub | The PR's lifetime | None; redaction applies first |

##### What the model sees

- Redacted, condensed `resource_changes` for the resources routed to it: names, types, actions, attribute values, and tags, which often hold an owner's name or e-mail.
- The standards text, policy findings and cost figures.
- It never sees the HCL. The redactor keeps only `format_version`, `resource_changes` and `output_changes`.
- It never sees state.

##### Residency

- Today everything sits in Germany West Central, and model processing stays within the EU data zone (D4, D8).
- A company needing single-region processing would use regional Standard deployments where the model offers them.
- A company outside the EU picks its own region and zone. The region becomes a variable in P0-06.

##### Contracts

- Moving to the company's tenant brings Azure under the company's own Microsoft agreement and DPA. The personal pay-as-you-go subscription was under the author's.
- GitHub is already a processor of the code.
- Infracost needs its own DPA, or no plan data. The plan's risk table already says client repos need a decision.

##### Secrets in plans, layer by layer

1. **Marked values.** Provider and module sensitivity marks (D25). Repos keep marking variables `sensitive`.
2. **Names.** The attribute-name denylist in `PlanRedactor.SecretNames`, extended per company through configuration (P0-08).
3. **Patterns and entropy (new, P0-08).** Known formats, plus an entropy check for long opaque strings under unknown names, either redacted or refused.
4. **Sweep and verifier.** Harvested values removed everywhere, and the plan refused if one survives.
5. **Output.** Comments render only code-controlled fields and escaped text (plan.md, "Rules every agent run follows").
6. **Response.** Runbook RB8 covers a leak: rotate the secret first. Then delete the comment, or the affected revisions of its edit history: the worker rewrites the sticky comment on every push, and GitHub keeps each earlier version visible to anyone who can read the repo. Purge the logs if needed.

##### What else could be redacted, and what that costs

A per-repo `redaction` profile could add these:

- Hash owner e-mails in tags. Costs nothing, since ADR-002 checks that the tag exists, not its value.
- Replace IP ranges with a category: public, private or any. conftest runs on the redacted plan too (`ReviewRunner`), so POL-002-style Rego would have to match categories instead of ranges, and the security agent loses the exact range. Worth it only where the network layout itself is sensitive.
- Hash resource names. That ends naming checks (ADR-001) and makes comments harder to read.

Default to the first only.

#### 3.5.5 Security: threat model, pentest scope and supply chain

**Threat model summary.** P0-10 writes the full document, with a data-flow diagram and STRIDE per element.

| Entry point or asset | Threat | Controls today | Gap and action |
| --- | --- | --- | --- |
| Review API (public ingress) | Forged or replayed submissions; spending the budget | D45: audience, `event_name`, `environment: review`, allowlisted `repository_id`, `job_workflow_ref` at a released commit, PR number from `ref`. 16 MB body limit, 60 reviews an hour per repo, idempotent review IDs | Trusted workflow hardcoded to a person (P0-02); no global daily cap (P0-13); the rate limiter is in memory per replica |
| Plan job (runs PR code with the plan identity) | PR code reads state, unwraps the state key, can overwrite state, and can doctor the redacted plan the reviewer sees | D21 keeps LLM credentials out; fork PRs skipped; D48 takes no lock | Plan identity to read-only state (§3.5.1). **State plainly: blocking (D55) guards against mistakes, not against someone with push access who means harm.** Required workflows or approval on workflow changes for sensitive repos (P2-07); reviewed-versus-applied check (P3-08) |
| Plan values sent to the model | Secrets or personal data disclosed | D25 redaction; only `CondensedPlan` reaches agents | Patterns and entropy (P0-08); DPIA (P0-07) |
| Plan text as instructions | Prompt injection hides or plants a finding | Fenced, escaped user message; agents have no tools; code sets severity; evidence check; the arbiter can't drop findings (D36); C16 case; never the sole gate (D9) | A model finding can still be suppressed. Keep high rules deterministic (§3.5.7) |
| Standards repo and its Rego | Code execution in the worker; reaching the managed identity | D47: conftest without `http.send`, `net.lookup_ip_addr`, `opa.runtime` or file readers; sealed environment | Any public repo can be named today (P0-09 limits it to the org) |
| Scanners and closed-source Infracost plugins | A compromised or vulnerable dependency | Checksums, the Grype gate (D28), the Trivy precedent (D10), source builds with patched modules | Plugins are closed and unsigned and hold the Infracost token; the known-exploited brotli exception (P0-11); self-hosted pricing (P2-04 or P3-03) |
| GitHub App key | Read every installed repo's contents; post or alter comments and checks | Key Vault; one-repo tokens with three permissions, revoked after use; key read into memory, never into the environment (D47) | Signing in Key Vault (P2-07); vault-scoped Secrets User and purge protection (P0-10); Key Vault audit logs; selected-repo installation |
| PR comment | Markdown, link or mention injection; forged dismissal markers | Code spans for untrusted text; URLs, mentions and the marker stripped; markers hold only hashes (D56); only two bot logins count | Bot login as configuration (P0-02) |
| Result cache | Leakage or poisoning across repos | Partitioned by repo ID; key is a SHA-256 of the whole input; 7-day TTL (D53) | None |
| Images and releases | Tampered image | Digest pins; cosign verification of the signing workflow's identity before PR code runs | Provenance (P0-11); private-repo signing (O2) |
| People and laptops | Over-privileged personal roles; secrets in `pass` | – | Groups with PIM, and CI bootstrap (P0-04, P0-05) |
| Claude Code plugin hooks | Developers bypass the apply guard | Pinned tag (D57); deny list in the template | A convenience, not a control: say so in the guide |

##### Pentest scope (P2-07)

In scope:

- **The API's authentication:** forged and expired tokens; other events, environments and workflows; fork commits; replay within a token's lifetime; claim confusion between repos.
- **Request handling:** size, rate and JSON parsing.
- **Worker inputs:** plan JSON crafted against the redactor, the condenser and each scanner.
- **The Rego sandbox:** a hostile standards repo trying to reach the network, the environment, or the managed identity's endpoint.
- **Prompt injection:** content that tries to produce harmful comment content or suppress a finding (measured as a rate, not pass or fail).
- **Tokens and isolation:** App token scope across repos; Cosmos partition isolation.
- **Platform configuration:** Container Apps ingress and identity; Key Vault access paths.
- **CI/CD:** the release workflow; environment protections; the federated credentials' subjects.

Out of scope: Azure and GitHub themselves.

##### Supply chain

Keep what's there:

- Digest-pinned bases.
- hadolint and Syft.
- The Grype gate with one-file exceptions and review dates.
- cosign keyless signing with an SBOM attestation.
- Consumers verifying the signer.
- Actions pinned by SHA.

Add:

1. **Build provenance in `release.yml`.** For example `actions/attest-build-provenance`, which yields SLSA v1 provenance at Build L2, or L3 run from an isolated reusable workflow. Verify it in `tf-review.yml` alongside the signature.
2. **NuGet lock files** (`RestorePackagesWithLockFile`), restored in locked mode in CI and the `Containerfile`.
3. **Renovate or Dependabot** for digests, actions and NuGet, replacing the author's machine-level `update-sources` checks.
4. **A signing decision for a private repo.** Public-good keyless signing would publish the private repo's name and workflow in Rekor, a public transparency log. Choose between GitHub's attestations for private repos (an Enterprise Cloud feature), a KMS key in Key Vault, or accepting the disclosure.
5. **A calendar of exception reviews:** brotli and compress by 2026-12-06, go-jose by 2027-02-01 (plan.md, Accounts).

#### 3.5.6 Reliability and operations

**Service level objectives** (measured over 28 days; P1-06 adds the metrics they need)

| SLI | Definition | Objective |
| --- | --- | --- |
| Answered | Pushes with a plan job that ran, whose head commit gets a tf-review comment and check on the PR | 95% within 15 minutes, 99% within 60 |
| Reviewed | Answered reviews with no "not reviewed" scope caused by the service: `rate_limited`, `unavailable`, `timeout`, `scanner_failed`, `standards_unavailable` (but not `redaction_refused`, `invalid_config` or `plan_too_large`) | 95% |
| Latency | From submit to posted | p50 4 minutes or less, p90 10 minutes or less |

**Error budget policy:** when a budget is spent, prompt and model changes wait until reliability work restores it.

**Alerts (P0-12).** Log alerts on the workspace, using KQL like the dashboard's tested tiles (D58), routed to an action group that posts to the team's channel and mails a list:

| ID | Fires when |
| --- | --- |
| A1 | Review job executions fail (`ContainerAppSystemLogs_CL`, reasons `BackoffLimitExceeded`, `DeadlineExceeded`, `Failed`) |
| A2 | `tfreview.not_reviewed` with a service-caused reason exceeds 10% of reviews in an hour, or 3 in a row |
| A3 | Reviews have been queued or running for over 30 minutes (from the watchdog, P1-08) |
| A4 | Model calls finishing other than `stop` or `cached` exceed a threshold (`tfreview.llm.latency` by `finish`) |
| A5 | Log Analytics reaches its daily cap |
| A6 | Daily `tfreview.llm.cost` is over three times its trailing average, or over a fixed cap; budget forecast at 100% |
| A7 | API 5xx responses, or a burst of 401/403 refusals (often a caller pinned to a commit that isn't released) |
| A8 | A Key Vault secret is read by an unexpected identity (needs diagnostic logs) |
| A9 | The daily canary review gets no result within 20 minutes |
| A10 | A dated item comes due: Grype exception reviews, model retirements, the App key's age, the Infracost token |

**Runbooks.** RB1–RB6 in Phase 0, the rest by Phase 2:

- **RB1:** reviews not posting, including how to bypass a required check.
- **RB2:** a 429 storm or exhausted quota, and switching to the fallback deployment.
- **RB3:** a model upgrade or retirement.
- **RB4:** rotating the App key or Infracost token, and responding to a suspected key leak.
- **RB5:** onboarding and offboarding a repo.
- **RB6:** releasing and rolling back the image, the workflow or the plugin (plan.md, "Releasing").
- **RB7:** rebuilding an environment, and the corpus (`corpus.yml` with `rebuild`).
- **RB8:** a secret leaked into a comment or a log.
- **RB9:** state key recovery: soft-deleted keys, purge protection (D59).
- **RB10:** a release stopped at the Grype gate (D28).

**Support model: no on-call, by design.**

The reviewer is advisory, failures become "not reviewed" (D39), and from P1-08 a neutral check. Blocking applies only to deterministic findings, never to "not reviewed" (D55). So an outage costs reviews, not merges, and the runbook covers required checks.

Business-hours targets: acknowledge within 4 business hours, and fix or degrade within one business day. A weekly "reviewer of the week" in the platform team watches the alerts and the dismissals queue.

##### Backup and DR

| Component | Holds | Recovery point | Recovery time | How |
| --- | --- | --- | --- | --- |
| API and review job | Nothing | – | Under an hour | `infra.yml` apply |
| Queue and plans | In-flight reviews | In-flight work is lost | Under an hour | Re-run PR workflows; the watchdog answers "not reviewed" |
| Cosmos `reviews` (statuses, cache, history) | Metadata | The last backup | Hours | Turn on continuous backup (check the 7-day tier's current price) for history; the cache needs none |
| Cosmos `references` | The index | – | Under an hour | Rebuild from pins (D43, D44), about 7 cents to embed |
| Service vault | App key, Infracost token | – | Under an hour | Generate a new App key in GitHub; reissue the token |
| Foundry | Deployments | – | Under an hour if quota exists | Apply; quota in another region is the long pole |
| State (if tf-review still hosts it) | Every repo's state | Versions; 30-day soft delete | Hours | Restore a version or a soft-deleted blob; move LRS to ZRS or GZRS (P2-08) |
| State vault keys | Wrapping keys | – | – | Never delete; purge protection with 90-day soft delete (D59) |

A whole-region loss stops reviews until the environment is rebuilt in another EU region (RB7), which is acceptable for an advisory reviewer. Test it once a year with the P2-08 drill.

##### Model fallback and quota

Today one deployment name comes from `TF_REVIEW_OPENAI_DEPLOYMENT`. gpt-5.4-mini is deployed at 50K TPM but isn't wired in, so switching takes an infra change and an approval.

P1-08 adds a fallback deployment in code, used per call after the SDK's retries on `rate_limited` or `unavailable`. Results are already tagged by model in the metrics. Mini passed 155 of 155 on the evals, with fewer suggestion concepts (`baseline.md`), which is acceptable degraded output.

Quota is per subscription, region and model, so:

- Size `maxExecutions` × per-review budget to stay under capacity (P0-13), then share one budget across replicas (P2-04).
- Watch the `rate_limited` reason (A2).
- Ask for tier increases early.
- For Azure's content filter (`content_filter` reason), a custom filter policy on the deployment is the remedy if security text trips it.

#### 3.5.7 Quality, standards and value

##### An eval suite for the company's standards (P1-05)

- Keep the method in `baseline.md`: five runs a case, a whole-set match, and held-out cases fixed before any model sees them.
- Change the content:
  - each accepted doc gets a positive, a negative and a near-miss case;
  - cases come from real pilot plans, through `just fixtures`' GUID zeroing;
  - the suite lives in a private repo.
- In a private fork, D56's rule that only the demo becomes cases can go: it existed because fixtures were public. So `feedback-cases` can draft from any onboarded repo.
- Run the suite on every standards PR as well as on prompt and agent PRs.
- Run it weekly in full, and record the trend.

##### Writing ADRs and policies (P1-04)

1. **Propose.** A PR in the standards repo from the template, with `status: proposed`. Proposed docs aren't retrieved.
2. **Encode.** If the rule is deterministic, write Rego with `conftest verify` tests (`just policy-test`). If it needs judgement, write crisp criteria. ADR-003's prose thresholds made the cost agent the flakiest role until cost v6 (`baseline.md`).
3. **Measure.** Add the eval cases, and run the suite against the proposed commit in nonprod.
4. **Accept.** Code owners approve (platform for ADRs, security for policies), the doc moves to `status: accepted`, and Renovate bumps the pins.
5. **Watch.** Track the rule's dismissal rate for four weeks.
6. **Retire.** Set `status: superseded` and `superseded_by`; never delete (tf-standards' CLAUDE.md; plan.md, "Corpus").

Severity rubric: high only for rules a policy reports deterministically, which is what makes them able to block (D55). Model-judged rules are medium or low.

##### False positives

- **Precision per rule** is 1 minus dismissed over shown, from D56's checkboxes and P1-06's history.
- A rule over 20% dismissed with 10 or more findings gets triaged that week. The fixes:
  - a prompt or Rego change, measured by the evals;
  - a suppression with an expiry (P1-07);
  - the rule switched off org-wide;
  - a negative eval case, so it can't come back.
- The existing defences stay: the evidence check, the `breaks_standard` field, the arbiter's limits (D36), and near-miss cases (C9, C13, C14, C18).

##### Feedback loops

- **Weekly:** dismissals, "not reviewed", and the cost per review.
- **Monthly:** a two-line report to the owner, with precision, addressed findings and cost.
- **Quarterly:** the value report (P3-11).

##### Measuring value (P1-06)

- **Issues caught:** findings addressed before merge (present on one push, gone on a later push while the resource still changes), weighted by severity. Split them by `agent` (`scanner` against the model agents) to see what the model layer adds over conftest and Checkov alone. That number decides §4.3.
- **Blocks:** blocking events, and what they prevented.
- **Precision:** the dismissal rate.
- **Time:** time to first feedback, minutes against the hours of a human first review, from PR timestamps. Also review rounds and time to merge in pilot repos against control repos. Treat these as indicators, not proof, because pilot repos self-select.
- **Opinion:** a developer survey at mid-pilot and at the end, then twice a year.
- **Cost per addressed finding**, from §5.

#### 3.5.8 Product gaps

| Gap | Today | Proposal | Phase |
| --- | --- | --- | --- |
| Several projects per PR | Collisions in concurrency, artifact, comment, check and state key | P1-03, P2-02 | 1–2 |
| Existing pipelines and other state backends | azurerm `-backend-config` and GitHub-hosted runners hardwired in `tf-review.yml` | Bring-your-own-plan and new inputs (P1-02) | 1 |
| Terragrunt | None | Bring-your-own-plan per unit (P2-02) | 2 |
| Terraform | `tool: terraform` works (D1). The template and plugin are OpenTofu-only: the state-encryption block is OpenTofu syntax, and the skill says to use `tofu` | Pilot a Terraform repo; a Terraform variant of the template; plugin wording (P3-10) | 1–3 |
| Terraform Cloud and HCP plans | Backlog | An adapter over bring-your-own-plan (P3-02) | 3 |
| Module repos | No state, so no plan | Review `examples/` plans in a sandbox subscription, or leave out of scope; version bumps are reviewed in consumers' plans | 2 (guide) |
| AWS and GCP | Azure-only scanners, corpus, pricing and standards | P2-05 | 2 |
| Required check | Missing on the outage path (§2.2) | P1-08, P2-03 | 1–2 |
| Fix suggestions as GitHub suggested changes | Backlog | P3-01 | 3 |
| Noise controls | `comment_min_severity` ignored; no suppressions | P1-07 | 1 |
| Org defaults and pin bumps | Per repo, by hand | P2-01 | 2 |
| Other CI | Non-goal | Only through a new trust adapter; out of scope | – |
| GitHub Enterprise Server or GHE.com | `api.github.com`, `codeload.github.com` and the OIDC issuer are hardcoded | Configurable hosts, only if the company uses them | 2 |
| Merge queues | The API accepts only `pull_request` events (D45) | Nothing, unless the check must pass inside the queue; then `merge_group` support | 2 |
| Backlog housekeeping | The template-names item is done (demo #52, one `locals` block), and plan.md doesn't tick it | Tick it | 0 |

**Developer experience, beyond the gaps above:**

- A comment arrives in about 1–5 minutes: 42 seconds on demo #32, about 3 minutes for 24 NSGs on demo #45.
- Collapse the comment when there are no findings.
- Link each finding to the developer guide.
- Document `just review` for local runs.
- Advertise the plugin's skill, which cites the same pinned standards (D57).

#### 3.5.9 Ownership, documentation, licensing and technology currency

##### Who runs it

| Activity | Platform team | Security | Repo owners | Engineering management or FinOps |
| --- | --- | --- | --- | --- |
| Run the service, releases, upgrades | Responsible, accountable | Consulted | Informed | Informed |
| ADRs | Responsible, accountable | Consulted | Consulted | – |
| Policies and Rego | Consulted | Responsible, accountable | Consulted | – |
| Onboarding a repo | Accountable | – | Responsible | – |
| Dismissal triage, prompt changes | Responsible, accountable | Consulted | Consulted | – |
| DPIA and retention | Responsible | Accountable, with the DPO | – | – |
| Budget | Responsible | – | – | Accountable |

**People:** a named product owner and at least two maintainers, so the bus factor is two or more. Steady state takes 2–4 person-days a month.

##### Documentation (P2-06)

- **Operator guide:** architecture, the decisions as ADRs drawn from plan.md, deploy, config, secrets, release and upgrade.
- **Runbooks:** RB1–RB10.
- **Onboarding guide**, replacing the template's one-paragraph README.
- **Developer guide:** reading a comment, dismissing, suppressing, what blocking means, and that "No findings" isn't approval.
- **Standards authoring guide.**
- **Security pack:** the threat model and data-flow diagram, the DPIA, the retention schedule.
- **A cost and capacity note.**

##### Licensing

- **tf-review** is Apache-2.0 (D14), so the company can fork it and use it internally. Keep the licence and notices.
- **IP:** if the author is or was an employee, settle the IP in writing; otherwise consume the release under the licence.
- **Dependencies:**
  - OpenTofu is MPL-2.0.
  - Terraform is BUSL-1.1; internal use is fine.
  - Checkov, conftest and OPA are Apache-2.0.
  - .NET and Agent Framework are MIT.
  - Infracost's plugins are closed, under Infracost's terms.
- **Corpus:**
  - Azure Policy is MIT.
  - The Cloud Adoption Framework is CC-BY-4.0, attributed in comments (D41).
  - azurerm, AWS and Google provider docs are MPL-2.0.
  - CIS stays out (D41).
- **Model output** falls under Microsoft's product terms.

##### Technology currency

| Dependency | State | Action |
| --- | --- | --- |
| .NET 11 SDK and runtime | RC3 until GA on 2026-11-10; STS, so support ends around November 2028 (D2) | Re-pin (P0-11); .NET 12 LTS in 2028 (P3-07) |
| C# 15 (`union`, `closed`) | New: csharp-ls reports false errors, and CSharpier can't parse it (plan.md) | Trust `dotnet build`; `dotnet format` is the only formatter |
| Agent Framework 1.27, Microsoft.Extensions.AI 10.11, OpenAI SDK 2.14 | No `net11.0` builds yet; an experimental constructor (`OPENAI001`) | Pin; run `llm-smoke` on every upgrade |
| azapi 2.13 | The provider is GA, and its API versions (`2025-07-01`, `2025-10-15`) aren't previews. It's a workaround for azurerm reading secrets (D46) | Track API-version deprecations; recheck D46 when azurerm changes |
| gpt-6.1-sol `2026-09-29` | Data Zone; retires 2028-03-11; newer than Microsoft's supported list for structured output (plan.md) | Smoke test and evals on every change; the fallback |
| gpt-5.4-mini `2026-03-17` | Retires 2027-09-21 | A successor through the evals (P3-06) |
| Infracost CLI 2.18.3 (built from source), plugins 0.0.37 and 0.0.27 | Closed and unsigned, with Grype exceptions | P0-11, P3-03 |
| conftest 0.71.1 (built from source, OPA 1.21.1) | Patched Go and modules | P3-03 |

---

## 4. Risks, open decisions, and build versus buy

### 4.1 Open decisions for the company

| ID | Decision | Options | Recommendation | Needed by |
| --- | --- | --- | --- | --- |
| O1 | Fork and own, or work with the author | Fork into the org; contribute upstream; buy | Fork under Apache-2.0, send general fixes upstream, settle IP | P0-01 |
| O2 | Visibility of the repos and the image | Public; internal repos with a public image; internal repos with a private image in Azure Container Registry | Internal repos. A private image in Container Registry if prompts or standards become company-specific, otherwise the public image | P0-01 |
| O3 | GitHub plan | Team; Enterprise Cloud | Enterprise Cloud if private repos need required reviewers on environments, org-required workflows, or attestations for private repos. Confirm against GitHub's current plan matrix | P0-01 |
| O4 | Is tf-review also the IaC platform (state, identities, keys)? | Keep bootstrap per repo; reviewer-only | Reviewer-only, with bootstrap's platform part kept as an optional starter | P0-05 |
| O5 | Data residency and abuse monitoring | EU Data Zone (today); regional Standard; Global | Keep the EU Data Zone unless policy needs single-region processing. Apply for modified abuse monitoring if no retention is acceptable | P0-07 |
| O6 | Infracost | Self-hosted pricing; paid with a DPA; accept the telemetry; cost agent off | Pilot with cost off or under an accepted DPA. Self-host or pay before about 500 reviews a month (§5.3) | P0-07 |
| O7 | Network exposure | Public ingress with D45's trust; private (VNet, internal ingress, self-hosted runners) | Public with D45, unless the landing zone forbids it. Private adds about $73 a month for the Container Apps environment's private endpoint (plan.md's risk table), about $7 a month per other endpoint, and the runners | P0-04 |
| O8 | References (RAG over imported docs) | On (the demo today); off | Off by default until company evals show a gain. A team's own standards don't depend on it: reviews read them at their pinned commit, not from the index (D44) | P1-10 |
| O9 | What may block, and where the check is required | Advisory everywhere; blocking on deterministic highs; required checks | Advisory in the pilot; blocking and required only after P1-08 | P2-03 |
| O10 | Suppressions | Base-commit config only (D27); inline in HCL from the PR head with code-owner approval | Base-commit config with expiry; revisit after the pilot | P1-07 |
| O11 | Trust model | Allowlist (D40); org plus installation | Org plus installation (P2-01) | P2-01 |
| O12 | Model mix | gpt-6.1-sol for every role; mini for some roles | Sol for the agents; evaluate mini for the orchestrator and re-ranker (P3-05) | Phase 3 |
| O13 | Support model | None; business hours; on-call | Business hours, no on-call (§3.5.6) | P0-12 |
| O14 | AWS and GCP scope | Azure only; add AWS and GCP | Only if AWS or GCP repos are where the pilot shows value | P2-05 |
| O15 | Build versus buy | Continue; deterministic only; buy | Decide at Phase 1's exit, by §4.3 | End of Phase 1 |

### 4.2 Risks

| ID | Risk | Likelihood | Impact | Mitigation |
| --- | --- | --- | --- | --- |
| R1 | All knowledge sits with one maintainer, the author | High | High | Two maintainers trained in Phase 1; docs (P2-06); runbooks |
| R2 | The model layer adds little beyond conftest and Checkov | Medium | High | Measure by `agent` (P1-06); stop criteria (§4.3) |
| R3 | Noise erodes trust, and people stop reading comments | Medium | High | Advisory by default; dismissals; suppressions; a per-rule switch; weekly triage |
| R4 | An unmarked secret or personal data reaches Azure OpenAI or a comment | Medium | High | P0-08; code-rendered comments; DPIA; RB8 |
| R5 | A prompt injection in plan values hides a model finding | Medium | Medium | Deterministic backstop; C16; never the sole gate (D9) |
| R6 | Someone with push access doctors the reviewed plan, or the state, from the plan job | Low to medium | High | Stated as a limit; human review; required workflows; read-only state; P3-08 |
| R7 | The model changes under the service: retirement, quota tiers, regional capacity, structured output on new models | Medium | Medium | Fallback in code; eval-gated upgrades; dates in A10 |
| R8 | Infracost: terms, telemetry, the free-tier cap, closed and unsigned plugins, one with a known-exploited finding | High | Medium | P0-07; self-host or cost off; exception dates |
| R9 | Landing-zone policy (no public endpoints, customer-managed keys, regions) forces a different network design | Medium | Medium to high | Check in P0-04; budget per O7 |
| R10 | The GitHub plan limits environments, required workflows or attestations in private repos | Medium | Medium | O3 |
| R11 | Runtime churn: .NET STS, preview-era AI libraries, C# 15 tooling | Medium | Low to medium | Pins, `llm-smoke`, .NET 12 LTS (P3-07) |
| R12 | Cost overrun from large plans or bot PR storms | Low to medium | Medium | D54's caps, pacing, a daily breaker, a bot policy, budgets |
| R13 | A required check blocks merges during an outage | Medium if it's required before P1-08 | High | Don't require it before the watchdog; RB1's bypass |
| R14 | Standards pins drift apart across 40 repos | High | Low to medium | An org default and Renovate (P2-01) |

### 4.3 Build versus buy, and when to stop

| Option | Examples (check current features and prices) | Better than tf-review at | Worse than tf-review at |
| --- | --- | --- | --- |
| Deterministic checks only, open source | conftest with your Rego (what tf-standards already is), Checkov, KICS, TFLint | Cost, predictability, no data leaving CI, no model risk | No explanation in the team's own terms; can't judge rules that need judgement; every rule must be coded |
| PR automation for IaC | Atlantis, Digger (open source); Spacelift, env0, Scalr, HCP Terraform (commercial; HCP Terraform runs Terraform, not OpenTofu) | A mature plan and apply workflow, policy as code, drift detection, approvals, support | Not a reviewer, and their policy engines are deterministic. tf-review can sit alongside through bring-your-own-plan |
| Cost tools | Infracost Cloud | Cost and FinOps policies in PRs | Cost only. tf-review already uses Infracost's CLI |
| Cloud security platforms | Wiz, Prisma Cloud, Snyk IaC and similar | Thousands of rules, compliance mapping, runtime context, support | Generic rules rather than the company's ADRs; expensive; data held by the vendor |
| General AI code review | GitHub Copilot code review, CodeRabbit and similar | Adopted in minutes; reviews any code; custom instructions | Reads HCL diffs, not plans, so no computed values, module expansion or forced replacements; no evidence check; not safe to gate on |
| Plan-aware AI risk analysis | Overmind and similar | Closest in concept, with live infrastructure context | A commercial SaaS; generic risk rather than your standards; residency to check |

**Where tf-review is the better choice:**

- Reviews of the actual plan.
- Grounded in the company's own ADRs and policies, with an evidence check.
- Blocking only on deterministic findings.
- EU processing in the company's own tenant.
- A run cost in the tens to low hundreds of dollars a month.

**Where buying is better:** nobody can own it, the company needs broad compliance coverage and multi-cloud breadth more than its own standards, or it needs a vendor's SLA.

**Stop, or fall back to deterministic-only, if any of these holds at the end of Phase 1 (and at each yearly review):**

1. **Value:** fewer than about 3 addressed findings per 100 reviews that conftest and Checkov alone wouldn't have produced.
2. **Noise:** the dismissal rate stays above 25% after a month of tuning.
3. **People:** no team can staff two maintainers and 2–4 person-days a month.
4. **Compliance:** the DPIA won't approve sending plan data to Azure OpenAI.
5. **Price:** a quote for an equivalent tool undercuts tf-review's cost of ownership, people included.
6. **Market:** a vendor ships plan-aware review grounded in your own standards, with EU data handling.

**Stopping isn't all-or-nothing.** The deterministic core survives: tf-standards' Rego and tests under conftest (`just policy-test`) and Checkov's allowlisted checks run in any CI. A middle path is that core in every repo, plus the model reviewer only where teams value it.

---

## 5. Cost projection

### 5.1 The cost of one review

**Prices** (`src/TfReview.Core/ModelPrices.cs`; Data Zone list prices in Germany West Central on 2026-11-05):

| Model | Input | Cached input | Output | Cache writes |
| --- | --- | --- | --- | --- |
| gpt-6.1-sol | $2.40 | $0.12 | $12.00 | $3.00, which `ModelPrices` leaves out (`baseline.md`), so the dashboard's cost tile reads low |
| gpt-5.4-mini | $0.825 | $0.0825 | $4.95 | – |

All figures are per million tokens.

**Measured.** A full eval run is 155 reviews of plans with one to three resources, using about 3K input tokens a review (484K in total). Sol's three runs cost $0.30–0.37 at list prices, or $0.39–0.54 with cache writes: $0.002–0.004 a review. That's a floor, not a forecast, because company plans and standards are bigger.

**Estimated for company-sized reviews:**

| Size | Shape | Input tokens | Output tokens | Cost on sol (list, uncached) | Share assumed |
| --- | --- | --- | --- | --- | --- |
| Small | 1–5 changes, 1–2 agents | 10–15K | about 0.5K | $0.03–0.05 | 55% |
| Typical | 6–20 changes, 3 agents | 30–40K | about 1.5K | $0.09–0.12 | 40% |
| Large | Batched (D54), at most 4 batches an agent | Up to about 240K | about 6K | Up to about $0.65 ($0.80 with cache writes) | 5% |

The estimate assumes:

- company standards fill 4–8K of each agent's 8K standards cap;
- about 300–500 tokens per condensed resource change;
- about 1K tokens per agent prompt;
- references off.

**Blended: about $0.08–0.12 a review; plan on $0.10.** Some factors push it up or down:

- `references: true` multiplies it by about 1.3–1.7.
- Prompt caching lowers input cost when reviews cluster.
- The result cache (D53) removes repeat calls on pushes that don't change a role's input.
- A PR has about two billed reviews, so about **$0.20 a PR**.

**Recompute from the pilot's own figures:**

> monthly model cost ≈ PRs × billed reviews per PR × blended cost per review

P0-12 adds `repository` to the cost metric, so this can be read per repo.

### 5.2 Monthly projection

The PR counts are assumptions for IaC repos: a platform team's daily work, occasional app-team changes, and Renovate provider bumps.

| | Small: 20 engineers, 10 repos | Medium: 50 engineers, 25 repos | Large: 80 engineers, 40 repos |
| --- | --- | --- | --- |
| PRs a month | about 150 | about 450 | about 1,000 |
| Billed reviews (about 2 a PR) | about 300 | about 900 | about 2,000 |
| Model tokens at $0.04–0.15 a review | $12–45 | $36–135 | $80–300 |
| Container Apps (free grant: 180K vCPU-seconds and 360K GiB-seconds a month per subscription) | $0 | $0–3 | $3–10 |
| Cosmos (free tier in each dedicated subscription) | $0 ($25–60 without a free tier) | $0 | $0, unless RU/s must grow |
| Storage, queue, Key Vault | Under $2 | Under $3 | Under $5 |
| Log Analytics and App Insights, with a cap of about 1 GB a day | $0–5 | $2–8 | $5–15 |
| Log alert rules (about 10, at about $1.50 each at 15-minute frequency) | about $15 | about $15 | about $15 |
| Nonprod environment, mostly idle | $0–10 | $0–10 | $0–10 |
| Azure Container Registry Basic, if the image is private (O2) | $0–5 | $0–5 | $0–5 |
| Infracost: pricing runs at 2 a review | About 600, within the reported free tier | About 1,800: over it, so self-hosted pricing ($15–30 plus upkeep) or a paid plan | About 4,000: same |
| GitHub Actions, extra minutes (the review job plus image pulls, about 2–3 minutes a review; most repos already plan on PRs) | About 800 minutes, usually within the plan | About 2,500 | About 5,500: $0–40 beyond the allowance |
| **Total, excluding people** | **about $30–80** | **about $55–230** | **about $100–430** |
| **Planning figure** (at $0.10 a review and middle platform costs) | **about $55** | **about $145** | **about $280** |

With references on, add 30–70% to the model line.

These totals replace D15's €30 budget. Budget about 1.5 times the planning figure: about €80, €200 and €400, more with references on.

### 5.3 When the free tiers and limits break

| Free tier or limit | Breaks when | Then | Action |
| --- | --- | --- | --- |
| Cosmos free tier: one account per subscription, 1,000 RU/s, 25 GB (D13) | The company's subscription already uses its free account; or concurrent hybrid searches overrun the `references` container's 600 RU/s | $25–60 a month, or 429s and slower reviews | Dedicated subscriptions (P0-04); watch `TotalRequestUnits` (D58); with references off, the 600 RU/s isn't needed |
| Infracost's free tier: reportedly about 1,000 pricing runs a month | About 500 reviews a month, at two scans each (D31) | The cost scope reports "not reviewed", or a bill | Self-hosted pricing (backlog), a paid plan, or the cost agent on only where it's wanted (O6) |
| Container Apps' free grant (per subscription) | Shared with the company's other apps, or above about 1,500 two-minute reviews | A few dollars | None needed |
| Log Analytics' 0.2 GB a day cap | More reviews and alerts. Data is dropped silently, which blinds the dashboard and the alerts | Missed incidents | About 1 GB a day, with alert A5 (P0-12) |
| GitHub Free and public repos (D6) | The repos go private | Minutes are billed, and environment reviewers in private repos need Enterprise | O3 |
| GHCR public package (D17) | The image goes private | Pulls need credentials in the workflow and in Container Apps | Container Registry with managed-identity pull, or keep the image public (O2) |
| Azure OpenAI Tier 1, 100K TPM for sol | Three concurrent large reviews (3 × 90K pacing) | `rate_limited` partial reviews | P0-13, P2-04, quota requests |
| The €30 budget (D15) | Any company volume | Alerts from week one | §5.4 |

### 5.4 Budgets and guardrails

- **Azure budgets.** Per subscription (prod and nonprod), at about 1.5 times the planning figure. Thresholds at actual 80% and 100%, and forecast 100%, to an action group (a list and the channel), not one person's e-mail.
- **Rate is not budget.** TPM capacity limits the rate, not the spend: at 100K TPM a runaway loop could spend about $15–70 an hour (`infra/main.tf`).
- **A daily breaker in code.** Above a daily estimated spend, the model roles return "not reviewed" with a budget reason, and deterministic findings still post. Azure budgets alert; they never stop anything.
- **Rate limits.** The per-repo limit of 60 reviews an hour (`ApiOptions.ReviewsPerHour`) becomes configuration. Bot PRs (Renovate) get conventions only, or are skipped when the plan has no changes.
- **Monthly reconciliation.** Compare `tfreview.llm.cost` (list prices, no cache writes) with the invoice, and adjust `ModelPrices`.
- **Cost allocation.** Tag resources with `owner` set to the team, plus `project` and `environment`, as ADR-002 already asks.

### 5.5 People

People are the main cost:

- **Phases 0–2:** 125–200 person-days.
- **Phase 3:** 35–65 person-days over a year.
- **Running it:** 2–4 person-days a month.

At an illustrative loaded rate of €700 a day, that's €87,500–140,000 to production-grade and €1,400–2,800 a month to run, against $30–430 a month of cloud. Any build-versus-buy comparison (§4.3) should be on this total.

---

## 6. Appendix: code and config changes by file

### 6.1 tf-review

| File | Change | Item |
| --- | --- | --- |
| `src/TfReview.Api/CallerPolicy.cs` | Replace the `Workflow` constant (line 28) with a configured list of trusted workflows (`tf-review.yml`, `submit.yml` in the org repo). Later, trust by `repository_owner_id` plus installation instead of `AllowedRepositoryIds` | P0-02, P1-02, P2-01 |
| `src/TfReview.Api/ReviewApi.cs` | Where the allowlist comes from; the project in `ReviewId`; `ReviewsPerHour` and `MaxRequestBytes` from configuration; a submit metric with the workflow commit and repo, to retire old released commits | P1-03, P2-01 |
| `src/TfReview.GitHub/StickyComment.cs` | `BotLogins` (line 18) from configuration; a marker per project | P0-02, P1-03 |
| `src/TfReview.GitHub/CommentRenderer.cs` | A marker per project; apply `comment_min_severity`; count suppressed findings; link to the developer guide | P1-03, P1-07 |
| `src/TfReview.GitHub/RepositorySession.cs` | A check-run name per project; `external_id` set to the review ID; a neutral "not reviewed" check the watchdog can post | P1-03, P1-08 |
| `src/TfReview.GitHub/GitHubApp.cs` | Sign JWTs through a signer interface, with a Key Vault key implementation; tokens for the repo plus its standards repo | P0-09, P2-07 |
| `src/TfReview.Rag/GitHubTarballSource.cs` | Download through the API with the installation token; the host from configuration | P0-09 |
| `src/TfReview.Rag/TfReviewConfig.cs` | Map `Thresholds.CommentMinSeverity`, which is parsed and dropped today; `suppressions`; allowed standards owners | P0-09, P1-07 |
| `src/TfReview.Rag/References.cs` | Lock-file versions for `aws` and `google`, besides azurerm (line 81) | P2-05 |
| `src/TfReview.Core/PlanRedactor.cs` | Pattern and entropy detectors; configurable names and resource types; an optional strict profile; a test per layer | P0-08 |
| `src/TfReview.Core/ReviewMetrics.cs` | `repository` on cost and findings, `rule` on findings; review duration, queue wait and stale-review metrics; the workflow commit on submits | P0-12, P1-06 |
| `src/TfReview.Core/ModelPrices.cs` | Prices from configuration, including cache writes | P0-12 |
| `src/TfReview.Agents/TokenPacer.cs` | `DefaultTokensPerMinute` from configuration; then a reservation shared across replicas | P0-13, P2-04 |
| `src/TfReview.Agents/LlmClient.cs`, `src/TfReview.Worker/WorkerHost.cs` | A fallback deployment chain; the GitHub API base, vault name and App ID from configuration | P0-02, P1-08 |
| `src/TfReview.Scanners/CheckovRunner.cs` | The allowlist (lines 67–71) as data per cloud, from the standards repo or configuration | P2-05 |
| `src/TfReview.Worker/ReviewWorker.cs` | Store findings history; post per project | P1-03, P1-06 |
| `src/TfReview.Service/ReviewStores.cs`, `AzureStores.cs` | A TTL on statuses; findings documents; a project field | P0-07, P1-06 |
| `src/TfReview.Cli/Program.cs` | A `watch` command for the watchdog | P1-08 |
| `tests/TfReview.Eval/Program.cs` | `CasesDirectory` and the standards repo and commit as options | P1-05 |
| `tests/**` | Personal names in tests and fixtures become configured values | P0-02 |
| `scripts/service_eval.py`, `scripts/feedback_cases.py`, `scripts/fixtures.sh` | `DEMO` and `APP` from arguments or the environment; in a private fork, draft cases from any onboarded repo | P0-02, P1-05 |
| `.github/workflows/tf-review.yml` | Org values for the `image` and `api-url` defaults and the cosign identity; `runs-on`, `backend-config`, `state-key` and `project` inputs; per-project concurrency groups and artifact names; the provider cache plan.md describes; optional AWS and GCP sign-in; a `print-plan` option | P0-02, P1-02, P1-03, P2-05 |
| `.github/workflows/submit.yml` (new) | The review job alone, for bring-your-own-plan | P1-02 |
| `.github/workflows/release.yml` | A build provenance attestation; the private-signing choice | P0-11 |
| `.github/workflows/ci.yml` | The standards repo from the org; CodeQL for C#; dependency review; locked restore | P0-02, P0-10, P0-11 |
| `.github/workflows/infra.yml` | Apply per environment: nonprod on merge, prod behind team approval, each with its var file | P0-06 |
| `.github/workflows/e2e-live.yml`, `corpus.yml` | The Cosmos endpoint and standards repo from variables, not literals | P0-02, P0-06 |
| `.github/rulesets/main.json` | At least one approval, code-owner review, approval of the last push | P0-10 |
| `.github/CODEOWNERS`, `SECURITY.md`, `renovate.json` or `.github/dependabot.yml` (new) | Owners for prompts, the API, infra, bootstrap and workflows; automated updates | P0-10, P2-01 |
| `Directory.Build.props` | `RestorePackagesWithLockFile`, with locked mode in CI and the `Containerfile` | P0-11 |
| `global.json`, `Containerfile` | GA SDK `11.0.100` without `allowPrerelease`; GA digests for both .NET images; the AWS and Google Infracost plugins | P0-11, P2-05 |
| `.grype.yaml` | Resolve brotli and compress by 2026-12-06, go-jose by 2027-02-01 | P0-11 |
| `infra/main.tf`, `service.tf`, `apps.tf`, `dashboard.tf` | Variables for environment, region (the Cosmos `locationName` literal), names, capacities, `enable_free_tier`, log cap and retention, the App client ID, `service_image`, `maxExecutions` and the API's `maxReplicas`. Purge protection on the service vault. Diagnostic settings for Key Vault and the apps. Alert rules and an action group. The watchdog as a scheduled job. Cosmos continuous backup. Dashboard tiles per repo. `owner` tags set to the team | P0-06, P0-10, P0-12, P1-08 |
| `infra/env/nonprod.tfvars`, `infra/env/prod.tfvars` (new) | Per-environment values | P0-06 |
| `infra/released-commits.txt` | One per environment, with release candidates only in nonprod | P0-06 |
| `bootstrap/variables.tf` | `github_owner` becomes the org; `location` and `budget_amount` per environment; group object IDs; the split `repos` map | P0-05 |
| `bootstrap/repos.tf` | The organization's numeric ID in the OIDC subject, instead of `data.github_user`; teams as environment reviewers; groups instead of `deployer_*`; `plan_state` to Blob Data Reader after the test; the reviewer allowlist apart from platform resources | P0-05, P0-10 |
| `bootstrap/service.tf` | Resource-scoped roles; Cosmos assignments per container; groups instead of the deployer | P0-10 |
| `bootstrap/main.tf` | Forecast thresholds and an action group on the budget; a variable for the state account's replication (ZRS or GZRS); keep the lifecycle rule from #126 | P0-12, P2-08 |
| `bootstrap/state_keys.tf` | Crypto Officer to a group; keep purge protection | P0-05 |
| `bootstrap/backend.tf` | A backend for the bootstrap that CI runs, per environment | P0-05 |
| `justfile` | Endpoints and vault names from the environment (`corpus-search`, `eval-retrieval`); `secrets-sync` removed, since keys are generated into Key Vault; bootstrap recipes run through CI | P0-02, P0-05 |
| `plugin/.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json`, `README.md` | The org as owner, and its URLs; wording that fits Terraform | P0-02, P3-10 |
| `docs/contracts/*.schema.json` | `$id` URLs in the org; `project` in the review result; `suppressions` in the config; new `topics`, in both repos | P0-02, P1-03, P1-07, P3-09 |
| `docs/plan.md` | New decisions (§6.4); edits to D6, D8, D13, D15, D17, D24, D40, D45 and D56; tick the template-names backlog item | All |
| `README.md` | A real README: what it is, how to onboard, links to the guides | P2-06 |

### 6.2 tf-review-demo (the template) and tf-review-demo2

| File | Change | Item |
| --- | --- | --- |
| `infra/versions.tf` | The state vault URI (`kv-tf-review-state-prod`) and key name as inputs, or the block removed where the company's platform provides encrypted state | P0-05, P1-02 |
| `infra/main.tf` | The `owner` local set to a team | P0-02 |
| `.github/workflows/plan.yml` | The org's workflow path at a released SHA, kept current by Renovate | P0-02, P2-01 |
| `.github/workflows/apply.yml` | Team approval on `prod`; a variant for other backends and for Terraform | P0-05 |
| `.tf-review.yml` | The org's standards repo; `references: false` during the pilot | P1-10 |
| `.claude/settings.json` | The org's plugin marketplace | P0-02 |
| `README.md` | The onboarding guide | P2-06 |
| Seed branches | Moved to the nonprod sandbox repo | P0-06 |

### 6.3 tf-standards

| File | Change | Item |
| --- | --- | --- |
| `adr/`, `policies/` | The company's content; every high-severity policy with Rego and tests | P1-04 |
| `contracts/corpus-frontmatter.schema.json` | Kept in step with tf-review (`just contracts-sync`), including new topics | P3-09 |
| `.github/CODEOWNERS` (new), `.github/rulesets/main.json` | Owners per directory; at least one approval | P1-04 |
| `.github/workflows/lint.yml` | Also run the company eval suite on PRs | P1-05 |
| A doc template (new) | Frontmatter, sections, a Rego and test skeleton, and an eval-case checklist | P1-04 |

### 6.4 Outside the repos, and new decisions

**GitHub org settings:**

- An Actions policy: SHA-pinned, allowlisted actions.
- The OIDC subject format that bootstrap expects.
- Workflow access to a private `tf-review`.
- The App's installation on selected repos.
- Org rulesets, with the `tf-review` check pinned to the App (P2-03).
- Actions log retention.

**Azure:**

- Management-group placement and any Azure Policy exemptions.
- PIM for the groups.
- Model quota requests.
- Budgets.

**New rows for `docs/plan.md`:**

- **D60:** ownership and hosting.
- **D61:** service environments and promotion.
- **D62:** trust by org and App installation; revises D40 and D45's allowlist.
- **D63:** bring-your-own-plan, and several projects per PR.
- **D64:** data retention and the redaction profile; extends D25 and D53.
- **D65:** how Infracost runs; revises D18.
- **D66:** the required check, and behaviour during an outage; extends D39 and D55.
- **D67:** model fallback and a shared token budget; extends D4 and D54.
- **D68:** suppressions; extends D27.
- **D69:** findings history; extends D56.
