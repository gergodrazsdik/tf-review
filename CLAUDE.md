# tf-review

Multi-agent, RAG-backed reviewer for OpenTofu pull requests. The plan, decisions and milestones are in `docs/plan.md`: update it in the same PR when a decision, contract or milestone changes, and tick tasks as they land.

## Commands

- `just lint`: every check the CI `lint` job runs
- `just contracts`: validate the contract examples against their schemas
- `just bootstrap-validate`: validate `bootstrap/` without a backend
- `just hooks`: install the git hooks

`just bootstrap-plan`, `bootstrap-apply` and `bootstrap-migrate` read the GitHub token from pass and belong to the user. Agents check their work with `tofu -chdir=bootstrap plan` at most, never apply.

## Conventions

- This repo owns the contracts in `docs/contracts/`. `tf-review-demo` and `tf-standards` depend on them, so a breaking change means a new version and changes in those repos.
- `tf-standards` keeps a copy of `corpus-frontmatter.schema.json`: change both together.
- `scripts/install-ci-tools.sh` is identical in all three repos. Keep its pins the same as `docs/plan.md` and the setup doc.
- `main` takes squash-merged PRs only, and third-party actions are pinned to full SHAs with `pinact`.
