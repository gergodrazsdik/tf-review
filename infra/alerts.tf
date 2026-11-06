# Alerts, mailed through bootstrap's action group (OPS-2). Each one stands for a way the service can fail without
# anyone seeing it: the dashboard shows what happened, these say when to look.

locals {
  action_group_id = "${data.azurerm_resource_group.target.id}/providers/Microsoft.Insights/actionGroups/ag-tf-review-prod"
}

# A review the worker gave up on: the PR got "not reviewed", or nothing at all when the App couldn't post.
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "give_ups" {
  name                 = "alert-tf-review-give-ups-prod"
  resource_group_name  = data.azurerm_resource_group.target.name
  location             = data.azurerm_resource_group.target.location
  description          = "The review job gave up on a review. Its ERROR line in ContainerAppConsoleLogs_CL says why."
  severity             = 2
  scopes               = [azurerm_log_analytics_workspace.service.id]
  evaluation_frequency = "PT1H"
  window_duration      = "PT1H"
  tags                 = local.service_tags

  criteria {
    query                   = <<-KQL
      AppMetrics | where Name == "tfreview.give_ups"
      | summarize give_ups = sum(Sum)
    KQL
    time_aggregation_method = "Total"
    metric_measure_column   = "give_ups"
    operator                = "GreaterThan"
    threshold               = 0
  }

  action {
    action_groups = [local.action_group_id]
  }
}

# The workspace stops taking data for the day once it reaches its 0.2 GB cap, which blinds the dashboard and the alert
# above until midnight UTC (OPS-18).
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "log_cap" {
  name                 = "alert-tf-review-log-cap-prod"
  resource_group_name  = data.azurerm_resource_group.target.name
  location             = data.azurerm_resource_group.target.location
  description          = "Log Analytics reached its daily cap and stopped ingesting until midnight UTC."
  severity             = 2
  scopes               = [azurerm_log_analytics_workspace.service.id]
  evaluation_frequency = "PT1H"
  window_duration      = "PT1H"
  tags                 = local.service_tags

  criteria {
    query                   = <<-KQL
      _LogOperation | where Category =~ "Ingestion" and Detail has "OverQuota"
      | summarize events = count()
    KQL
    time_aggregation_method = "Total"
    metric_measure_column   = "events"
    operator                = "GreaterThan"
    threshold               = 0
  }

  action {
    action_groups = [local.action_group_id]
  }
}

# A review takes minutes, so messages on the queue for hours mean the job isn't taking them: a bad image, an
# environment that can't start, or a scaling rule that stopped firing. Azure reports the queue's length about hourly
# and only as an average, so this needs it at one or more on average over six hours, which one review in flight
# when a sample is taken can't reach.
resource "azurerm_monitor_metric_alert" "queue_stuck" {
  name                = "alert-tf-review-queue-stuck-prod"
  resource_group_name = data.azurerm_resource_group.target.name
  scopes              = ["${azurerm_storage_account.service.id}/queueServices/default"]
  description         = "Review messages have sat on the queue for hours. Check the review job's executions."
  severity            = 2
  frequency           = "PT1H"
  window_size         = "PT6H"
  tags                = local.service_tags

  criteria {
    metric_namespace = "Microsoft.Storage/storageAccounts/queueServices"
    metric_name      = "QueueMessageCount"
    aggregation      = "Average"
    operator         = "GreaterThanOrEqual"
    threshold        = 1
  }

  action {
    action_group_id = local.action_group_id
  }
}
