# uv run, not uvx: uvx writes to ~/.local/share/uv/tools, which is read-only in the Claude sandbox
check_jsonschema := "uv run --no-project --with check-jsonschema==0.39.0 check-jsonschema"

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

# Run the checks that need no plugins, so they also work in the Claude sandbox; CI runs this too
[group('dev')]
lint: contracts
    tofu fmt -check -recursive .
    yamllint --strict .
    actionlint
    pinact run --check
    shellcheck scripts/*.sh
    gitleaks git --no-banner

# Run the checks that start provider or ruleset plugins, which can't run in the Claude sandbox; CI runs this too
[group('dev')]
validate: bootstrap-validate infra-validate
    tflint --init
    tflint --recursive --config "$PWD/.tflint.hcl"

# Build, then run each test project; dotnet test can't run from a recipe in the Claude sandbox, so only CI uses it
[group('dev')]
test:
    dotnet build -m:1
    dotnet run --project tests/TfReview.Agents.Tests --no-build
    dotnet run --project tests/TfReview.Core.Tests --no-build
    dotnet run --project tests/TfReview.GitHub.Tests --no-build
    TF_STANDARDS_DIR="{{ justfile_directory() }}/../tf-standards" dotnet run --project tests/TfReview.Rag.Tests --no-build
    TF_STANDARDS_DIR="{{ justfile_directory() }}/../tf-standards" dotnet run --project tests/TfReview.Scanners.Tests --no-build

# Build the review image with podman; it can't run from a recipe in the Claude sandbox
[group('dev')]
image:
    podman build --format docker -t tf-review:dev -f Containerfile .

# Check C# formatting and style in a way that works in the Claude sandbox; CI runs the full dotnet format
[group('dev')]
lint-dotnet:
    dotnet format whitespace --folder --verify-no-changes
    dotnet build -m:1 --no-incremental -p:EnforceCodeStyleInBuild=true -warnaserror

# Validate the contract examples against their JSON Schemas
[group('dev')]
contracts:
    {{ check_jsonschema }} --check-metaschema docs/contracts/*.schema.json
    {{ check_jsonschema }} --schemafile docs/contracts/tf-review-config.schema.json \
      docs/contracts/examples/tf-review.yml
    {{ check_jsonschema }} --schemafile docs/contracts/finding.schema.json \
      docs/contracts/examples/finding.json
    {{ check_jsonschema }} --schemafile docs/contracts/corpus-frontmatter.schema.json \
      docs/contracts/examples/corpus-frontmatter.yml
    {{ check_jsonschema }} --schemafile docs/contracts/review-result.schema.json \
      docs/contracts/examples/review-result.json
    uv run --quiet scripts/check_contract_cases.py

# Validate the infra project without touching its backend
[group('dev')]
infra-validate:
    tofu -chdir=infra init -backend=false -lockfile=readonly -input=false
    tofu -chdir=infra validate

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
    tofu -chdir=bootstrap init -migrate-state -force-copy -input=false
