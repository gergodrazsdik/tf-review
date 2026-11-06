terraform {
  required_version = "~> 1.13"

  required_providers {
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.13"
    }
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.12"
    }
  }

  # scripts/tofu-init.sh passes the settings from the repo's Actions variables.
  backend "azurerm" {}

  # State encryption (D59): a data key wrapped by this repo's RSA key in the state vault, which only its plan and
  # apply identities can use. The fallback reads the state written before encryption; the first apply rewrites it
  # encrypted, and a later change removes the fallback and enforces encryption.
  encryption {
    key_provider "azure_vault" "state" {
      vault_uri      = "https://kv-tf-review-state-prod.vault.azure.net"
      vault_key_name = "state-tf-review"
      key_length     = 32

      # Read only from here, not from ARM_USE_OIDC. CI signs in with GitHub's OIDC token, and on a laptop, where
      # there's no such token, this falls through to the Azure CLI.
      use_oidc = true
    }

    method "aes_gcm" "state" {
      keys = key_provider.azure_vault.state
    }

    method "unencrypted" "migrate" {}

    state {
      method = method.aes_gcm.state

      fallback {
        method = method.unencrypted.migrate
      }
    }
  }
}

# Credentials come from the ARM_* variables and OIDC in the workflows.
provider "azapi" {
  skip_provider_registration = true
}

provider "azurerm" {
  features {}

  # The identities only hold roles on one resource group, so they can't register providers.
  resource_provider_registrations = "none"

  # The service's storage account has access keys off, so the provider has to use its identity.
  storage_use_azuread = true
}
