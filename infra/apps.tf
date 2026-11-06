# The review service: an API that scales to zero and a job that only exists while a message is on the queue, so
# neither costs anything between PRs (D16). Both run the released image, with the identity from bootstrap.

variable "allowed_repository_ids" {
  description = "Repos the API serves, as comma-separated IDs. Bootstrap's repos map sets the Actions variable this comes from (D40)."
  type        = string

  # An unset Actions variable arrives as an empty string, and the API would then refuse every repo.
  validation {
    condition     = can(regex("^[0-9]+(,[0-9]+)*$", var.allowed_repository_ids))
    error_message = "allowed_repository_ids must be comma-separated repository IDs; is TF_REVIEW_ALLOWED_REPOSITORY_IDS set?"
  }
}

locals {
  # image-v0.0.19. A release that changes the service updates this, and the workflow only needs the same one when
  # its own commands change.
  service_image = "ghcr.io/gergodrazsdik/tf-review@sha256:6667239cf99688b6192b640eb6378df46a565da8a03e39d699017b065e0e3ba2"

  # Reviews that run at once, and each one's share of the deployment's tokens a minute: 90% of it, split between them,
  # so together they stay under the rate limit (D54). Two at once leaves each 45K, enough for a large plan's batches.
  review_job_executions    = 2
  review_tokens_per_minute = floor(azurerm_cognitive_deployment.gpt_6_1_sol.sku[0].capacity * 1000 * 0.9 / local.review_job_executions)

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
  api_client_id = {
    name  = "AZURE_CLIENT_ID"
    value = sensitive(data.azurerm_user_assigned_identity.api.client_id)
  }
}

# Bootstrap creates this, because the apply identity can't assign roles (D24).
data "azurerm_user_assigned_identity" "service" {
  name                = "id-tf-review-service-prod"
  resource_group_name = data.azurerm_resource_group.target.name
}

# The API's own identity, from bootstrap: storage for plans and the queue, and below, the reviews container (SEC-6).
data "azurerm_user_assigned_identity" "api" {
  name                = "id-tf-review-api-prod"
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

# Only the container holding review statuses, which the API writes when it takes a review in.
resource "azurerm_cosmosdb_sql_role_assignment" "api" {
  resource_group_name = data.azurerm_resource_group.target.name
  account_name        = azapi_resource.cosmos.name
  role_definition_id  = "${azapi_resource.cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
  principal_id        = sensitive(data.azurerm_user_assigned_identity.api.principal_id)
  scope               = "${azapi_resource.cosmos.id}/dbs/${azurerm_cosmosdb_sql_database.service.name}/colls/${azurerm_cosmosdb_sql_container.reviews.name}"
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
    identity_ids = [data.azurerm_user_assigned_identity.api.id]
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
            [local.api_client_id],
          )
        }]
      }
    }
  }

  lifecycle {
    prevent_destroy = true

    # The API refuses every caller whose workflow commit isn't released, so an empty list would refuse them all.
    precondition {
      condition     = length(local.released_commits) > 0 && alltrue([for commit in local.released_commits : can(regex("^[0-9a-f]{40}$", commit))])
      error_message = "infra/released-commits.txt must hold at least one full commit SHA, one per line."
    }
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
            maxExecutions   = local.review_job_executions
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
              TF_REVIEW_OPENAI_DEPLOYMENT    = azurerm_cognitive_deployment.gpt_6_1_sol.name
              TF_REVIEW_EMBEDDING_DEPLOYMENT = azurerm_cognitive_deployment.text_embedding_3_small.name
              # Part of the result cache's key, so a new model version doesn't get the old one's answers (D53).
              TF_REVIEW_OPENAI_MODEL_VERSION = azurerm_cognitive_deployment.gpt_6_1_sol.model[0].version
              TF_REVIEW_TOKENS_PER_MINUTE    = tostring(local.review_tokens_per_minute)
              TF_REVIEW_DAILY_BUDGET_USD     = "5"
            }) : { name = name, value = value }],
            [
              local.service_client_id,
              { name = "APPLICATIONINSIGHTS_CONNECTION_STRING", value = azurerm_application_insights.service.connection_string },
            ],
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
