# The review service's supporting resources (M3). Nothing here runs between PRs: the API and the review job
# arrive with the service itself, and scale to zero, so these only cost what they store and log.

locals {
  service_tags = {
    owner       = "gergodrazsdik"
    environment = "prod"
    project     = "tf-review"
  }
}

# Holds the review queue and redacted plans. Access keys are off, so only identities with a role can reach it.
resource "azurerm_storage_account" "service" {
  name                            = "sttfreviewsvcprod"
  resource_group_name             = data.azurerm_resource_group.target.name
  location                        = data.azurerm_resource_group.target.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  https_traffic_only_enabled      = true
  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = false
  tags                            = local.service_tags

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_storage_queue" "reviews" {
  name               = "reviews"
  storage_account_id = azurerm_storage_account.service.id

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_storage_container" "plans" {
  name                  = "plans"
  storage_account_id    = azurerm_storage_account.service.id
  container_access_type = "private"

  lifecycle {
    prevent_destroy = true
  }
}

# Redacted plans only need to last until the review is done, with some days to look into a failure.
resource "azurerm_storage_management_policy" "plans" {
  storage_account_id = azurerm_storage_account.service.id

  rule {
    name    = "delete-old-plans"
    enabled = true

    filters {
      prefix_match = ["plans/"]
      blob_types   = ["blockBlob"]
    }

    actions {
      base_blob {
        delete_after_days_since_creation_greater_than = 7
      }
    }
  }
}

# Holds the GitHub App's private key and the Infracost token. `just secrets-sync` writes them.
resource "azurerm_key_vault" "service" {
  name                       = "kv-tf-review-prod"
  resource_group_name        = data.azurerm_resource_group.target.name
  location                   = data.azurerm_resource_group.target.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = false
  soft_delete_retention_days = 7
  tags                       = local.service_tags

  lifecycle {
    prevent_destroy = true
  }
}

data "azurerm_client_config" "current" {}

resource "azurerm_log_analytics_workspace" "service" {
  name                = "log-tf-review-prod"
  resource_group_name = data.azurerm_resource_group.target.name
  location            = data.azurerm_resource_group.target.location
  sku                 = "PerGB2018"
  retention_in_days   = 30

  # A cap in GB a day, because logs are the one cost here that can run away.
  daily_quota_gb = 0.2
  tags           = local.service_tags
}

resource "azurerm_application_insights" "service" {
  name                = "appi-tf-review-prod"
  resource_group_name = data.azurerm_resource_group.target.name
  location            = data.azurerm_resource_group.target.location
  workspace_id        = azurerm_log_analytics_workspace.service.id
  application_type    = "other"
  tags                = local.service_tags
}

# Consumption only. A planned maintenance window or a private endpoint would add a fixed fee (D16).
resource "azurerm_container_app_environment" "service" {
  name                       = "cae-tf-review-prod"
  resource_group_name        = data.azurerm_resource_group.target.name
  location                   = data.azurerm_resource_group.target.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.service.id
  tags                       = local.service_tags
}

# The subscription's one free account, which can only be chosen when it's created. Access keys are off, and
# vector search can't use shared throughput, so each container gets its own part of the free 1,000 RU/s.
resource "azurerm_cosmosdb_account" "service" {
  name                         = "cosmos-tf-review-prod"
  resource_group_name          = data.azurerm_resource_group.target.name
  location                     = data.azurerm_resource_group.target.location
  offer_type                   = "Standard"
  kind                         = "GlobalDocumentDB"
  free_tier_enabled            = true
  local_authentication_enabled = false
  tags                         = local.service_tags

  capabilities {
    name = "EnableNoSQLVectorSearch"
  }

  consistency_policy {
    consistency_level = "Session"
  }

  geo_location {
    location          = data.azurerm_resource_group.target.location
    failover_priority = 0
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_cosmosdb_sql_database" "service" {
  name                = "tfreview"
  resource_group_name = data.azurerm_resource_group.target.name
  account_name        = azurerm_cosmosdb_account.service.name

  lifecycle {
    prevent_destroy = true
  }
}

# Review status and history. The vector container arrives with retrieval, and takes the remaining 600 RU/s. It
# can't be declared with azurerm 5.12, which has no vector policy on a container (see docs/plan.md).
resource "azurerm_cosmosdb_sql_container" "reviews" {
  name                = "reviews"
  resource_group_name = data.azurerm_resource_group.target.name
  account_name        = azurerm_cosmosdb_account.service.name
  database_name       = azurerm_cosmosdb_sql_database.service.name
  partition_key_paths = ["/repository"]
  throughput          = 400

  lifecycle {
    prevent_destroy = true
  }
}

# Data Zone, like the chat model, so text stays in the EU. Capacity is in thousands of tokens per minute, and
# limits how fast indexing can spend. Deployments on one account have to be created one at a time.
resource "azurerm_cognitive_deployment" "text_embedding_3_small" {
  name                   = "text-embedding-3-small"
  cognitive_account_id   = azurerm_cognitive_account.foundry.id
  version_upgrade_option = "NoAutoUpgrade"
  depends_on             = [azurerm_cognitive_deployment.gpt_5_4_mini]

  model {
    format  = "OpenAI"
    name    = "text-embedding-3-small"
    version = "1"
  }

  sku {
    name     = "DataZoneStandard"
    capacity = 120
  }
}
