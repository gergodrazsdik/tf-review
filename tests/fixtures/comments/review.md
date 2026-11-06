<!-- tf-review -->
### tf-review

**3 finding(s)** for commit `0123456`.

| Severity | Resource | Rule | Finding | Suggestion | Source |
| --- | --- | --- | --- | --- | --- |
| high, **blocking** | `azurerm_network_security_rule.ssh` | [POL-002](https://github.com/gergodrazsdik/tf-standards/blob/adf50af6607b4e2b28a7e3566db8adfdecabe683/policies/POL-002-network.md), also `CKV_AZURE_10` | `POL-002: SSH is open to the internet` | `Allow SSH only from the bastion subnet \| or a VPN range.` | policy and model |
| medium | `azurerm_storage_account.logs` | `CKV_AZURE_59` | `Public network access is enabled` |  | scanner |
| low | `azurerm_storage_account.logs` | `ADR-001` | ``Name `stlogs` has no environment`` | `Rename it to stlogsprod.` | model |

Wrong? Tick a finding to dismiss it, which helps tune the reviewer:

- [ ] Dismiss `azurerm_network_security_rule.ssh` (`POL-002`) <!-- tf-review:dismiss 09b3bcb6e83c98fe rule=POL-002 -->
- [ ] Dismiss `azurerm_storage_account.logs` (`CKV_AZURE_59`) <!-- tf-review:dismiss 11f5c7caa0da196b rule=CKV_AZURE_59 -->
- [x] Dismiss `azurerm_storage_account.logs` (`ADR-001`) <!-- tf-review:dismiss d75ff549dfd9a21d rule=ADR-001 recorded -->

Not reviewed by cost (the plan is too large): `azurerm_linux_virtual_machine.big`.

This repo blocks merges only on high findings a policy or scanner reported itself, marked blocking above; the rest of this review is advisory, and finding nothing doesn't mean a change is approved.
