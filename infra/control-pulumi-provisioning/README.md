# Control Pulumi provisioning proof

Staging-only state backend, dedicated seed vault, secrets key, and provisioner
data-plane roles for the #1014 AC4 Pulumi proof (#1034).
`dev/regenerate-infra.sh` preserves this directory. Production does not deploy
it: `enablePulumiProvisioning` defaults to false and stays unset in
`infra/main.parameters.production.json`.

This module never writes secret values, account keys, SAS tokens, or
connection strings. Outputs are names and locators only.

## Resources

| Resource | Purpose |
| --- | --- |
| StorageV2 Standard_LRS account | Pulumi Blob state. Shared-key access off, public blobs off, TLS 1.2, blob versioning and soft delete. |
| `pulumi-state` container | State blobs. Storage Blob Data Contributor for the existing provisioner UAMI, scoped to this container. |
| Dedicated seed Key Vault | RBAC, soft delete, purge protection, public access matching the Control API Key Vault firewall (deny by default, API egress subnet, optional operator CIDRs). Holds allowlisted seed secrets and the Pulumi secrets key. |
| RSA 2048 key `pulumi-secrets` | Pulumi secrets provider in the seed vault. Key Vault Crypto User for the same UAMI, scoped to this key. |
| Key Vault Secrets User on the seed vault | Seed-vault reads for allowlisted locators (`Provisioning:SeedVaultUri`). Vault scope on this vault only. The provisioner gets no role on the Control API Key Vault. |

The provisioner is the existing staging UAMI (`ProvisionerClientId` /
`pulumiProvisionerPrincipalId`). This module does not create an identity and
does not grant Contributor. Engine SQL SKUs are untouched.

The Control API Key Vault is not referenced. Seed secrets and the Pulumi
secrets key live in this dedicated vault so Control's own secrets stay out of
reach.

## Locators

Names are `stpulumi` / `ecseedkv` plus `uniqueString(resourceGroup().id)`.
Staging (`rg-valence-control-staging`) therefore uses:

| Setting | Value |
| --- | --- |
| `Provisioning__StateBackendLocator` | `stpulumitud53zotij43k/pulumi-state` |
| `Provisioning__SecretsProviderLocator` | `azurekeyvault://ecseedkvtud53zotij43k.vault.azure.net/keys/pulumi-secrets` |
| `Provisioning__SeedVaultUri` | `https://ecseedkvtud53zotij43k.vault.azure.net/` |
| `Provisioning__RegistryLoginServer` | `valenceruntimeimages.azurecr.io` |
| `Provisioning__RegistryId` | `/subscriptions/{RegistrySubscriptionId}/resourceGroups/{RegistryResourceGroupName}/providers/Microsoft.ContainerRegistry/registries/{RegistryName}` |

`Provisioning__SecretsProviderLocator` is the full Pulumi Key Vault URL. The
worker accepts that form and the short `{vault}/{key}` form and normalizes both
to the same `azurekeyvault://{vault}.vault.azure.net/keys/{key}` URL.

The engine stack reads the commercial registry from these two settings when they
are both set, so the first Create works on a fresh environment that has never
applied the `elsa-platform` stack. Config wins over a `StackReference`. The
resource id is overlayed at run time from host settings and is not stored in the
provisioning request snapshot (stack config must not carry ARM identifiers).
The platform stack remains the shared-registry program for environments that
apply it; staging does not need that extra resource group for the first engine.

Those staging-only values live in the worker-settings overlay, not in
production parameter files. The overlay is applied only when
`enablePulumiProvisioning` is true.

## Who applies this module

The base subscription deployment (`infra/main.bicep`) includes this module
when `enablePulumiProvisioning` is true. Staging parameters set that flag,
the provisioner principal ID, and the API egress subnet for the seed vault
firewall. The same operator rights as the base deployment apply, plus:

- storage account write in the Control resource group
- `Microsoft.KeyVault/vaults/write` for the dedicated seed vault
- `Microsoft.KeyVault/vaults/keys/write` on that vault
- `Microsoft.Network/virtualNetworks/subnets/joinViaServiceEndpoint/action` on
  `snet-api-egress`
- `Microsoft.Authorization/roleAssignments/write` limited to Storage Blob
  Data Contributor, Key Vault Crypto User, and Key Vault Secrets User

A staging `what-if` should show only the new account and container, the seed
vault and key, and those three role assignments. It must not assign any role
on `ecapikv…`.
