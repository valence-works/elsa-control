# Control API Key Vault

One vault per Control environment. It holds the API's secret App Service
settings. The site sees only versionless Key Vault references, resolved with
the API user-assigned identity (`keyVaultReferenceIdentity`).

This directory is hand-maintained. `dev/regenerate-infra.sh` preserves it.

## Resources

| Resource | Purpose |
| --- | --- |
| Key Vault (RBAC, soft delete, purge protection) | Secret store. This module never writes secret values. |
| Key Vault Secrets User on the API identity | Runtime resolution of App Service Key Vault references. |
| Key Vault Secrets User on the deploy identity | Read-only. CI may list secret **names** and reference status. It never writes a secret value. |

No other identity receives a data-plane role. An operator writes each secret
value after the vault exists.

A private endpoint is out of scope (#839). Network access is the vault
firewall: default deny, the API egress subnet, the operator write-path CIDRs,
and optional API outbound NAT CIDRs. App Service Key Vault references are
platform configuration traffic, not application egress, so the vault also
bypasses trusted Azure services for resolution.

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

## Operator write path

1. Deploy this module (the Azure Control API Deploy `infra` path does that
   before it applies the API site).
2. If your client is not already on `operatorWriteIpCidrs`, add one IPv4 CIDR
   through the management plane (`az keyvault network-rule add`). That does
   not require data-plane access.
3. Grant yourself a temporary Key Vault Secrets Officer assignment scoped to
   this vault, or use an existing privileged operator identity. Do not grant
   CI a write role.
4. Write each secret by name. Do not print the value.

```bash
az keyvault secret set \
  --vault-name "<vault-name>" \
  --name Authentication-ApiKey \
  --file /private/path/authentication-api-key \
  --output none
```

5. Remove the temporary write role.
6. Confirm every listed reference reports `Resolved` with
   `scripts/check-key-vault-references.sh` (names and status only).

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
| `deployIdentityPrincipalId` | Deploy identity principal. Required. |
| `apiEgressSubnetId` | `AZURE_API_EGRESS_SUBNET_ID` when the site is attached. |
| `operatorWriteIpCidrs` | Operator write-path CIDRs (`KEY_VAULT_OPERATOR_WRITE_IP_CIDRS`). |
| `apiOutboundIpCidrs` | Optional NAT `/32` CIDRs (`KEY_VAULT_API_OUTBOUND_IP_CIDRS`). |
