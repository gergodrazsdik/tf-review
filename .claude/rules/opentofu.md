---
paths:
  - "**/*.tf"
  - "**/*.tofu"
  - "**/*.tfvars"
---

# OpenTofu

- Use `tofu`, not `terraform`, and pin `required_version = "~> 1.13"`. Commit `.terraform.lock.hcl`.
- Check with `tofu fmt -check -recursive`, `tofu validate` and `tflint --recursive --config "$PWD/.tflint.hcl"`.
- `tofu plan` is fine. Applying is CI's job behind the `prod` environment, or the user's through a `just` recipe.
- No secrets in code, `*.tfvars` or outputs. Azure access uses Entra ID and OIDC, never storage or service keys.
- Name resources by ADR-001 and tag them by ADR-002 in `tf-standards`.
