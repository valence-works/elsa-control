# Control API Key Vault

One vault per Control environment. It holds the API's secret App Service
settings. The site sees only versionless Key Vault references, resolved with
the API user-assigned identity (`keyVaultReferenceIdentity`).

This directory is hand-maintained. `dev/regenerate-infra.sh` preserves it.

## Resources

| Resource | Purpose |
| --- | --- |
| Key Vault (RBAC, soft delete, purge protection) | Secret store. This module never writes secret values. |
| Key Vault Secrets User on the API identity | Runtime resolution. Secrets User includes `secrets/getSecret` and can read values. |

The GitHub OIDC deploy identity gets no Key Vault data role. Nothing in this
repo reads vault values. `scripts/check-key-vault-references.sh` uses Reader
on the App Service (`Microsoft.Web/sites/config/read`) and the ARM
`configreferences` endpoint, and prints names and status only.

No other identity receives a data-plane role. An operator writes each secret
value after the vault exists. CI never writes a secret value.

A private endpoint is out of scope (#839). Network access is the vault
firewall: default deny, bypass none, the API egress subnet (which must expose
the `Microsoft.KeyVault` service endpoint), the operator write-path CIDRs,
and optional API outbound NAT CIDRs. Trusted-services bypass does not cover
App Service Key Vault reference resolution. The site reaches this vault
through VNet integration and that subnet service endpoint. The subnet rule
does not set `ignoreMissingVnetServiceEndpoint`; a missing service endpoint
fails the deploy. An empty `AZURE_API_EGRESS_SUBNET_ID` also fails the deploy.

## Secret names

| App setting | Vault secret name |
| --- | --- |
| `Authentication__ApiKey` | `Authentication-ApiKey` |
| `Authentication__ControlIdentity__ClientSecret` | `Authentication-ControlIdentity-ClientSecret` |
| `Authentication__BuilderClientApiKey` | `Authentication-BuilderClientApiKey` |
| `Billing__Stripe__SecretKey` | `Billing-Stripe-SecretKey` |
| `Billing__Stripe__WebhookSigningSecret` | `Billing-Stripe-WebhookSigningSecret` |
| `Billing__Stripe__WebhookSources__0__SigningSecret` | `Billing-Stripe-WebhookSources-control-SigningSecret` |
| `Billing__Stripe__WebhookSources__1__SigningSecret` | `Billing-Stripe-WebhookSources-hub-SigningSecret` |
| `ManagedElsa__Handoff__ActivePrivateKeyPem` | `ManagedElsa-Handoff-ActivePrivateKeyPem` |

`Authentication__ControlIdentity__ClientSecret` stays a client secret in this
vault. Switching that identity to a federated credential is #838.

## Who applies this module

Phase 1 deploys this module: the vault, its network rules and the Key Vault
Secrets User assignment. A human operator applies it, with
`scripts/deploy-azure-elsa-control.sh --vault-only` or, for production, a
direct resource-group deployment of this module (see "Production apply" in
`docs/deployment/azure-app-service.md`). That operator holds:

- Key Vault Contributor (or another role with
  `Microsoft.KeyVault/vaults/write`) on the Control resource group;
- `Microsoft.Network/virtualNetworks/subnets/joinViaServiceEndpoint/action`
  on `snet-api-egress`;
- `Microsoft.Authorization/roleAssignments/write` limited to Key Vault
  Secrets User, for example Role Based Access Control Administrator with a
  condition on that role.

Re-running phase 1 to change the firewall rules uses the same operator.

Phase 2 (the site apply, a run without `--vault-only`) never deploys this
module. It reads the existing vault URI and fails with a pointer to phase 1
when the vault is missing. Phase 2 needs only management-plane read on the
vault (Reader on the resource group is enough) and no Key Vault data role.
The roles of the GitHub deploy identity in `infra/control-deploy-identity`
(Reader, Website Contributor on the API site, AcrPush) do not change.

## Operator write path

1. Apply `infra/control-egress` so `snet-api-egress` has the Key Vault
   service endpoint. Record `AZURE_API_EGRESS_SUBNET_ID`.
2. Phase 1: as the phase 1 operator, deploy this module with
   `scripts/deploy-azure-elsa-control.sh --vault-only`. Do not apply the
   site yet.
3. If your client is not already on `operatorWriteIpCidrs`, add one IPv4 CIDR
   through the management plane (`az keyvault network-rule add`). That does
   not require data-plane access.
4. Grant yourself a temporary Key Vault Secrets Officer assignment scoped to
   this vault, or use an existing privileged operator identity. Do not grant
   CI a write role.
5. Write each secret by name. Do not print the value.

```bash
az keyvault secret set \
  --vault-name "<vault-name>" \
  --name Authentication-ApiKey \
  --file /private/path/authentication-api-key \
  --output none
```

6. Remove the temporary write role.
7. Phase 2: set `API_KEY_VAULT_SECRETS_WRITTEN=true` in the GitHub
   environment, then apply the site so settings are repointed to the
   versionless references. Phase 2 reads this vault and does not redeploy
   it. Confirm every listed reference reports
   `Resolved` with `scripts/check-key-vault-references.sh` (names and
   status only).

## Purge protection

Purge protection cannot be turned off. The vault name is
`ecapikv` plus `uniqueString(resourceGroup().id)`, so it is fixed per
resource group. Deleting and recreating the resource group (`test`
teardown) keeps that name reserved for 90 days. Recover it before
redeploying:

```bash
az keyvault recover --name "<vault-name>" --resource-group "<rg>" --output none
```

A held name blocks a new vault until recovery or the retention period ends.

## Rotation

Write a new version of the same secret name, then restart the API App Service
so the versionless reference refreshes immediately. Without a restart, App
Service can take up to 24 hours to pick up the new version.

```bash
az keyvault secret set \
  --vault-name "<vault-name>" \
  --name Authentication-ApiKey \
  --file /private/path/authentication-api-key \
  --output none
az webapp restart --resource-group "<rg>" --name "<webapp>"
```

## Parameters

| Parameter | Purpose |
| --- | --- |
| `apiIdentityPrincipalId` | API identity principal. Required. |
| `apiEgressSubnetId` | `AZURE_API_EGRESS_SUBNET_ID`. Required. |
| `operatorWriteIpCidrs` | Operator write-path CIDRs (`KEY_VAULT_OPERATOR_WRITE_IP_CIDRS`). |
| `apiOutboundIpCidrs` | Optional NAT `/32` CIDRs (`KEY_VAULT_API_OUTBOUND_IP_CIDRS`). |
