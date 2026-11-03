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
    dotnet run --project tests/TfReview.Api.Tests --no-build
    dotnet run --project tests/TfReview.Core.Tests --no-build
    dotnet run --project tests/TfReview.Corpus.Tests --no-build
    dotnet run --project tests/TfReview.GitHub.Tests --no-build
    TF_STANDARDS_DIR="{{ justfile_directory() }}/../tf-standards" dotnet run --project tests/TfReview.Rag.Tests --no-build
    TF_STANDARDS_DIR="{{ justfile_directory() }}/../tf-standards" dotnet run --project tests/TfReview.Scanners.Tests --no-build
    dotnet run --project tests/TfReview.Worker.Tests --no-build

# Replay recorded model answers over the seeded cases: offline and free, so it proves only the code around the model
[group('dev')]
e2e:
    dotnet build -m:1 tests/TfReview.Eval
    TF_STANDARDS_DIR="{{ justfile_directory() }}/../tf-standards" dotnet run --project tests/TfReview.Eval --no-build -- replay

# Run the seeded cases against the real model; it costs cents, and CI's e2e-live workflow is the usual way
[group('dev')]
e2e-live runs="3":
    dotnet build -m:1 tests/TfReview.Eval
    TF_STANDARDS_DIR="{{ justfile_directory() }}/../tf-standards" dotnet run --project tests/TfReview.Eval --no-build -- live --runs {{ runs }}

# Refresh the eval fixtures from the seeded branches' redacted plans; run it outside the Claude sandbox
[group('dev')]
fixtures *cases:
    scripts/fixtures.sh {{ cases }}

# Import the sources in corpus/sources.yml and report what they come to; needs no credentials and writes nothing to Azure
[group('dev')]
corpus-import output="artifacts/chunks.jsonl":
    dotnet run --project src/TfReview.Corpus -- import corpus/sources.yml "{{ output }}"

# Show what a review's references would be, from the index in use, as you; read-only, and costs a fraction of a cent
[group('dev')]
corpus-search text types version="5.12.0" ranking="hybrid":
    AZURE_TOKEN_CREDENTIALS=dev TF_REVIEW_COSMOS_ENDPOINT=https://cosmos-tf-review-prod.documents.azure.com:443/ \
      TF_REVIEW_OPENAI_ENDPOINT=https://ais-tf-review-prod.openai.azure.com/openai/v1/ \
      dotnet run --project src/TfReview.Corpus -- search "{{ text }}" "{{ types }}" "{{ version }}" "{{ ranking }}"

# Compare what the real service posted on the demo's seed PRs with what's expected; pass --rerun to review them again, which costs cents
[group('dev')]
service-eval *args:
    python3 scripts/service_eval.py {{ args }}

# Save what real Checkov and Infracost say about the named eval cases, or all; needs the token from pass, so it's yours to run
[group('dev')]
record-scans *cases:
    #!/usr/bin/env bash
    set -euo pipefail
    unset SSH_AUTH_SOCK
    INFRACOST_CLI_AUTHENTICATION_TOKEN="$(pass show tf-review/infracost/cli-token)"
    export INFRACOST_CLI_AUTHENTICATION_TOKEN
    dotnet build -m:1 tests/TfReview.Eval
    TF_STANDARDS_DIR="{{ justfile_directory() }}/../tf-standards" dotnet run --project tests/TfReview.Eval --no-build -- record-scans {{ cases }}

# Price a plan JSON with the token from pass for this run only; pinentry may prompt
[group('dev')]
cost plan:
    #!/usr/bin/env bash
    set -euo pipefail
    unset SSH_AUTH_SOCK
    INFRACOST_CLI_AUTHENTICATION_TOKEN="$(pass show tf-review/infracost/cli-token)"
    export INFRACOST_CLI_AUTHENTICATION_TOKEN
    infracost scan "{{ plan }}" --json --no-color --currency USD

# Regenerate conftest's capabilities after moving to a new conftest: pass the OPA version `conftest --version` reports
[group('dev')]
conftest-capabilities opa_version:
    opa capabilities --version v{{ opa_version }} | jq -c 'del(.builtins[] | select(.name == "http.send" or .name == "opa.runtime" or .name == "net.lookup_ip_addr")) | .allow_net = []' > src/TfReview.Scanners/conftest-capabilities.json

# Copy the Infracost token and the GitHub App's key from pass into Key Vault, for the service; yours to run, never an agent's
[confirm('Copy the Infracost token and the GitHub App key into Key Vault?')]
[group('bootstrap')]
secrets-sync:
    #!/usr/bin/env bash
    set -euo pipefail
    token="$(pass show tf-review/infracost/cli-token)"
    key="$(pass show tf-review/github-app/private-key)"
    # From stdin so values stay out of the process list, and no output because az prints the value it set. Not
    # with <<<, which adds a newline: az stores it, and a token with one can't be sent as a header.
    printf '%s' "$token" | az keyvault secret set --vault-name kv-tf-review-prod --name infracost-cli-token --file /dev/stdin --output none
    echo "Key Vault: infracost-cli-token set"
    printf '%s' "$key" | az keyvault secret set --vault-name kv-tf-review-prod --name github-app-private-key --file /dev/stdin --output none
    echo "Key Vault: github-app-private-key set"

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
    {{ check_jsonschema }} --schemafile docs/contracts/reference-chunk.schema.json \
      docs/contracts/examples/reference-chunk.json
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
