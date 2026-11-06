# The reviewer's dashboard: an Azure Workbook over what the service reports (cost and tokens per agent, cache hits,
# findings, what wasn't reviewed, dismissals) and what it uses (review job runs, Cosmos request units, storage). Every
# tile is a KQL query on the workspace, so each can be run and checked from the command line.

# Cosmos and storage metrics reach the workspace as AzureMetrics rows, a few KB a day against the 0.2 GB cap.
resource "azurerm_monitor_diagnostic_setting" "cosmos" {
  name                       = "to-log-tf-review-prod"
  target_resource_id         = azapi_resource.cosmos.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.service.id

  enabled_metric {
    category = "Requests"
  }
}

resource "azurerm_monitor_diagnostic_setting" "storage" {
  name                       = "to-log-tf-review-prod"
  target_resource_id         = azurerm_storage_account.service.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.service.id

  enabled_metric {
    category = "Capacity"
  }

  enabled_metric {
    category = "Transaction"
  }
}

locals {
  # A tile: a KQL query over the chosen time range, drawn as a table or a chart.
  dashboard_tiles = [
    {
      title         = "Reviews by repo and verdict"
      visualization = "table"
      query         = <<-KQL
        AppMetrics | where TimeGenerated {TimeRange} and Name == "tfreview.reviews"
        | extend repository = tostring(Properties.repository), verdict = tostring(Properties.verdict)
        | summarize reviews = sum(Sum) by repository, verdict | order by reviews desc
      KQL
    },
    {
      title         = "Estimated model cost per day (USD, list prices)"
      visualization = "barchart"
      query         = <<-KQL
        AppMetrics | where TimeGenerated {TimeRange} and Name == "tfreview.llm.cost"
        | extend agent = tostring(Properties.agent)
        | summarize usd = round(sum(Sum), 4) by bin(TimeGenerated, 1d), agent
      KQL
    },
    {
      title         = "Model calls per role: cache hits, failures and latency"
      visualization = "table"
      query         = <<-KQL
        AppMetrics | where TimeGenerated {TimeRange} and Name == "tfreview.llm.latency"
        | extend agent = tostring(Properties.agent), finish = tostring(Properties.finish), model = tostring(Properties.model)
        | summarize calls = sum(ItemCount), cached = sumif(ItemCount, finish == "cached"),
            failed = sumif(ItemCount, finish !in ("stop", "cached")), avg_ms = round(sum(Sum) / sum(ItemCount)) by agent, model
        | extend cache_hit_pct = round(100.0 * cached / calls, 1) | order by calls desc
      KQL
    },
    {
      title         = "Tokens per role"
      visualization = "table"
      query         = <<-KQL
        AppMetrics | where TimeGenerated {TimeRange} and Name in ("tfreview.llm.input_tokens", "tfreview.llm.cached_input_tokens", "tfreview.llm.output_tokens")
        | extend agent = tostring(Properties.agent), tokens_of = replace_string(replace_string(Name, "tfreview.llm.", ""), "_tokens", "")
        | summarize tokens = sum(Sum) by agent, tokens_of | evaluate pivot(tokens_of, sum(tokens))
      KQL
    },
    {
      title         = "Findings by agent and severity"
      visualization = "table"
      query         = <<-KQL
        AppMetrics | where TimeGenerated {TimeRange} and Name == "tfreview.findings"
        | extend agent = tostring(Properties.agent), severity = tostring(Properties.severity)
        | summarize findings = sum(Sum) by agent, severity | order by findings desc
      KQL
    },
    {
      title         = "Dismissed findings by rule (D56)"
      visualization = "table"
      query         = <<-KQL
        AppMetrics | where TimeGenerated {TimeRange} and Name == "tfreview.dismissals"
        | extend rule = tostring(Properties.rule), repository = tostring(Properties.repository)
        | summarize dismissed = sum(Sum) by rule, repository | order by dismissed desc
      KQL
    },
    {
      title         = "Not reviewed, and findings the evidence check dropped"
      visualization = "table"
      query         = <<-KQL
        AppMetrics | where TimeGenerated {TimeRange} and Name in ("tfreview.not_reviewed", "tfreview.evidence_drops")
        | extend what = iff(Name == "tfreview.not_reviewed", strcat("not reviewed: ", tostring(Properties.scope)), "evidence drop"),
            reason = coalesce(tostring(Properties.reason), "")
        | summarize count = sum(Sum) by what, reason | order by count desc
      KQL
    },
    {
      title         = "Review job runs per day"
      visualization = "barchart"
      query         = <<-KQL
        ContainerAppSystemLogs_CL | where TimeGenerated {TimeRange} and JobName_s != ""
        | summarize completed = countif(Reason_s == "Completed"),
            failed = countif(Reason_s in ("BackoffLimitExceeded", "DeadlineExceeded", "Failed")) by bin(TimeGenerated, 1d)
      KQL
    },
    {
      title         = "Cosmos request units per hour (free tier: 1,000 RU/s)"
      visualization = "timechart"
      query         = <<-KQL
        AzureMetrics | where TimeGenerated {TimeRange} and ResourceProvider == "MICROSOFT.DOCUMENTDB" and MetricName == "TotalRequestUnits"
        | summarize request_units = sum(Total) by bin(TimeGenerated, 1h)
      KQL
    },
    {
      title         = "Storage used (MB)"
      visualization = "timechart"
      query         = <<-KQL
        AzureMetrics | where TimeGenerated {TimeRange} and ResourceProvider == "MICROSOFT.STORAGE" and MetricName == "UsedCapacity"
        | summarize used_mb = round(max(Maximum) / 1000000, 1) by bin(TimeGenerated, 1d)
      KQL
    },
  ]
}

resource "azurerm_application_insights_workbook" "dashboard" {
  # Workbooks are named by a UUID; this one is derived, so it stays the same from plan to plan.
  name                = uuidv5("url", "https://github.com/gergodrazsdik/tf-review/infra/dashboard")
  resource_group_name = data.azurerm_resource_group.target.name
  location            = data.azurerm_resource_group.target.location
  display_name        = "tf-review dashboard"
  source_id           = lower(azurerm_log_analytics_workspace.service.id)
  category            = "workbook"
  tags                = local.service_tags

  data_json = jsonencode({
    version = "Notebook/1.0"
    items = concat(
      [
        {
          type = 1
          name = "header"
          content = {
            json = "## tf-review\nThe reviewer's metrics and resource use. Costs are estimates at list prices (`ModelPrices`), not an invoice."
          }
        },
        {
          type = 9
          name = "parameters"
          content = {
            version = "KqlParameterItem/1.0"
            style   = "pills"
            parameters = [{
              id         = "6f0d0c76-1f1e-4b7a-9d55-3a1f0c1d7e01"
              version    = "KqlParameterItem/1.0"
              name       = "TimeRange"
              label      = "Time range"
              type       = 4
              isRequired = true
              value      = { durationMs = 604800000 }
              typeSettings = {
                selectableValues = [
                  { durationMs = 86400000 },
                  { durationMs = 604800000 },
                  { durationMs = 2592000000 },
                ]
              }
            }]
          }
        },
      ],
      [
        for index, tile in local.dashboard_tiles : {
          type = 3
          name = "tile-${index}"
          content = {
            version                  = "KqlItem/1.0"
            title                    = tile.title
            query                    = tile.query
            size                     = 0
            queryType                = 0
            resourceType             = "microsoft.operationalinsights/workspaces"
            crossComponentResources  = [azurerm_log_analytics_workspace.service.id]
            timeContextFromParameter = "TimeRange"
            visualization            = tile.visualization
          }
        }
      ],
    )
  })
}
