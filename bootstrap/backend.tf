terraform {
  backend "azurerm" {
    resource_group_name  = "rg-tf-review-state-prod"
    storage_account_name = "sttfreviewstateprod"
    container_name       = "tfstate-bootstrap"
    key                  = "bootstrap.tfstate"
    use_azuread_auth     = true
  }
}
