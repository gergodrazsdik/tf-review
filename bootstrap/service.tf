# The review service's identity and what it can reach. Here and not in infra/, because the apply identity's
# Contributor can't assign roles (D24). Scoped to the resource group, because this project applies separately from
# infra/, which creates the resources, and these roles have to exist in either order.

locals {
  service_scope = azurerm_resource_group.target[local.foundry_repo].id

  service_roles = toset([
    "Cognitive Services OpenAI User",
    "Key Vault Secrets User",
    "Storage Blob Data Contributor",
    "Storage Queue Data Contributor",
  ])
}

# In the target resource group, so that the apply identity can attach it to the service's container apps.
resource "azurerm_user_assigned_identity" "service" {
  name                = "id-${local.foundry_repo}-service-prod"
  resource_group_name = azurerm_resource_group.target[local.foundry_repo].name
  location            = azurerm_resource_group.target[local.foundry_repo].location
  tags                = merge(local.common_tags, { project = local.foundry_repo })
}

resource "azurerm_role_assignment" "service" {
  for_each = local.service_roles

  scope                = local.service_scope
  role_definition_name = each.value
  principal_id         = azurerm_user_assigned_identity.service.principal_id
  principal_type       = "ServicePrincipal"
}

# `just secrets-sync` writes the service's secrets as the person running bootstrap.
resource "azurerm_role_assignment" "deployer_service_secrets" {
  scope                = local.service_scope
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

# Which repos the API serves (D40, D45): every onboarded repo, by the ID that survives a rename. infra/ gives this
# to the API when it deploys it. Repo IDs are public, so this needs no masking.
resource "github_actions_variable" "service_allowed_repository_ids" {
  repository    = local.foundry_repo
  variable_name = "TF_REVIEW_ALLOWED_REPOSITORY_IDS"
  value         = join(",", [for repo in sort(keys(var.repos)) : data.github_repository.repo[repo].repo_id])
}

# Builds the reference index, and nothing else holds these rights outside the service: the dry run and PRs can't reach
# it, because its credential only works from the corpus environment, which only main can use, and only once you approve
# (D44). infra/ gives it its Cosmos data role, because that needs the account.
resource "azurerm_user_assigned_identity" "index" {
  name                = "id-${local.foundry_repo}-index-prod"
  resource_group_name = azurerm_resource_group.state.name
  location            = azurerm_resource_group.state.location
  tags                = local.common_tags
}

resource "azurerm_federated_identity_credential" "index" {
  name                      = "github-environment-corpus"
  user_assigned_identity_id = azurerm_user_assigned_identity.index.id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = local.github_oidc_issuer
  subject                   = "${local.github_oidc_subject[local.foundry_repo]}:environment:corpus"
}

resource "azurerm_role_assignment" "index_embeddings" {
  scope                = local.service_scope
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_user_assigned_identity.index.principal_id
  principal_type       = "ServicePrincipal"
}

resource "github_repository_environment" "corpus" {
  repository  = local.foundry_repo
  environment = "corpus"

  reviewers {
    users = [data.github_user.owner.id]
  }

  deployment_branch_policy {
    protected_branches     = false
    custom_branch_policies = true
  }
}

resource "github_repository_environment_deployment_policy" "corpus_main" {
  repository     = local.foundry_repo
  environment    = github_repository_environment.corpus.environment
  branch_pattern = "main"
}

resource "github_actions_secret" "index_client_id" {
  repository  = local.foundry_repo
  secret_name = "AZURE_CLIENT_ID_INDEX"
  value       = azurerm_user_assigned_identity.index.client_id
}
