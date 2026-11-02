# The review service: an API that scales to zero and a job that only exists while a message is on the queue, so
# neither costs anything between PRs (D16). Both run the released image, with the identity from bootstrap.

variable "allowed_repository_ids" {
  description = "Repos the API serves, as comma-separated IDs. Bootstrap's repos map sets the Actions variable this comes from (D40)."
  type        = string
}

locals {
  # image-v0.0.10. A release updates this with the reusable workflow's digest.
  service_image = "ghcr.io/gergodrazsdik/tf-review@sha256:da37a98d31628cc573f98d3d30991bc2709609f823fadef0295c5f85f444ce94"

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

  # Client IDs stay out of the public logs (D30).
  service_client_id = {
    name  = "AZURE_CLIENT_ID"
    value = sensitive(data.azurerm_user_assigned_identity.service.client_id)
  }
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

# The apps use azapi, because azurerm lists an app's secrets on every read, which the plan identity can't do and
# shouldn't (D46). Neither app has any: the worker reads Key Vault itself (D47).
resource "azapi_resource" "api" {
  type                   = "Microsoft.App/containerApps@2025-07-01"
  name                   = "ca-tf-review-api-prod"
  parent_id              = data.azurerm_resource_group.target.id
  location               = data.azurerm_resource_group.target.location
  tags                   = local.service_tags
  response_export_values = ["properties.configuration.ingress.fqdn"]

  identity {
    type         = "UserAssigned"
    identity_ids = [data.azurerm_user_assigned_identity.service.id]
  }

  body = {
    properties = {
      environmentId       = azurerm_container_app_environment.service.id
      workloadProfileName = "Consumption"
      configuration = {
        activeRevisionsMode = "Single"
        ingress = {
          external   = true
          targetPort = 8080
          traffic    = [{ latestRevision = true, weight = 100 }]
        }
      }
      template = {
        scale = { minReplicas = 0, maxReplicas = 1 }
        containers = [{
          name      = "api"
          image     = local.service_image
          args      = ["serve"]
          resources = { cpu = 0.5, memory = "1Gi" }
          env = concat(
            [for name, value in merge(local.service_storage, local.service_home, {
              TF_REVIEW_ALLOWED_REPOSITORY_IDS = var.allowed_repository_ids
              TF_REVIEW_RELEASED_COMMITS       = join(",", local.released_commits)
            }) : { name = name, value = value }],
            [local.service_client_id],
          )
        }]
      }
    }
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "azapi_resource" "review_job" {
  type                   = "Microsoft.App/jobs@2025-07-01"
  name                   = "caj-tf-review-prod"
  parent_id              = data.azurerm_resource_group.target.id
  location               = data.azurerm_resource_group.target.location
  tags                   = local.service_tags
  response_export_values = []

  identity {
    type         = "UserAssigned"
    identity_ids = [data.azurerm_user_assigned_identity.service.id]
  }

  body = {
    properties = {
      environmentId       = azurerm_container_app_environment.service.id
      workloadProfileName = "Consumption"
      configuration = {
        triggerType = "Event"

        # A bit over the worker's own 14 minute deadline, which leaves it time to say "not reviewed", and under the
        # 20 minutes a message stays hidden. The queue does the retrying, so the job doesn't.
        replicaTimeout    = 900
        replicaRetryLimit = 0
        eventTriggerConfig = {
          parallelism            = 1
          replicaCompletionCount = 1
          scale = {
            minExecutions   = 0
            maxExecutions   = 3
            pollingInterval = 30
            rules = [{
              name     = "reviews-queue"
              type     = "azure-queue"
              identity = data.azurerm_user_assigned_identity.service.id
              metadata = {
                accountName = azurerm_storage_account.service.name
                queueName   = azurerm_storage_queue.reviews.name
                queueLength = "1"
              }
            }]
          }
        }
      }
      template = {
        containers = [{
          name      = "review"
          image     = local.service_image
          args      = ["work"]
          resources = { cpu = 1, memory = "2Gi" }
          env = concat(
            [for name, value in merge(local.service_storage, local.service_home, {
              TF_REVIEW_KEY_VAULT            = azurerm_key_vault.service.name
              TF_REVIEW_GITHUB_APP_CLIENT_ID = "Iv23lilG16KleMoC5ReP"
              TF_REVIEW_OPENAI_ENDPOINT      = "https://${azurerm_cognitive_account.foundry.custom_subdomain_name}.openai.azure.com/openai/v1/"
              TF_REVIEW_OPENAI_DEPLOYMENT    = azurerm_cognitive_deployment.gpt_5_4_mini.name
            }) : { name = name, value = value }],
            [local.service_client_id],
          )
        }]
      }
    }
  }

  lifecycle {
    prevent_destroy = true
  }
}

output "api_url" {
  description = "Where the reusable workflow submits reviews."
  value       = "https://${azapi_resource.api.output.properties.configuration.ingress.fqdn}"
}
