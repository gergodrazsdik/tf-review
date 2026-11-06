# State encryption keys (D59): OpenTofu's azure_vault key provider wraps each state's data key with an RSA key here,
# one per onboarded repo, so an identity can only read the state of the repo it belongs to. Losing a key means losing
# the state it encrypts, so purge protection keeps a deleted key recoverable for 90 days, and nobody can purge it
# sooner: that can't be turned off once it's on.
resource "azurerm_key_vault" "state" {
  name                       = "kv-tf-review-state-prod"
  resource_group_name        = azurerm_resource_group.state.name
  location                   = azurerm_resource_group.state.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = true
  soft_delete_retention_days = 90
  tags                       = local.common_tags

  lifecycle {
    prevent_destroy = true
  }
}

# You create and manage the keys, through this bootstrap. A new role assignment can take a few minutes to apply, so
# the first apply may fail creating the keys; a second one finishes them.
resource "azurerm_role_assignment" "deployer_state_keys" {
  scope                = azurerm_key_vault.state.id
  role_definition_name = "Key Vault Crypto Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_key_vault_key" "state" {
  for_each = var.repos

  name         = "state-${each.key}"
  key_vault_id = azurerm_key_vault.state.id
  key_type     = "RSA"
  key_size     = 3072
  key_opts     = ["wrapKey", "unwrapKey", "encrypt", "decrypt"]
  tags         = merge(local.common_tags, { project = each.key })

  depends_on = [azurerm_role_assignment.deployer_state_keys]

  lifecycle {
    prevent_destroy = true
  }
}

# The plan job reads state and the apply job writes it, so both use the key, and only their own repo's.
resource "azurerm_role_assignment" "plan_state_key" {
  for_each = var.repos

  scope                = azurerm_key_vault_key.state[each.key].resource_versionless_id
  role_definition_name = "Key Vault Crypto User"
  principal_id         = azurerm_user_assigned_identity.plan[each.key].principal_id
}

resource "azurerm_role_assignment" "apply_state_key" {
  for_each = var.repos

  scope                = azurerm_key_vault_key.state[each.key].resource_versionless_id
  role_definition_name = "Key Vault Crypto User"
  principal_id         = azurerm_user_assigned_identity.apply[each.key].principal_id
}
