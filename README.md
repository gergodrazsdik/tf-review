# tf-review
Multi-agent RAG review board for OpenTofu PRs

## Claude Code plugin

`tf-review-opentofu` brings the reviewer's guardrails into Claude Code for a repo tf-review reviews: a skill that reads the repo's pinned standards before writing OpenTofu, a hook that blocks `apply`, `destroy`, `import` and the commands that change state, and a format check on each edited `.tf` file. Its hooks run `python3`.

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
