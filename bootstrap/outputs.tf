output "state_storage_account" {
  description = "Storage account that holds every repo's state."
  value       = azurerm_storage_account.state.name
}

output "state_resource_group" {
  description = "Resource group of the state storage account."
  value       = azurerm_resource_group.state.name
}
