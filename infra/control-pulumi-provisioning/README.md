# Control Pulumi provisioning proof

Staging-only state backend, secrets key, and provisioner data-plane roles for
the #1014 AC4 Pulumi proof (#1034). `dev/regenerate-infra.sh` preserves this
directory. Production does not deploy it: `enablePulumiProvisioning` defaults
to false and stays unset in `infra/main.parameters.production.json`.

This module never writes secret values, account keys, SAS tokens, or
connection strings. Outputs are names and locators only.

## Resources

| Resource | Purpose |
| --- | --- |
| StorageV2 Standard_LRS account | Pulumi Blob state. Shared-key access off, public blobs off, TLS 1.2, blob versioning and soft delete. |
| `pulumi-state` container | State blobs. Storage Blob Data Contributor for the existing provisioner UAMI, scoped to this container. |
| RSA 2048 key `pulumi-secrets` | Pulumi secrets provider in the existing Control API Key Vault. Key Vault Crypto User for the same UAMI, scoped to this key. |
| Key Vault Secrets User on that vault | Seed-vault reads for allowlisted locators (`Provisioning:SeedVaultUri`). Vault scope, not subscription or resource-group. |

The provisioner is the existing staging UAMI (`ProvisionerClientId` /
`pulumiProvisionerPrincipalId`). This module does not create an identity and
does not grant Contributor. Engine SQL SKUs are untouched.

The Control API Key Vault must already exist (phase 1 of
`infra/api-key-vault`). Staging in `rg-valence-control-staging` already has
it. A new environment cannot enable this flag before that vault exists.

## Locators

Names are `stpulumi` / `ecapikv` plus `uniqueString(resourceGroup().id)`.
Staging (`rg-valence-control-staging`) therefore uses:

| Setting | Value |
| --- | --- |
| `Provisioning__StateBackendLocator` | `stpulumitud53zotij43k/pulumi-state` |
| `Provisioning__SecretsProviderLocator` | `azurekeyvault://ecapikvtud53zotij43k.vault.azure.net/keys/pulumi-secrets` |
| `Provisioning__SeedVaultUri` | `https://ecapikvtud53zotij43k.vault.azure.net/` |

Those staging-only values live in the worker-settings overlay, not in
production parameter files.

## Who applies this module

The base subscription deployment (`infra/main.bicep`) includes this module
when `enablePulumiProvisioning` is true. Staging parameters set that flag
and the provisioner principal ID. The same operator rights as the base
deployment apply, plus:

- storage account write in the Control resource group
- `Microsoft.KeyVault/vaults/keys/write` on the existing vault
- `Microsoft.Authorization/roleAssignments/write` limited to Storage Blob
  Data Contributor, Key Vault Crypto User, and Key Vault Secrets User

A staging `what-if` should show only the new account and container, the key,
and those three role assignments.
