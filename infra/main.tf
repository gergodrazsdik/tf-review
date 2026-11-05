# Bootstrap creates this resource group and the identities' roles on it.
data "azurerm_resource_group" "target" {
  name = "rg-tf-review-prod"
}

# Bootstrap builds TF_REVIEW_OPENAI_ENDPOINT from this name, so changing it means changing
# foundry_account_name in bootstrap/repos.tf too.
resource "azurerm_cognitive_account" "foundry" {
  name                  = "ais-tf-review-prod"
  resource_group_name   = data.azurerm_resource_group.target.name
  location              = data.azurerm_resource_group.target.location
  kind                  = "AIServices"
  sku_name              = "S0"
  custom_subdomain_name = "ais-tf-review-prod"
  local_auth_enabled    = false

  # Projects need both of these. Turning project management off again replaces the account.
  project_management_enabled = true

  identity {
    type = "SystemAssigned"
  }

  tags = {
    owner       = "gergodrazsdik"
    environment = "prod"
    project     = "tf-review"
  }
}

# The new Foundry portal opens an account through a project. The review doesn't use it: it calls
# the account's endpoint directly.
resource "azurerm_cognitive_account_project" "main" {
  name                 = "proj-tf-review-prod"
  cognitive_account_id = azurerm_cognitive_account.foundry.id
  location             = data.azurerm_resource_group.target.location
  display_name         = "tf-review"

  identity {
    type = "SystemAssigned"
  }

  tags = {
    owner       = "gergodrazsdik"
    environment = "prod"
    project     = "tf-review"
  }
}

# Every role's model until 2026-11-05, kept as the fallback and to compare against. The pinned version stops at
# retirement (2027-09-21).
resource "azurerm_cognitive_deployment" "gpt_5_4_mini" {
  name                   = "gpt-5.4-mini"
  cognitive_account_id   = azurerm_cognitive_account.foundry.id
  version_upgrade_option = "NoAutoUpgrade"

  model {
    format  = "OpenAI"
    name    = "gpt-5.4-mini"
    version = "2026-03-17"
  }

  # In thousands of tokens per minute: only the evals and the smoke test call it now.
  sku {
    name     = "DataZoneStandard"
    capacity = 50
  }
}

# Every role's model from 2026-11-05: it passed every eval run three times over and suggested more than gpt-5.4-mini,
# at about 1.1 to 1.5 times the cost (D4, docs/evals/baseline.md). Bootstrap's TF_REVIEW_OPENAI_DEPLOYMENT holds this
# name too. The pinned version stops at retirement (2028-03-11).
resource "azurerm_cognitive_deployment" "gpt_6_1_sol" {
  name                   = "gpt-6.1-sol"
  cognitive_account_id   = azurerm_cognitive_account.foundry.id
  version_upgrade_option = "NoAutoUpgrade"
  depends_on             = [azurerm_cognitive_deployment.text_embedding_3_small]

  model {
    format  = "OpenAI"
    name    = "gpt-6.1-sol"
    version = "2026-09-29"
  }

  # In thousands of tokens per minute: all of the Tier 1 quota. A large plan's batches need this much (D54), and
  # TokenPacer keeps a review's calls under 90% of it. It's a rate limit, not a budget, which the budget alerts (D15)
  # cover: a runaway loop could spend about $15 to $70 an hour, by the mix of input and output.
  sku {
    name     = "DataZoneStandard"
    capacity = 100
  }
}
