variable "subscription_id" {
  description = "Azure subscription that holds every resource (D13)."
  type        = string
}

variable "budget_contact_email" {
  description = "Receives the budget alerts. Set from git config by the just recipes, so it stays out of the code."
  type        = string
  sensitive   = true
}

variable "github_owner" {
  description = "GitHub account that owns the onboarded repos."
  type        = string
  default     = "gergodrazsdik"
}

variable "location" {
  description = "Azure region for all resources (D8)."
  type        = string
  default     = "germanywestcentral"
}

variable "budget_amount" {
  description = "Monthly budget in the subscription's billing currency (D15)."
  type        = number
  default     = 30
}

variable "budget_start_date" {
  description = "First day of the budget period. Azure needs the first of a month, and it can't move later without replacing the budget."
  type        = string
  default     = "2026-11-01T00:00:00Z"
}

variable "repos" {
  description = "Onboarded repos, keyed by repo name. Each entry gets a state container, a target resource group, plan and apply identities, and Actions variables."
  type = map(object({
    target_resource_group = string
  }))
  default = {
    "tf-review-demo" = {
      target_resource_group = "rg-tf-review-demo-prod"
    }
  }
}
