# tf-review
Multi-agent RAG review board for OpenTofu PRs

The plan, decisions and milestones are in [`docs/plan.md`](docs/plan.md), and the eval results in [`docs/evals/baseline.md`](docs/evals/baseline.md).

## Develop

You need the .NET SDK `global.json` pins, [`just`](https://github.com/casey/just), `uv`, OpenTofu, conftest and the other CI tools at the versions `scripts/install-ci-tools.sh` pins, and [`tf-standards`](https://github.com/gergodrazsdik/tf-standards) checked out beside this repo (`../tf-standards`): the tests and the replay read its working tree, where CI uses the commit `.tf-review.yml` pins. `just` alone lists every recipe.

- **Build:** `dotnet build -m:1`. In the Claude sandbox a build without `-m:1` hangs.
- **Test:** `just test` builds and runs each test project; CI runs `dotnet test`.
- **Lint:** `just lint` runs the checks that work anywhere (formatting, YAML, workflows, contracts, the plugin's hook cases, the Python tests, ruff, the cross-repo copies, gitleaks); `just lint-dotnet` checks C# style; `just validate` runs `tofu validate` and TFLint, whose plugins can't start in the Claude sandbox.
- **Evals:** `just e2e` replays recorded model answers over the seeded cases in `tests/fixtures/cases`, offline and free. `just e2e-live` calls the real model and costs cents; the `e2e-live` workflow is the usual way. `just rescore` scores the saved suggestions in `docs/evals/runs` against the current concept patterns, offline.
- **Release:** `just release status` says what's released, deployed and pinned and what comes next; `just release --help` lists the steps (pin, trust, tag, repin, seeds). Run it outside the Claude sandbox.
- **Merge:** `just merge <pr>` fast-forwards `main` to a PR's head after checking its required checks and signatures, which is how `main` takes PRs (D60).
- `just hooks` installs the git hooks.

## Claude Code plugin

`tf-review-opentofu` brings the reviewer's guardrails into Claude Code for a repo tf-review reviews: a skill that reads the repo's pinned standards before writing OpenTofu, a hook that blocks `apply`, `destroy`, `test`, `import` and the commands that change state, whether quoted, path-qualified or wrapped, and a format check on each edited `.tf` file. Its hooks run `python3`.

Enable it for everyone working on a repo in its checked-in `.claude/settings.json`, pinned to a release tag, because the hooks run on your machine:

```json
{
  "extraKnownMarketplaces": {
    "tf-review": {
      "source": { "source": "github", "repo": "gergodrazsdik/tf-review", "ref": "plugin-v0.1.0" }
    }
  },
  "enabledPlugins": { "tf-review-opentofu@tf-review": true }
}
```

Or install it for yourself: `/plugin install tf-review-opentofu --marketplace gergodrazsdik/tf-review`.
