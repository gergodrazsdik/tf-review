# List the recipes
default:
    @just --list

# Start Claude Code with the demo and standards repos in reach
[group('dev')]
claude *args:
    CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD=1 claude \
      --add-dir ../tf-review-demo --add-dir ../tf-standards {{ args }}

# Install the git hooks
[group('dev')]
hooks:
    lefthook install

# Run every pre-commit check on all files; the CI lint job runs this too
[group('dev')]
lint: contracts bootstrap-validate
    tofu fmt -check -recursive .
    tflint --init
    tflint --recursive --config "$PWD/.tflint.hcl"
    yamllint --strict .
    actionlint
    pinact run --check
    shellcheck scripts/*.sh
    gitleaks git --no-banner

# Validate the contract examples against their JSON Schemas
[group('dev')]
contracts:
    uvx check-jsonschema@0.39.0 --check-metaschema docs/contracts/*.schema.json
    uvx check-jsonschema@0.39.0 --schemafile docs/contracts/tf-review-config.schema.json \
      docs/contracts/examples/tf-review.yml
    uvx check-jsonschema@0.39.0 --schemafile docs/contracts/finding.schema.json \
      docs/contracts/examples/finding.json
    uvx check-jsonschema@0.39.0 --schemafile docs/contracts/corpus-frontmatter.schema.json \
      docs/contracts/examples/corpus-frontmatter.yml

# Validate the bootstrap project without touching its backend
[group('bootstrap')]
bootstrap-validate:
    tofu -chdir=bootstrap init -backend=false -lockfile=readonly -input=false
    tofu -chdir=bootstrap validate

# Plan the bootstrap project with your az login and the GitHub token from pass
[group('bootstrap')]
bootstrap-plan:
    #!/usr/bin/env bash
    set -euo pipefail
    GITHUB_TOKEN="$(pass show tf-review/github/bootstrap-token)"
    export GITHUB_TOKEN
    TF_VAR_subscription_id="$(az account show --query id -o tsv)"
    TF_VAR_budget_contact_email="$(git config user.email)"
    export TF_VAR_subscription_id TF_VAR_budget_contact_email
    tofu -chdir=bootstrap init -input=false
    tofu -chdir=bootstrap plan -input=false -out=bootstrap.tfplan

# Apply the reviewed plan from bootstrap-plan
[confirm('Apply bootstrap.tfplan to Azure and GitHub?')]
[group('bootstrap')]
bootstrap-apply:
    #!/usr/bin/env bash
    set -euo pipefail
    GITHUB_TOKEN="$(pass show tf-review/github/bootstrap-token)"
    export GITHUB_TOKEN
    tofu -chdir=bootstrap apply -input=false bootstrap.tfplan

# Move bootstrap's local state into the storage account (after backend.tf is added)
[confirm('Migrate the bootstrap state into Azure Storage?')]
[group('bootstrap')]
bootstrap-migrate:
    tofu -chdir=bootstrap init -migrate-state -input=false
