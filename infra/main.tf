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

  # In thousands of tokens per minute. Below the 200K quota, to limit how fast a runaway loop can
  # spend: $5 to $30 an hour at most, by the mix of input and output; it's a rate limit, not a
  # budget, which the budget alerts (D15) cover. A large plan's batches need
  # this much (D54), and TokenPacer keeps a review's calls under 90% of it.
  sku {
    name     = "DataZoneStandard"
    capacity = 100
  }
}

# Candidates from the Tier 1 quota (2026-11-05, 100K TPM each in Data Zone Standard), compared with gpt-5.4-mini on the
# eval suite before any agent uses them (D4). Nothing calls them but the evals and the smoke test, which pick them by
# name. Half the quota each limits what a loop could spend; a candidate that isn't adopted is removed again.
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

  sku {
    name     = "DataZoneStandard"
    capacity = 50
  }
}

resource "azurerm_cognitive_deployment" "gpt_6_luna" {
  name                   = "gpt-6-luna"
  cognitive_account_id   = azurerm_cognitive_account.foundry.id
  version_upgrade_option = "NoAutoUpgrade"
  depends_on             = [azurerm_cognitive_deployment.gpt_6_1_sol]

  model {
    format  = "OpenAI"
    name    = "gpt-6-luna"
    version = "2026-09-22"
  }

  sku {
    name     = "DataZoneStandard"
    capacity = 50
  }
}
