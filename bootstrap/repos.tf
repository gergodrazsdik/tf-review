locals {
  github_oidc_issuer = "https://token.actions.githubusercontent.com"

  # Variable name => value, per repo.
  repo_variables = merge([
    for repo in keys(var.repos) : {
      for name, value in {
        AZURE_CLIENT_ID_PLAN     = azurerm_user_assigned_identity.plan[repo].client_id
        AZURE_CLIENT_ID_APPLY    = azurerm_user_assigned_identity.apply[repo].client_id
        AZURE_TENANT_ID          = data.azurerm_client_config.current.tenant_id
        AZURE_SUBSCRIPTION_ID    = var.subscription_id
        TF_STATE_RESOURCE_GROUP  = azurerm_resource_group.state.name
        TF_STATE_STORAGE_ACCOUNT = azurerm_storage_account.state.name
        TF_STATE_CONTAINER       = azurerm_storage_container.repo[repo].name
      } : "${repo}/${name}" => { repo = repo, name = name, value = value }
    }
  ]...)
}

resource "azurerm_resource_group" "target" {
  for_each = var.repos

  name     = each.value.target_resource_group
  location = var.location
  tags     = merge(local.common_tags, { project = each.key })
}

resource "azurerm_storage_container" "repo" {
  for_each = var.repos

  name                  = "tfstate-${each.key}"
  storage_account_id    = azurerm_storage_account.state.id
  container_access_type = "private"

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_user_assigned_identity" "plan" {
  for_each = var.repos

  name                = "id-${each.key}-plan-prod"
  resource_group_name = azurerm_resource_group.state.name
  location            = azurerm_resource_group.state.location
  tags                = local.common_tags
}

resource "azurerm_user_assigned_identity" "apply" {
  for_each = var.repos

  name                = "id-${each.key}-apply-prod"
  resource_group_name = azurerm_resource_group.state.name
  location            = azurerm_resource_group.state.location
  tags                = local.common_tags
}

resource "azurerm_federated_identity_credential" "plan" {
  for_each = var.repos

  name                      = "github-pull-request"
  user_assigned_identity_id = azurerm_user_assigned_identity.plan[each.key].id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = local.github_oidc_issuer
  subject                   = "repo:${var.github_owner}/${each.key}:pull_request"
}

resource "azurerm_federated_identity_credential" "apply" {
  for_each = var.repos

  name                      = "github-environment-prod"
  user_assigned_identity_id = azurerm_user_assigned_identity.apply[each.key].id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = local.github_oidc_issuer
  subject                   = "repo:${var.github_owner}/${each.key}:environment:prod"
}

resource "azurerm_role_assignment" "plan_target" {
  for_each = var.repos

  scope                = azurerm_resource_group.target[each.key].id
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.plan[each.key].principal_id
}

resource "azurerm_role_assignment" "apply_target" {
  for_each = var.repos

  scope                = azurerm_resource_group.target[each.key].id
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.apply[each.key].principal_id
}

resource "azurerm_role_assignment" "plan_state" {
  for_each = var.repos

  scope                = azurerm_storage_container.repo[each.key].id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.plan[each.key].principal_id
}

resource "azurerm_role_assignment" "apply_state" {
  for_each = var.repos

  scope                = azurerm_storage_container.repo[each.key].id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.apply[each.key].principal_id
}

resource "github_actions_variable" "repo" {
  for_each = local.repo_variables

  repository    = each.value.repo
  variable_name = each.value.name
  value         = each.value.value
}

data "github_user" "owner" {
  username = var.github_owner
}

resource "github_repository_environment" "prod" {
  for_each = var.repos

  repository  = each.key
  environment = "prod"

  reviewers {
    users = [data.github_user.owner.id]
  }

  deployment_branch_policy {
    protected_branches     = false
    custom_branch_policies = true
  }
}

resource "github_repository_environment_deployment_policy" "prod_main" {
  for_each = var.repos

  repository     = each.key
  environment    = github_repository_environment.prod[each.key].environment
  branch_pattern = "main"
}
