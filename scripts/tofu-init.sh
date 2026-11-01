#!/usr/bin/env bash
# Initializes infra/ against the repo's state container. Usage: scripts/tofu-init.sh
# Expects TF_STATE_RESOURCE_GROUP, TF_STATE_STORAGE_ACCOUNT and TF_STATE_CONTAINER (Actions variables).
set -euo pipefail

tofu -chdir=infra init -input=false -lockfile=readonly \
  -backend-config="resource_group_name=$TF_STATE_RESOURCE_GROUP" \
  -backend-config="storage_account_name=$TF_STATE_STORAGE_ACCOUNT" \
  -backend-config="container_name=$TF_STATE_CONTAINER" \
  -backend-config="key=infra.tfstate" \
  -backend-config="use_azuread_auth=true"
