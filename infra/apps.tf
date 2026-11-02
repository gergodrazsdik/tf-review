# The review service: an API that scales to zero and a job that only exists while a message is on the queue, so
# neither costs anything between PRs (D16). Both run the released image, with the identity from bootstrap.

variable "allowed_repository_ids" {
  description = "Repos the API serves, as comma-separated IDs. Bootstrap's repos map sets the Actions variable this comes from (D40)."
  type        = string
}

locals {
  # image-v0.0.9. A release updates this with the reusable workflow's digest.
  service_image = "ghcr.io/gergodrazsdik/tf-review@sha256:21bcd484dc1b967d0500d42049943ba34295f1b85d4f15a6a1eb9efdec01e6e5"

  # The commits callers may pin the reusable workflow to (D45).
  released_commits = [
    for line in split("\n", file("${path.module}/released-commits.txt")) : trimspace(line)
    if trimspace(line) != "" && !startswith(trimspace(line), "#")
  ]

  service_storage = {
    TF_REVIEW_STORAGE_ACCOUNT = azurerm_storage_account.service.name
    TF_REVIEW_COSMOS_ENDPOINT = "https://${azapi_resource.cosmos.name}.documents.azure.com:443/"
  }

  # The image's filesystem isn't the place for anything these write.
  service_home = { HOME = "/tmp" }
}

# Bootstrap creates this, because the apply identity can't assign roles (D24).
data "azurerm_user_assigned_identity" "service" {
  name                = "id-tf-review-service-prod"
  resource_group_name = data.azurerm_resource_group.target.name
}

# The one data role bootstrap can't give, because it needs the account to exist. Built-in Data Contributor.
resource "azurerm_cosmosdb_sql_role_assignment" "service" {
  resource_group_name = data.azurerm_resource_group.target.name
  account_name        = azapi_resource.cosmos.name
  role_definition_id  = "${azapi_resource.cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
  principal_id        = sensitive(data.azurerm_user_assigned_identity.service.principal_id)
  scope               = azapi_resource.cosmos.id
}

resource "azurerm_container_app" "api" {
  name                         = "ca-tf-review-api-prod"
  resource_group_name          = data.azurerm_resource_group.target.name
  container_app_environment_id = azurerm_container_app_environment.service.id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = local.service_tags

  identity {
    type         = "UserAssigned"
    identity_ids = [data.azurerm_user_assigned_identity.service.id]
  }

  ingress {
    external_enabled = true
    target_port      = 8080

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 0
    max_replicas = 1

    container {
      name   = "api"
      image  = local.service_image
      args   = ["serve"]
      cpu    = 0.5
      memory = "1Gi"

      dynamic "env" {
        for_each = merge(local.service_storage, local.service_home, {
          TF_REVIEW_ALLOWED_REPOSITORY_IDS = var.allowed_repository_ids
          TF_REVIEW_RELEASED_COMMITS       = join(",", local.released_commits)
        })

        content {
          name  = env.key
          value = env.value
        }
      }

      # Client IDs stay out of the public logs (D30).
      env {
        name  = "AZURE_CLIENT_ID"
        value = sensitive(data.azurerm_user_assigned_identity.service.client_id)
      }
    }
  }
}

resource "azurerm_container_app_job" "review" {
  name                         = "caj-tf-review-prod"
  resource_group_name          = data.azurerm_resource_group.target.name
  location                     = data.azurerm_resource_group.target.location
  container_app_environment_id = azurerm_container_app_environment.service.id
  workload_profile_name        = "Consumption"
  tags                         = local.service_tags

  # A bit over the worker's own 14 minute deadline, which leaves it time to say "not reviewed", and under the 20
  # minutes a message stays hidden. The queue does the retrying, so the job doesn't.
  replica_timeout_in_seconds = 900
  replica_retry_limit        = 0

  identity {
    type         = "UserAssigned"
    identity_ids = [data.azurerm_user_assigned_identity.service.id]
  }

  event_trigger_config {
    parallelism              = 1
    replica_completion_count = 1

    scale {
      min_executions              = 0
      max_executions              = 3
      polling_interval_in_seconds = 30

      rules {
        name             = "reviews-queue"
        custom_rule_type = "azure-queue"
        identity_id      = data.azurerm_user_assigned_identity.service.id
        metadata = {
          accountName = azurerm_storage_account.service.name
          queueName   = azurerm_storage_queue.reviews.name
          queueLength = "1"
        }
      }
    }
  }

  template {
    container {
      name   = "review"
      image  = local.service_image
      args   = ["work"]
      cpu    = 1
      memory = "2Gi"

      dynamic "env" {
        for_each = merge(local.service_storage, local.service_home, {
          TF_REVIEW_KEY_VAULT            = azurerm_key_vault.service.name
          TF_REVIEW_GITHUB_APP_CLIENT_ID = "Iv23lilG16KleMoC5ReP"
          TF_REVIEW_OPENAI_ENDPOINT      = "https://${azurerm_cognitive_account.foundry.custom_subdomain_name}.openai.azure.com/openai/v1/"
          TF_REVIEW_OPENAI_DEPLOYMENT    = azurerm_cognitive_deployment.gpt_5_4_mini.name
        })

        content {
          name  = env.key
          value = env.value
        }
      }

      env {
        name  = "AZURE_CLIENT_ID"
        value = sensitive(data.azurerm_user_assigned_identity.service.client_id)
      }
    }
  }
}

output "api_url" {
  description = "Where the reusable workflow submits reviews."
  value       = "https://${azurerm_container_app.api.ingress[0].fqdn}"
}
