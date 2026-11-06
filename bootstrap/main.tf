data "azurerm_client_config" "current" {}

locals {
  common_tags = {
    owner       = var.github_owner
    environment = "prod"
    project     = "tf-review"
  }
}

resource "azurerm_resource_group" "state" {
  name     = "rg-tf-review-state-prod"
  location = var.location
  tags     = local.common_tags
}

resource "azurerm_storage_account" "state" {
  name                            = "sttfreviewstateprod"
  resource_group_name             = azurerm_resource_group.state.name
  location                        = azurerm_resource_group.state.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  https_traffic_only_enabled      = true
  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = false
  default_to_oauth_authentication = true
  tags                            = local.common_tags

  blob_properties {
    versioning_enabled = true

    delete_retention_policy {
      days = 30
    }

    container_delete_retention_policy {
      days = 30
    }
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_management_lock" "state" {
  name       = "state-cannot-delete"
  scope      = azurerm_storage_account.state.id
  lock_level = "CanNotDelete"
  notes      = "Holds the state of every onboarded repo."
}

resource "azurerm_storage_container" "bootstrap" {
  name                  = "tfstate-bootstrap"
  storage_account_id    = azurerm_storage_account.state.id
  container_access_type = "private"

  lifecycle {
    prevent_destroy = true
  }
}

# The person running bootstrap reads and writes its state in the account once it's migrated.
resource "azurerm_role_assignment" "deployer_state" {
  scope                = azurerm_storage_container.bootstrap.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_consumption_budget_subscription" "monthly" {
  name            = "budget-tf-review-monthly"
  subscription_id = "/subscriptions/${var.subscription_id}"
  amount          = var.budget_amount
  time_grain      = "Monthly"

  time_period {
    start_date = var.budget_start_date
  }

  dynamic "notification" {
    for_each = toset([50, 80, 100])

    content {
      enabled        = true
      threshold      = notification.value
      threshold_type = "Actual"
      operator       = "GreaterThanOrEqualTo"
      contact_emails = [var.budget_contact_email]
    }
  }

  # Actual cost lags by up to a day, so a forecast warns before the month's spend arrives.
  notification {
    enabled        = true
    threshold      = 100
    threshold_type = "Forecasted"
    operator       = "GreaterThanOrEqualTo"
    contact_emails = [var.budget_contact_email]
  }
}

# Where infra/'s alerts go. Here, because bootstrap already holds the address and keeps it out of the code; infra/
# refers to it by name (OPS-2).
resource "azurerm_monitor_action_group" "alerts" {
  name                = "ag-${local.foundry_repo}-prod"
  resource_group_name = azurerm_resource_group.target[local.foundry_repo].name
  short_name          = "tfreview"
  tags                = merge(local.common_tags, { project = local.foundry_repo })

  email_receiver {
    name                    = "owner"
    email_address           = var.budget_contact_email
    use_common_alert_schema = true
  }
}

# Previous state versions go a day after they're superseded. Every state is encrypted and enforced (D59), so this is
# what removes the plaintext versions written before, and keeps old versions from piling up. Recovery stays: soft
# delete keeps a deleted version restorable for 30 days.
resource "azurerm_storage_management_policy" "state" {
  storage_account_id = azurerm_storage_account.state.id

  rule {
    name    = "expire-previous-state-versions"
    enabled = true

    filters {
      blob_types = ["blockBlob"]
    }

    actions {
      version {
        delete_after_days_since_creation = 1
      }
    }
  }
}
