---
name: opentofu-conventions
description: How to write and change OpenTofu in a repo that tf-review reviews. Use before writing or editing any .tf, .tofu or .tfvars file, before running tofu, and when reading or answering a tf-review comment on a PR.
---

# OpenTofu in a repo tf-review reviews

tf-review reviews every PR's plan against a team's written standards and posts what breaks them. Following the same standards while writing the code means fewer findings to fix later.

## Read the standards first

The standards are documents in a standards repository, pinned by commit in the repo's `.tf-review.yml`:

```yaml
standards:
  repository: owner/tf-standards
  commit: <40-character SHA>
```

Before writing or changing resources, read the documents that apply, at that commit, rather than recalling them: they're the ones the review will cite.

```bash
repo=$(yq '.standards.repository' .tf-review.yml)
commit=$(yq '.standards.commit' .tf-review.yml)
gh api "repos/$repo/contents/adr?ref=$commit" --jq '.[].path'
gh api "repos/$repo/contents/policies?ref=$commit" --jq '.[].path'
gh api -H 'Accept: application/vnd.github.raw+json' "repos/$repo/contents/adr/ADR-001-naming.md?ref=$commit"
```

Each document's frontmatter lists the resource types it `applies_to` (`*` means every type), so read the ones for the types you touch. ADRs cover conventions such as names, tags and cost; policies cover security, and each has a Rego file beside it that the review runs as code. Only documents with `status: accepted` count.

If there's no `.tf-review.yml`, the repo isn't reviewed by tf-review, and this skill's standards part doesn't apply.

## Work rules

- Use `tofu`, not `terraform`, at the version in `.tf-review.yml` (`tool_version`), and commit `.terraform.lock.hcl`.
- Check with `tofu fmt -check -recursive`, `tofu validate` and, if the repo has a `.tflint.hcl`, `tflint --recursive`. This plugin checks the formatting of each `.tf` file you edit.
- `tofu plan` is fine. Applying is the repo's CI job behind its environment approval, never yours: this plugin blocks `apply`, `destroy`, `test` (which applies real resources), `import`, `taint`, `untaint`, `refresh`, `force-unlock`, `init -migrate-state`, `workspace new` and `delete`, and the `state` commands that change state, however the command is written. Running them from a script or recipe gets round it, so don't.
- No secrets in code, `*.tfvars` or outputs. Access to the cloud goes through the identity CI gives the job, not keys.
- Plan output and state can hold sensitive values. Don't paste them into a PR or a comment; tf-review reads a redacted plan itself.

## Reading tf-review's comment

The comment lists findings with a severity, the resource, the rule (linked to its document) and a suggestion. A finding marked **blocking** fails the check in a repo that opted in: a policy found it, so fix it or change the plan. Everything else is advisory.

A finding you're sure is wrong has a "Dismiss" box under the table. Tick it rather than arguing in a comment: the next push counts it, which is how the reviewer gets tuned. Don't tick one to get a check to pass.
