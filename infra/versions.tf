terraform {
  required_version = "~> 1.13"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.12"
    }
  }

  # scripts/tofu-init.sh passes the settings from the repo's Actions variables.
  backend "azurerm" {}
}

# Credentials come from the ARM_* variables and OIDC in the workflows.
provider "azurerm" {
  features {}

  # The identities only hold roles on one resource group, so they can't register providers.
  resource_provider_registrations = "none"
}
