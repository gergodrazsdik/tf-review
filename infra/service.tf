# The review service's supporting resources (M3). Nothing here runs between PRs: the API and the review job
# arrive with the service itself, and scale to zero, so these only cost what they store and log.

variable "tenant_id" {
  description = "Entra tenant, for Key Vault. A variable because the azurerm_client_config data source prints an ID that encodes the subscription, tenant and client IDs in base64, which log masking can't catch (D30)."
  type        = string
}

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
  tenant_id                  = var.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = false
  soft_delete_retention_days = 7
  tags                       = local.service_tags

  lifecycle {
    prevent_destroy = true
  }
}

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
  logs_destination           = "log-analytics"
  log_analytics_workspace_id = azurerm_log_analytics_workspace.service.id
  tags                       = local.service_tags

  # Azure adds this profile itself, and it has no fixed fee. Left out, every plan would try to remove it.
  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
    minimum_count         = 0
    maximum_count         = 0
  }
}

# The subscription's one free account, which can only be chosen when it's created. Access keys are off, and vector
# search can't use shared throughput, so each container gets its own part of the free 1,000 RU/s. In azapi because
# azurerm lists the account's keys on every read, which the plan identity can't do and shouldn't (D46).
resource "azapi_resource" "cosmos" {
  type                   = "Microsoft.DocumentDB/databaseAccounts@2025-10-15"
  name                   = "cosmos-tf-review-prod"
  parent_id              = data.azurerm_resource_group.target.id
  location               = data.azurerm_resource_group.target.location
  tags                   = local.service_tags
  response_export_values = []

  body = {
    kind = "GlobalDocumentDB"
    properties = {
      databaseAccountOfferType = "Standard"
      enableFreeTier           = true
      disableLocalAuth         = true
      minimalTlsVersion        = "Tls12"
      capabilities             = [{ name = "EnableNoSQLVectorSearch" }]
      consistencyPolicy        = { defaultConsistencyLevel = "Session" }
      locations = [{
        locationName     = "Germany West Central"
        failoverPriority = 0
      }]
    }
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_cosmosdb_sql_database" "service" {
  name                = "tfreview"
  resource_group_name = data.azurerm_resource_group.target.name
  account_name        = azapi_resource.cosmos.name

  lifecycle {
    prevent_destroy = true
  }
}

# Review status and history. The vector container arrives with retrieval and takes the remaining 600 RU/s, through
# azapi, because azurerm 5.12 has no vector policy on a container (D46).
resource "azurerm_cosmosdb_sql_container" "reviews" {
  name                = "reviews"
  resource_group_name = data.azurerm_resource_group.target.name
  account_name        = azapi_resource.cosmos.name
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
