locals {
  github_oidc_issuer = "https://token.actions.githubusercontent.com"

  # GitHub's immutable OIDC subject: numeric IDs keep a re-registered name from matching.
  github_oidc_subject = {
    for repo in keys(var.repos) :
    repo => "repo:${var.github_owner}@${data.github_user.owner.id}/${repo}@${data.github_repository.repo[repo].repo_id}"
  }

  # The Foundry resource lives in this repo's target resource group. infra/ creates it with
  # these names, so changing one here means changing it there too.
  foundry_repo         = "tf-review"
  foundry_account_name = "ais-tf-review-prod"
  foundry_deployment   = "gpt-6.1-sol"

  # What calls the model directly: only tf-review's evals and smoke test, because the service reviews
  # everything else (D38).
  llm_callers = toset([local.foundry_repo])

  # Secret name => value, per repo. These IDs aren't credentials, but Actions logs on public repos
  # are public, and only secrets get masked there, including inside tofu's resource IDs (D30).
  repo_secrets = merge(concat([
    for repo in keys(var.repos) : {
      for name, value in {
        AZURE_CLIENT_ID_PLAN  = azurerm_user_assigned_identity.plan[repo].client_id
        AZURE_CLIENT_ID_APPLY = azurerm_user_assigned_identity.apply[repo].client_id
        AZURE_TENANT_ID       = data.azurerm_client_config.current.tenant_id
        AZURE_SUBSCRIPTION_ID = var.subscription_id
      } : "${repo}/${name}" => { repo = repo, name = name, value = value }
    }
    ], [
    for repo in local.llm_callers : {
      "${repo}/AZURE_CLIENT_ID_REVIEW" = {
        repo  = repo
        name  = "AZURE_CLIENT_ID_REVIEW"
        value = azurerm_user_assigned_identity.review[repo].client_id
      }
    }
    ], [
    # The required variables of a project the reusable workflow plans for review: today only tf-review's own infra/,
    # which infra.yml gives the same values. A secret, because the tenant ID is one (D30).
    {
      "${local.foundry_repo}/PLAN_VARIABLES" = {
        repo = local.foundry_repo
        name = "PLAN_VARIABLES"
        value = jsonencode({
          tenant_id              = data.azurerm_client_config.current.tenant_id
          allowed_repository_ids = local.allowed_repository_ids
        })
      }
    }
  ])...)

  # Variable name => value, per repo.
  repo_variables = merge(concat([
    for repo in keys(var.repos) : {
      for name, value in {
        TF_STATE_RESOURCE_GROUP  = azurerm_resource_group.state.name
        TF_STATE_STORAGE_ACCOUNT = azurerm_storage_account.state.name
        TF_STATE_CONTAINER       = azurerm_storage_container.repo[repo].name
      } : "${repo}/${name}" => { repo = repo, name = name, value = value }
    }
    ], [
    for repo in local.llm_callers : {
      for name, value in {
        TF_REVIEW_OPENAI_ENDPOINT   = "https://${local.foundry_account_name}.openai.azure.com/openai/v1/"
        TF_REVIEW_OPENAI_DEPLOYMENT = local.foundry_deployment
      } : "${repo}/${name}" => { repo = repo, name = name, value = value }
    }
  ])...)
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

# Calls the LLM from tf-review's evals and smoke test, in the review environment. It gets no state or
# target access, so a plan job's PR code can't reach the LLM through its own identity (D21). Repos are
# reviewed through the service, so this isn't one per repo any more (D38).
resource "azurerm_user_assigned_identity" "review" {
  for_each = local.llm_callers

  name                = "id-${each.key}-review-prod"
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
  subject                   = "${local.github_oidc_subject[each.key]}:pull_request"
}

# Plans main before an apply, and weekly for drift, so the apply's approval comes after a plan you can read.
resource "azurerm_federated_identity_credential" "plan_main" {
  for_each = var.repos

  name                      = "github-main"
  user_assigned_identity_id = azurerm_user_assigned_identity.plan[each.key].id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = local.github_oidc_issuer
  subject                   = "${local.github_oidc_subject[each.key]}:ref:refs/heads/main"
}

resource "azurerm_federated_identity_credential" "apply" {
  for_each = var.repos

  name                      = "github-environment-prod"
  user_assigned_identity_id = azurerm_user_assigned_identity.apply[each.key].id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = local.github_oidc_issuer
  subject                   = "${local.github_oidc_subject[each.key]}:environment:prod"
}

resource "azurerm_federated_identity_credential" "review" {
  for_each = local.llm_callers

  name                      = "github-environment-review"
  user_assigned_identity_id = azurerm_user_assigned_identity.review[each.key].id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = local.github_oidc_issuer
  subject                   = "${local.github_oidc_subject[each.key]}:environment:review"
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

# Read-only: PR plans run without the state lock (D48), so they never write state. With write access, PR code could
# store a forged state that the next apply acts on.
resource "azurerm_role_assignment" "plan_state" {
  for_each = var.repos

  scope                = azurerm_storage_container.repo[each.key].id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = azurerm_user_assigned_identity.plan[each.key].principal_id
}

resource "azurerm_role_assignment" "apply_state" {
  for_each = var.repos

  scope                = azurerm_storage_container.repo[each.key].id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.apply[each.key].principal_id
}

# Here and not in infra/, because the apply identity's Contributor can't assign roles (D24).
resource "azurerm_role_assignment" "review_llm" {
  for_each = local.llm_callers

  scope                = azurerm_resource_group.target[local.foundry_repo].id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_user_assigned_identity.review[each.key].principal_id
}

# The person running bootstrap also plans from the laptop (just plan, just fixtures).
resource "azurerm_role_assignment" "deployer_repo_state" {
  for_each = var.repos

  scope                = azurerm_storage_container.repo[each.key].id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "github_actions_secret" "repo" {
  for_each = local.repo_secrets

  repository  = each.value.repo
  secret_name = each.value.name
  value       = each.value.value
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

data "github_repository" "repo" {
  for_each = var.repos

  full_name = "${var.github_owner}/${each.key}"
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

# No reviewers or branch policy: it runs on PRs. The API accepts only this environment's tokens, which
# the plan job doesn't have (D45), and it scopes the eval identity's federated credential in tf-review.
# Anyone who can edit a workflow can still use it, but D45's other checks refuse such a job.
resource "github_repository_environment" "review" {
  for_each = var.repos

  repository  = each.key
  environment = "review"
}

resource "github_repository_environment_deployment_policy" "prod_main" {
  for_each = var.repos

  repository     = each.key
  environment    = github_repository_environment.prod[each.key].environment
  branch_pattern = "main"
}
