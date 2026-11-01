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

# Bootstrap's TF_REVIEW_OPENAI_DEPLOYMENT holds this name too. The pinned version stops at
# retirement (2027-09-21), so the model moves through the eval suite before then.
resource "azurerm_cognitive_deployment" "gpt_5_4_mini" {
  name                   = "gpt-5.4-mini"
  cognitive_account_id   = azurerm_cognitive_account.foundry.id
  version_upgrade_option = "NoAutoUpgrade"

  model {
    format  = "OpenAI"
    name    = "gpt-5.4-mini"
    version = "2026-03-17"
  }

  # In thousands of tokens per minute. Well below the 200K quota, to limit how fast a runaway
  # loop can spend; it's a rate limit, not a budget.
  sku {
    name     = "DataZoneStandard"
    capacity = 30
  }
}
