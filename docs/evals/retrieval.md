# Retrieval evals

What each seeded case's agent gets as reference material, compared with docs picked by hand, for each ranking (D5). Agent evals can't show this, because a reference can't create a finding (D42), so a poor one only shows as a worse suggestion. `just eval-retrieval` runs it against the real index as you, read-only, in about half a minute and for a fraction of a cent.

## How it works

- **Labels:** `tests/fixtures/retrieval/cases.yml` lists, for 15 of the 17 cases, the imported docs a good search should find, 2 to 6 per case. They were picked from titles and paths before anything was measured, and weren't changed afterwards. C7 and C11 are left out, because no doc stood out as the right one.
- **Query:** the one reviews send, from `ReviewRunner.ReferenceQueryFor`: the agent's topics, its slice's types, and the names of changed attributes. The slice is routed as in a review, by the standards that apply (D35).
- **Found:** any chunk of a labelled doc within the agent's 2,000-token budget. A heading narrows a label to one section.
- **Rankings:** `Metadata` is filtering alone, the provider docs for the slice's types in a fixed order. `Vector` and `Hybrid` search everything, with hybrid fusing vector distance and full-text score by reciprocal rank.

## Results: 2026-11-03, corpus version `9e64e77a8010f482`

| Ranking | Cases with something relevant | Relevant docs found | Chunks per agent | Tokens per agent |
| --- | --- | --- | --- | --- |
| Metadata | 9 of 15 | 10 of 64 (16%) | 3.1 | 1,738 |
| Vector | 10 of 15 | 12 of 64 (19%) | 4.3 | 1,938 |
| **Hybrid** | **13 of 15** | **17 of 64 (27%)** | 4.2 | 1,924 |

**Hybrid is what reviews use.** It gives 13 of 15 agents something relevant, against 9 for filtering alone, which is the improvement vectors were meant to show. It still misses most labelled docs, because about four chunks fit the budget and some cases list six docs.

## Re-ranking: 2026-11-03 (D49)

| Ranking | Run 1 | Run 2 |
| --- | --- | --- |
| Hybrid | 27% | 27% |
| Vector, re-ranked | 38% | 36% |
| **Hybrid, re-ranked** | **36%** | **34%** |

A model picks up to 6 of 15 candidates of each kind, by title, heading and opening, and code fills the budget in that order. Both re-ranked rows give 13 of 15 agents something relevant. Reviews re-rank hybrid's candidates, because if the model fails, hybrid's own order is the better fallback.

### How it got there

The labelled docs were mostly in the top 50, which says the search finds them but doesn't rank them into the few slots the budget allows. These were tried first, each measured against the same labels:

| Tried | Result | Why not |
| --- | --- | --- |
| Identifiers written as words, with or without short values, or only set attributes | 30–31% | The query states facts, but not what they mean for a review |
| A model-written query, as a list and as guidance text | 25–27% | Better text, same docs found |
| Policies indexed without their rule JSON, offline and vector only | 11% | The JSON's identifiers were what today's query matched |
| Hand-written queries | 59% | Written already knowing the labels, so not a fair test |


### What changed on the way

The first run had hybrid and vector both at 22%. Each change below was measured alone, against the same labels:

| Change | Hybrid | Vector | Kept |
| --- | --- | --- | --- |
| First run | 22% | 22% | |
| Leave out provider docs' outputs ("Attributes Reference") | 22% | 17% | Yes. A review never needs outputs, and vector lost the small chunks that had filled its budget |
| Policies and framework pages before provider docs | **27%** | 17% | Yes |
| Leave `location` and `resource_group_name` out of the query | 27% | 19% | Yes, neutral |
| A 3,000-token budget instead of 2,000 | 28% | 28% | No: little for hybrid, at half again the cost |

## What this doesn't show

- **Whether references make reviews better.** That's the agent evals' job, with references in the prompt.
- **More than one labeller's view.** The labels are one person's picks from titles, so a found doc that isn't labelled counts for nothing, and some may be just as good.
- **Other queries.** Every case is one small change, and real PRs mix many.

## Ideas not tried yet

- A model-written query against an index without rule JSON, which reached 30–34% offline, and no more than re-ranking with it

- Fold near-identical policies together, such as the 429 "Enable logging" ones, so one family can't fill the results.
- Give each kind of doc its own share of the budget instead of alternating.
- Split long "Arguments Reference" sections smaller, which needs a new chunker version and a re-index, about 5 cents.
