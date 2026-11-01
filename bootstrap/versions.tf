terraform {
  required_version = "~> 1.13"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.12"
    }
    github = {
      source  = "integrations/github"
      version = "~> 6.13"
    }
  }
}

provider "azurerm" {
  features {}

  subscription_id     = var.subscription_id
  storage_use_azuread = true
}

# The token comes from GITHUB_TOKEN, which the just recipes read from pass.
provider "github" {
  owner = var.github_owner
}
