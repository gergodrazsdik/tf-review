# Eval baseline

What later prompts, models and retrieval changes have to beat. Each number is from `e2e-live`, which runs every case in `tests/fixtures/cases` five times against the real model. A case passes only if the whole set of findings matches, so an extra finding fails it like a missed one.

## Current: 2026-11-06

Model gpt-6.1-sol for every role, at low reasoning effort. Prompts conventions, security and cost v6, orchestrator v1, arbiter v2, suggester v1. `tf-standards` at `adf50af`. Concept patterns as of 2026-11-06 (see [the rescoring](#concept-patterns-tightened-every-saved-run-rescored-2026-11-06)).

| Measure | Result, over the seven saved runs without references, 2026-11-05 and 11-06 |
| --- | --- |
| Runs passing | 155 of 155 in each |
| Suggestion concepts, C18–C31 | 111 to 115 of 210 |
| Of those, on held-out cases (C18, C19, C21–C24, C31) | 47 to 50 of 105 |
| Estimated cost of a full run | About $0.30 to $0.37 at list prices; with cache writes, $0.39 to $0.54 on the three runs it was worked out for |

That's the bar. Only held-out cases say how the reviewer does on plans nobody tuned against; the others are regression cases (see [the held-out cases](#how-the-held-out-cases-went)). `e2e-live` reports the two kinds apart. Every concept score in the dated sections below used the patterns of its day; the rescored table at the end gives every saved run under today's, and `just rescore` reproduces it from `docs/evals/runs/`.

## gpt-5.4-mini baseline: 2026-11-02

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

## With reference material: 2026-11-03

Prompts conventions, security and cost v5, reranker v1, corpus version `9e64e77a8010f482`. Two `e2e-live` runs, one each way, of the same code (v0.0.11):

| Measure | Without references | With references |
| --- | --- | --- |
| Runs passing | 83 of 85 | 84 of 85 |
| Failures | C13 twice: private SSH reported again | C14 once: the cost agent flagged a $20, 20% increase, which is under both of ADR-003's rules |
| Input tokens (cached) | 217K (71K) | 829K (513K) |
| Estimated cost of a full run | About $0.22 | About $0.56, so about $0.0066 a review |

The baseline holds. One run each way can't separate 83 from 84, and C13's and C14's misses are both near misses that already flip now and then, so neither result counts as a gain or a regression.

**Suggestions,** read side by side for every case: the same findings in each run, and the suggestions slightly more specific with references, never off topic. C3 names narrower roles (Reader, Contributor), C7 adds `prevent_destroy` before the replacement, and C12 quotes the source range. The rest read the same. That's one reading of existing cases. The plan asked for new held-out cases scoring suggestions, which aren't written yet.

The demo turned references on afterwards (demo #37).

## Do references make suggestions better? 2026-11-04

Four held-out cases, C18–C21, each breaking a team policy, so the finding comes from the policy and reference material can only shape the suggestion (D42). Each has a `suggestions.json`: three concepts a good suggestion should mention, as patterns fixed before any model saw the case, including the Azure-specific guidance that only the references carry, such as just-in-time access for RDP or conditions on a role-granting role. `e2e-live` scores them and reports the score without failing a run on it. Five runs each way, same code:

| Measure | Without references | With references |
| --- | --- | --- |
| Findings | 20 of 20 | 20 of 20 |
| Suggestion concepts | 32 of 60 | 31 of 60 |
| Concepts only the references carry (network rules, RBAC Administrator or a custom role, conditions or PIM, `prevent_destroy`, just-in-time) | 0 of 25 | 1 of 25 |
| Estimated cost (20 reviews) | About $0.07 | About $0.19 |

**References don't make suggestions better, on this measure.** Every run had two or three reference sources and no provider docs missing, so the material arrived. The suggestions read the same either way: the fix the team's standard names (HTTPS and TLS 1.2, a narrower scope, keeping the login, a bastion or private range), and nothing the references add. The prompts ask for the change that resolves the finding, which may be why. That's a hypothesis for a later prompt version, to test on new held-out cases rather than these.

One pattern was wrong and fixed after the runs: C20's backup concept, `back ?up`, missed "backed up". The scores above use the fix for both columns; with the original pattern both were 28 of 60.

The earlier side-by-side reading of C1–C17 found suggestions slightly more specific with references. This measure, which was fixed in advance, doesn't confirm that.

## A prompt that asks for more: 2026-11-04, not shipped

The idea: v5 asks for "one plain sentence" with the standard's fix, which leaves references nothing to add. A trial security and cost v6 allowed two sentences, the standard's fix first and then "a safer or more complete way to fix this resource" from the reference material, by name, "never advice of your own". It was measured on five new held-out cases, C22–C26, written before the prompt was run on them: public blobs turned on by an update (POL-001), SSH open in an NSG's inline rule (POL-002), `Owner` on a management group (POL-003), a key vault replaced by a rename (POL-004), and a 4x VM resize (ADR-003). Five runs each, all four combinations:

| C22–C26, 75 concepts | Without references | With references |
| --- | --- | --- |
| v5 | 31 | 32 |
| v6 | 37 | 37 |

On C18–C21, seen before and so only a check: v5 32 and 31, v6 30 and 36. Findings held: v6 passed 129 of 130 runs without references (C13 once) and 130 of 130 with them.

**What the score doesn't show,** from reading every second sentence v6 wrote on C22–C26:

- Without references, every added sentence was on topic (Bastion, SAS or private endpoints, PIM, a smaller size), but came from the model's own knowledge, which the prompt forbade. The rule doesn't hold.
- With references, about 8 of 19 were redundant or beside the point: `allowBlobPublicAccess`, which is the ARM name of the attribute the finding already fixes (3), a key vault firewall for a vault being replaced (1), and the "Allowed virtual machine size SKUs" policy for a resize (3). The good ones were good: savings plans and reservations for the VM, conditions with PIM for `Owner`.
- Suggestions grew from about 100 to 164 characters on average with references.

**Not shipped:** the demo runs with references, so v6 there would add some coverage and a lot of noise to PR comments. The cause looks like retrieval more than the prompt: references are chosen per agent, for everything it reviews, so a finding gets material about its resource rather than its problem, and the model uses what it's given. The next try is references chosen per finding, from its rule and attribute, with a prompt rule that the added sentence must address the same problem, measured on new held-out cases, because C22–C26 have now been read.

## The part 6 gate: 2026-11-03

- Demo #38, an app subnet with an NSG allowing only HTTPS from the internet, was reviewed through the service with references on: the App posted a success check run and a "No findings" comment, which credits the reference sources and lists the NSG and rule to look at first. That's the right answer.
- Demo #39, a draft PR pointing `api-url` at a `.invalid` host, stood in for an outage: the submit failed after its retries, and the workflow posted "Not reviewed" for that commit with its own token (D39). It took 5 minutes, all of it retries.
- Retrieval against the metadata baseline is in `retrieval.md`: hybrid with re-ranking finds 34–36% of the labelled docs and gives 13 of 15 agents something relevant, against 9 for filtering alone.

## How the held-out cases went

These are the honest part of the suite, because C1–C10 were used to tune the prompts, or are things a policy finds anyway. Held-out cases had their expected results fixed before any model saw them. They run on every `e2e-live`, so "held out" can't mean measured once: it means nothing has been changed after reading their answers or failures, not a prompt, code or their suggestion patterns. Once something has, the case loses `held_out` in its `expected.json` and becomes a regression case, as C13 did below.

On 2026-11-06 that rule was applied to the later cases too (MNT-1 of the 2026-11-06 review), and these lost the flag:

| Case | What was changed after reading it |
| --- | --- |
| C11 | The arbiter can't drop findings (D36), after it dropped one here |
| C14 | Cost v4: code supplies the percentage, after a 20% rise was flagged here |
| C15, C26 | Cost v6, after the cost agent retracted real increases here (and on C4) |
| C20, C25, C27 | Their `in_place` patterns, widened after reading gpt-6.1-sol's answers (#111) |
| C28, C29 | Policy findings answered by ID (D51), after these failed |
| C30 | Cost v6, written from this case's answer |

Still held out: C12, C16, C17, C18, C19, C21, C22, C23, C24 and C31. C18–C26's suggestions were read in the 2026-11-04 prompt trial below, which wasn't shipped, so nothing changed from them. The flag is all a report has: `e2e-live` and `rescore` count held-out and tuned cases apart. New held-out cases are needed before the next prompt or model decision.

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

## References per finding (D50): 2026-11-04

The per-agent trial above pointed at retrieval: an agent's references are about its resources, not the problem a finding is about. D50 searches per finding instead, from the standard's title, the resource type and the attribute name, after the arbiter. One model call (suggester v1) may add one sentence, which code keeps only if it's grounded in an offered excerpt and isn't a rename of the fix. Agents get no references in this mode, so findings can't change. Five new held-out cases, C27–C31, were written before the code: a PostgreSQL server replaced to turn on geo-redundant backup, a Cosmos DB account deleted, a port range 20–25 open to the internet, a new Business Critical SQL database, and storage switched to plain HTTP. Five runs each:

| C27–C31, 75 concepts | No references | Per agent | Per finding |
| --- | --- | --- | --- |
| Suggestion concepts | 28 | 21 | 27 |
| Runs passing | 20 of 25 | 17 of 25 | 18 of 25 |
| Estimated cost (25 reviews) | About $0.08 | About $0.25 | About $0.11 |

**On the held-out cases it made no measurable difference.** It added a sentence to 4 of 25 findings, all on C27 and all on point: geo-redundant backup can only be set when a server is created, which is why the plan replaces it. The run failures aren't about references, because per finding the agents see exactly what they see without them (see below).

**On seen cases it did more.** The full suite once in this mode (C1–C26) added a sentence to 30 of 114 findings that cite a standard: `prevent_destroy` for the replaced database (C7, 5 of 5), a savings plan or Spot VMs for resizes (C4), built-in job-function roles before `Owner` or User Access Administrator (C3, C19, C24), tags instead of details in names (C6), and soft delete for the key vault, which is on by default now so it adds little (C25). Nine were advice to use Azure Policy to allow only approved VM sizes (C4, C15, C26), which the prompt forbids. Leaving Azure Policy excerpts out didn't stop it, because the Cloud Adoption Framework says it too, and it lost C27's sentence, so instead code drops any added sentence recommending Azure Policy. That leaves 21 of 114, nearly all on point. The C1–C26 suite passed 129 of 130 in this mode (C26 once), and it costs about $0.70 for 155 reviews.

**Not the default yet.** It's better than per-agent references on every measure taken: cost, noise, and findings it can't touch. But the held-out cases didn't show suggestions getting better, so whether `references: true` switches to it is a decision for the plan, not a result.

**What the held-out cases found instead,** in all three columns alike:

- C28: a deleted Cosmos DB account failed 15 of 15 runs, and waited in `tests/fixtures/pending/` until D51 fixed it; it's been in the suite since, and `pending/` is gone. The security prompt tells the agent to ignore resources whose actions are only `delete`, so it never re-reports POL-004 on a deletion, and a reviewer gets the policy's finding with no suggestion at all.
- C29: in 4 of 15 runs the security agent didn't re-report POL-002 on a port range holding 22, so that finding had no suggestion either.

## Policy findings answered by ID (D51), and per finding by default: 2026-11-04

The held-out cases C28 and C29 found policy findings left without a suggestion, because agents had to re-report each one as a new finding pointing at an attribute. A deletion has no attribute to point at, and the model sometimes skipped one. Conventions and security v6 answer each policy finding by ID instead, and code attaches the suggestion to the policy's own finding. `references: true` now means per-finding references (D50). The full suite, five runs per case:

| Measure | Without references | Per finding |
| --- | --- | --- |
| Runs passing | 153 of 155 | 151 of 155 |
| C28 (deletion) and C29 (port range) | 10 of 10 | 10 of 10 |
| Failures | C26 twice: cost retracted a real increase | C4, C26 and C30 once each, the same way; C7 once, conventions reporting a valid owner tag |
| Suggestion concepts, C18–C31 | 82 of 210 | 91 of 210 |
| Estimated cost of a full run | About $0.37 | About $0.63 |

C28 went from 0 of 15 to 10 of 10 with its expectations unchanged, and its suggestions now say what to confirm before the deletion. C29 went from 11 of 15 to 10 of 10.

**The cost agent's misses aren't this change's.** It wrote the ADR-003 finding and then marked it as not breaking the standard, so code dropped it. The cost prompt didn't change, and giving it back its old schema made no difference (9 and 6 misses of 25 on the cost cases). `main` run the same day missed 4 of 15 on C4, C15 and C26, where C4 had never failed. Something on the model's side moved, and it's a task of its own.

## Cost v6: 2026-11-04

The cost agent sometimes wrote an ADR-003 finding and then marked it `breaks_standard: false`, so code dropped it. `--show-answers` caught one, on C30: the title read "adds $1,785.90/month, from $0.00 to $1,785.90", and the last field asked whether the title "says the resource breaks the standard". A cost title only states figures, so it never says either way, and the model guessed: often on one day (`main` missed 4 of 15 runs on C4, C15 and C26) and rarely the next (1 of 30). The C13 field was written for titles that carry a judgement.

Cost v6 names the rule the figures meet in the title, such as "adds more than $50 a month", and the last field checks the figures against that rule, with `false` when the title names none.

| Measure | Result |
| --- | --- |
| Cost cases, ten runs each (C4, C14, C15, C26, C30) | 50 of 50, no finding retracted, and C14 still has none |
| Full suite with per-finding references, five runs per case | 155 of 155, the first clean full run |
| Suggestion concepts, C18–C31 | 96 of 210 |
| Estimated cost of a full run | About $0.64 |

v5 passed 29 of 30 on the day v6 was measured, so the score alone doesn't prove the fix: the evidence is that v6 removes the ambiguity the failed answer showed.

## Candidate models: 2026-11-05

Tier 1 brought Data Zone quota for gpt-6.1-sol and gpt-6-luna (D4). Both passed the structured output smoke test, then ran the full suite on every agent, five runs per case, without references, beside the same day's gpt-5.4-mini run on #108:

| Measure | gpt-5.4-mini | gpt-6.1-sol | gpt-6-luna |
| --- | --- | --- | --- |
| Runs passing | 155 of 155 | 155 of 155 | 154 of 155 |
| Failures | | | C18 once: conventions read the workload in `stexportsprod` as having to match the project tag, which ADR-001 doesn't say |
| Suggestion concepts, C18–C31 | 92 of 210 | 103 of 210 | 81 of 210 |
| Input tokens, of which cached | 484K, 283K (58%) | 484K, 427K (88%) | 484K, 427K (88%) |
| Estimated cost of a full run | About $0.36 | About $0.37, about $0.54 with cache writes | About $0.03, about $0.04 with cache writes |

The estimates use the Retail Prices API's list prices (`ModelPrices`), which leave out the GPT-6 models' cache writes ($3.00 and $0.15 a million); the second figure assumes the uncached input, 57K tokens, is what got written. Sol pays three times mini's input price but caches more of the prompt and writes less than half the output, about 15K tokens against 34K.

Concepts missed, out of each case's concepts over five runs:

| Case | mini | sol | luna |
| --- | --- | --- | --- |
| C19 access admin | 10 | 11 | 13 |
| C20 replace server | 7 | 5 | 10 |
| C21 RDP from the internet | 10 | 5 | 10 |
| C23 inline SSH | 9 | 5 | 10 |
| C24 Owner on a management group | 10 | 11 | 11 |
| C25 replace Key Vault | 6 | 9 | 10 |
| C27 replace PostgreSQL | 7 | 10 | 10 |
| C29 SSH in a port range | 9 | 5 | 5 |
| C30 business-critical database | 10 | 6 | 10 |
| C18, C22, C26, C28, C31 | 40 | 40 | 40 |

**Sol suggests better on network exposure** (C21, C23, C29) and C30, and worse on replacing data stores (C25, C27), all within the security agent, so the gain doesn't point to one agent over another. This section first said sol's 103 was "outside mini's range over three runs (88, 91, 92)". That range mixed conditions: 91 was a run with references (2026-11-04, per finding), while sol's was without, and the same day's mini run without references, 82, was left out. The comparison that holds, from every saved run without references on the same prompts, is in [the rescoring](#concept-patterns-tightened-every-saved-run-rescored-2026-11-06): mini 88 to 97 over eight runs, sol 111 to 115 over seven.

**Luna doesn't qualify for the agents.** D4 asks it to beat mini on the evals, not on price: it found fewer concepts and made the false positive mini also made once on #106. The roles that only order or pick (orchestrator, re-ranker) weren't measured on their own, and are where its price could count.

**Two more sol runs** confirmed it:

| Run | Runs passing | Suggestion concepts | Cached input | Estimated cost, list and with cache writes |
| --- | --- | --- | --- | --- |
| Sol, first | 155 of 155 | 103 of 210 | 88% | About $0.37, about $0.54 |
| Sol, second | 155 of 155 | 102 of 210 | 94% | About $0.30, about $0.39 |
| Sol, third | 155 of 155 | 106 of 210 | 90% | About $0.35, about $0.50 |

The same cases moved each time: C20, C21, C23, C29 and C30 better, C25 and C27 worse. All of them are the security agent's, so a model per agent wouldn't separate them. **gpt-6.1-sol became every role's model on 2026-11-05**, at about 1.1 to 1.5 times mini's cost; mini stays deployed as the fallback, and luna was removed. Sol's weaker POL-004 suggestions on replacing a data store (C25, C27) turned out to be the patterns: see below.

## C20, C25 and C27 rescored: 2026-11-06

Sol's apparent weakness on replacing a data store (C25, C27) was in the patterns, not the suggestions. Its answers kept saying to keep the resource, as "retain the existing name", "avoid replacement" or "revise the plan to avoid replacement", which the `in_place` patterns didn't accept: they took "keep the existing" and "in place". The three POL-004 cases' `in_place` now also accept `retain the (original|existing|current)` and `avoid (the |a |this )?replacement`. That's a change made after reading answers, so it stays to the same concept, and every saved run was rescored alike:

| Run | Concepts before | After | `in_place` found, C25 and C27, of 5 each |
| --- | --- | --- | --- |
| gpt-5.4-mini | 92 of 210 | 92 | 4 and 3, unchanged |
| gpt-6.1-sol, three runs | 103, 102, 106 | 112, 111, 113 | 5 and 5 each time, from 0 to 3 |
| gpt-6-luna | 81 | 81 | 0 and 0 |

This section first argued that because mini's and luna's scores didn't move, the wider pattern didn't favour the model it was read from. That's backwards: a pattern widened to one model's phrasing and matching no one else's is evidence it accepts that model's wording, not that it's neutral. Mini said the same thing as "avoid replacing the server", which neither pattern accepted, so the change scored the same concept for one model only. The 2026-11-06 patterns accept both. What sol still misses there, C25's soft delete and C27's replica, isn't in POL-004: it's what per-finding references add, and these runs have none.

## The near misses on gpt-6.1-sol: 2026-11-06

C9, C13, C14 and C18 each failed now and then on gpt-5.4-mini, and C18 once on gpt-6-luna: a missed environment value, private SSH reported, a cost within ADR-003's limits reported, and a valid storage account name read as breaking ADR-001. On sol, ten more runs of each passed 40 of 40, which with its three full runs makes 25 of 25 each, for about $0.09. They stay in the suite as near misses; 25 runs can't rule out a rare miss.

## Concept patterns tightened, every saved run rescored: 2026-11-06

The 2026-11-06 review (MNT-2) found the suggestion patterns scoring wording that isn't the concept: a word inside another ("move" in "remove", "SAS" in "disastrous", "Standard" in "cost standard"), generic words ("confirm", "narrow", "separate", "new server", a bare "21"), and on C27 the very attribute that forces the replacement (`geo_redundant_backup_enabled` scored both backup and in-place). The patterns now:

- use word boundaries;
- drop the generic words. ADR-003's ask, to confirm the increase is intended, is still a concept on C26 and C30, but it needs the intent or need ("intended", "needed for", "justify"), not the verb. C28's "confirm" likewise needs the deletion to be intended or the data disposable;
- never take a replace path's attribute as another concept, nor geo-redundant backups replicating to a paired region as C27's replica concept;
- accept each concept's plain variants for every model alike: "avoid replacing" beside "avoid replacement", "keep ... unchanged", and on C29 removing the admin port from the range.

`tests/TfReview.Eval.Tests/ConceptTests.cs` holds sentences that must and mustn't match, the review's examples among them, for every case. Every saved run was rescored with `just rescore`, which reads the suggestions kept in `docs/evals/runs/`: one file per run, the CI runs named by their Actions run ID, whose artifacts expire after 14 days. "At the time" is what the run printed; "after #111" is the patterns between #111 and this change.

| Saved run | Model | References | Runs passing | At the time | After #111 | Now, of 210 | Held out now, of 105 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `2026-11-04-d51-none`, local, D51 | mini | none | 153 of 155 | 82 | 82 | 90 | 38 |
| `2026-11-04-d51-finding`, local, D51 | mini | per finding | 151 of 155 | 91 | 91 | 95 | 38 |
| `2026-11-04-d50-full-finding`, local, D50 | mini (v5 prompts) | per finding | | 90 | 91 | 93 | 43 |
| `41853303470`, #95 | mini (cost v5) | none | 153 of 155 | 85 | 85 | 92 | 40 |
| `41862615921`, #96 | mini | none | 154 of 155 | 88 | 88 | 91 | 39 |
| `41908813773`, #101 | mini | none | 155 of 155 | 88 | 88 | 93 | 39 |
| `41920080565`, #104 | mini | none | 155 of 155 | 92 | 92 | 96 | 43 |
| `41923915636`, #104 | mini | none | 155 of 155 | 88 | 88 | 97 | 44 |
| `42093001506`, #106 | mini | none | 155 of 155 | 92 | 92 | 95 | 41 |
| `42102380469`, #108 | mini | none | 155 of 155 | 92 | 92 | 93 | 41 |
| `42104673959`, dispatch | sol | none | 155 of 155 | 103 | 112 | 112 | 48 |
| `42104678906`, dispatch | luna | none | 154 of 155 | 81 | 81 | 84 | 36 |
| `42109636904`, dispatch | sol | none | 155 of 155 | 102 | 111 | 111 | 47 |
| `42109642593`, dispatch | sol | none | 155 of 155 | 106 | 113 | 113 | 49 |
| `42115702849`, #110 | mini | none | 154 of 155 | 91 | 91 | 96 | 42 |
| `42116475283`, #110 | mini | none | 155 of 155 | 83 | 83 | 88 | 38 |
| `42222458244`, #111 | sol | none | 155 of 155 | 112 | 112 | 112 | 48 |
| `42222832383`, #111 | sol | none | 155 of 155 | 115 | 115 | 115 | 50 |
| `42223704912`, #112 | sol | none | 155 of 155 | 104 | 113 | 113 | 48 |
| `42234473307`, #115 | sol | none | 155 of 155 | 113 | 113 | 113 | 50 |

The CI runs ran the whole suite five times; "at the time" for #112 used the patterns from before #111, which it branched from. The earlier, smaller trials rescore like this (concepts now, of what they asked, then on held-out cases):

| Saved run | Section | At the time | Now | Held out now |
| --- | --- | --- | --- | --- |
| `2026-11-04-sug-off`, `-sug-on` | Do references make suggestions better? (C18–C21) | 32, 31 of 60 | 33, 31 | 23, 21 of 45 |
| `2026-11-04-v5-off`, `-v5-on` | A prompt that asks for more, v5 (C22–C26) | 31, 32 of 75 | 31, 32 | 16, 17 of 45 |
| `2026-11-04-v6-off`, `-v6-on` | The same, v6 (C18–C26) | 67, 73 of 135 | 67, 74 | 39, 47 of 90 |
| `2026-11-04-d50-none`, `-agent`, `-finding` | References per finding (C27–C31) | 28, 21, 27 of 75 | 29, 23, 27 | 5, 5, 5 of 15 |
| `2026-11-04-d50-no-policy-excerpts` | The same, Azure Policy excerpts left out | 33 of 75 | 36 | 10 of 30 |

The cost v6 run with references (96 of 210) kept no suggestions, so it can't be rescored.

**What changes, and what doesn't:**

- **Sol still suggests better than mini,** by a margin the tightening didn't touch: mini 88 to 97 over its eight runs without references on the v6 prompts (#96 to #110), sol 111 to 115 over seven. On held-out cases only, mini 38 to 44 of 105 and sol 47 to 50. The ranges don't overlap, but the runs weren't back to back, and the suite changed around them (D53's cache, D54's batches), though not the prompts.
- **Mini gained,** 1 to 9 a run, from accepting "avoid replacing", which mini and luna write and sol doesn't (sol writes "avoid replacement", which #111 had already accepted), less what it lost: the attribute on C20 and C27, "replicated" backups as C27's replica, and a bare "confirm" on C28. Sol's scores didn't move: none of its answers matched only through the wording dropped, and none says "avoid replacing".
- **References per finding still don't make suggestions measurably better** on these cases (mini, D51 day: 90 without, 95 with; held out, 38 and 38).
- **Luna stays below both** (84).
