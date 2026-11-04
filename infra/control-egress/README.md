# Control API static egress

The managed-instance provider runner bootstraps each instance's Azure SQL server through a temporary
firewall rule for exactly one IPv4 address (`Deployment:AzureProvider:Runner:SqlBootstrapIp`, which
refuses `0.0.0.0`). The production API on App Service egresses from a shared pool of fourteen
non-static addresses, so in-process bootstrap would be intermittent. This template (#310, part of
#264) gives the API one static egress: a small VNet in the Control resource group, a subnet delegated
to App Service, and a NAT gateway with a Standard static public IP. The API site joins the subnet
with regional VNet integration and routes all outbound traffic through it. The subnet also exposes
the `Microsoft.KeyVault` service endpoint so App Service Key Vault references can resolve through
the vault firewall (`infra/api-key-vault`).

Nothing inbound changes. No customer compute is created and the customer workload subscription is not
touched. Regional VNet integration is available on Basic and above, and NAT gateway integration has no
tier requirement beyond VNet integration (Microsoft docs, checked 2026-09-08); the Basic plan (B2 since #314, for in-process provisioning memory) supports it.
Two documented caveats apply while outbound internet routing is on: traffic to Azure Storage accounts
must use a service or private endpoint (the API has no direct Storage dependency; the platform content
share and image pull keep their default public configuration route, which is left unchanged), and
route-all only affects application traffic, not the platform's own configuration traffic.

| File | Purpose |
| --- | --- |
| `main.bicep` | VNet, delegated subnet, NAT gateway, static public IP; outputs the subnet id and the egress address. |
| `main.parameters.production.json` | Production region `belgiumcentral` (the API site's region; regional VNet integration requires the same region), names (`vnet-valence-control-prod-bec`, `snet-api-egress`, `natgw-valence-control-prod-bec`, `pip-natgw-valence-control-prod-bec`) and address space (`10.60.0.0/24`, subnet `/26`). |
| `main.parameters.staging.json` | Isolated staging API region `westeurope`, staging names, and a distinct `10.61.0.0/24` address space. |

## Key Vault endpoint-only mode

`egressDeploymentMode` defaults to `full`, which keeps the existing three-resource provisioning path and both outputs unchanged. For an already-provisioned VNet, `keyVaultEndpointOnly` uses the same VNet and subnet definition but conditionally omits the NAT gateway and Public IP resources. It retains the NAT attachment through `natGatewayName`, adds `Microsoft.KeyVault` to the service endpoints, and explicitly writes the production-read-back VNet setting `privateEndpointVNetPolicies: Disabled`. The subnet's App Service delegation, address prefixes, NAT association, and existing subnet network-policy settings stay in the shared definition. `egressIpAddress` continues to read the existing Public IP value; this mode does not deploy or modify that resource.

### Bind and inspect the current target

Set the approved production `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, and current `AZURE_API_EGRESS_SUBNET_ID` first. Resolve the VNet and subnet names from the production parameter file, capture the complete VNet response in a private local file, and verify that the returned VNet ID is the parent of the bound subnet ID:

```sh
set -eu
: "${AZURE_SUBSCRIPTION_ID:?Set the approved production subscription}"
: "${AZURE_RESOURCE_GROUP:?Set the approved production resource group}"
: "${AZURE_API_EGRESS_SUBNET_ID:?Set the current egress subnet resource ID}"

parameters_file=infra/control-egress/main.parameters.production.json
vnet_name="$(jq -er '.parameters.virtualNetworkName.value' "$parameters_file")"
subnet_name="$(jq -er '.parameters.subnetName.value' "$parameters_file")"
vnet_id="${AZURE_API_EGRESS_SUBNET_ID%/subnets/*}"
expected_subnet_id="${vnet_id}/subnets/${subnet_name}"
actual_subnet_id="$(printf '%s' "$AZURE_API_EGRESS_SUBNET_ID" | tr '[:upper:]' '[:lower:]')"
expected_subnet_id_lower="$(printf '%s' "$expected_subnet_id" | tr '[:upper:]' '[:lower:]')"
test "$actual_subnet_id" = "$expected_subnet_id_lower"

umask 077
vnet_readback="$(mktemp "${TMPDIR:-/tmp}/control-egress-vnet.XXXXXX")"
az network vnet show --subscription "$AZURE_SUBSCRIPTION_ID" \
  --resource-group "$AZURE_RESOURCE_GROUP" \
  --name "$vnet_name" \
  --output json > "$vnet_readback"
jq -e --arg id "$vnet_id" '(.id | ascii_downcase) == ($id | ascii_downcase)' \
  "$vnet_readback" > /dev/null
printf 'Full VNet readback saved privately to %s\n' "$vnet_readback"
```

Inspect the complete readback privately against the parameter file and Bicep model. Check all writable VNet and subnet properties, including VNet DHCP, DDoS, encryption and flow-timeout settings, plus subnet policy, delegation, endpoint, NSG, route-table and NAT settings. Ignore only read-only provider metadata. Stop if the target ID, region, tags, address prefixes, subnet set, or any writable property is unrepresented or differs from the current source. Keep the full response and raw resource IDs in the private local evidence file; do not paste them into issues, PRs or logs.

The modeled production subnet has one App Service delegation, the existing NAT attachment, and currently no service endpoints. If readback shows exactly the `Microsoft.KeyVault` endpoint at the configured region and every other writable value matches the model, skip the network update: the endpoint is already ready. Stop if any other endpoint or configuration is present; this template does not preserve additional endpoints or unmodeled properties.

If the Key Vault endpoint is absent and the complete readback matches the model, run the read-only preview with the explicit narrow mode:

```sh
whatif_readback="$(mktemp "${TMPDIR:-/tmp}/control-egress-whatif.XXXXXX")"
az deployment group what-if \
  --subscription "$AZURE_SUBSCRIPTION_ID" \
  --resource-group "$AZURE_RESOURCE_GROUP" \
  --name control-egress-keyvault-endpoint-preview \
  --template-file infra/control-egress/main.bicep \
  --parameters "@$parameters_file" egressDeploymentMode=keyVaultEndpointOnly \
  --no-pretty-print --output json > "$whatif_readback"
printf 'Full preview saved privately to %s\n' "$whatif_readback"
```

For the first production cutover, accept only one VNet `Modify` that adds the Key Vault service endpoint; provider normalization of the endpoint `locations` is `NoEffect`. NAT, Public IP, and unrelated resources must be `Ignore`. Stop on another action or any writable VNet property change beyond the endpoint and that `NoEffect` metadata. This full readback plus the fresh what-if is the refusal gate for unmodeled state. A what-if is not approval to apply: production still needs the separate #911 approval. This issue authorizes no production apply. Keep both private records through the approved change, post-change verification and any rollback. Once those are complete and sanitized evidence is recorded, remove the private files; publish only sanitized change counts, outcomes and revision bindings.

The site attachment is not in this template. `dev/patch-api-provisioner-identity.py` adds the optional
`api_egress_subnet_id` parameter to the Aspire-generated API module (`virtualNetworkSubnetId` and
`vnetRouteAllEnabled` are set only when it is non-empty), and the azd parameter template reads it from
`AZURE_API_EGRESS_SUBNET_ID`, so a later `azd provision` keeps the attachment. Contract gates:
`python3 scripts/tests/test_control_egress_infrastructure.py` and `scripts/tests/test_api_infrastructure.py`.
`dev/regenerate-infra.sh` preserves this directory.

## Cost and blast radius

A Standard NAT gateway and one Standard public IP cost on the order of forty euros per month plus
per-gigabyte processing; the API's outbound volume is small. Attaching the site restarts the API
once. While attached, every outbound call from the API (Entra, Azure SQL for the Catalog, ACR,
Azure Monitor, the customer workload subscription) leaves through the NAT address. Detaching
restores the platform pool; the SQL bootstrap address must then be cleared from the worker
composition before workers run again.

## Deployment (operator, Control subscription only)

1. What-if, then deploy the egress resources:
   ```sh
   az deployment group what-if --subscription 8e23037a-420f-4ad0-9594-9d194de29e84 \
     --resource-group rg-valence-control-prod --name control-egress \
     --template-file infra/control-egress/main.bicep \
     --parameters @infra/control-egress/main.parameters.production.json
   az deployment group create --subscription 8e23037a-420f-4ad0-9594-9d194de29e84 \
     --resource-group rg-valence-control-prod --name control-egress \
     --template-file infra/control-egress/main.bicep \
     --parameters @infra/control-egress/main.parameters.production.json \
     --query "properties.outputs" --output json
   ```
2. Record `integrationSubnetResourceId` as `AZURE_API_EGRESS_SUBNET_ID` in the azd environment and
   `egressIpAddress` as the `SqlBootstrapIp` parameter in `infra/control-worker-composition` through
   a reviewed PR. Neither value is secret.
3. Attach the site (one restart) and confirm the single outbound address:
   ```sh
   az webapp vnet-integration add --subscription 8e23037a-420f-4ad0-9594-9d194de29e84 \
     --resource-group rg-valence-control-prod --name api-m5uymkuaf222o \
     --vnet vnet-valence-control-prod-bec --subnet snet-api-egress
   az webapp config set --subscription 8e23037a-420f-4ad0-9594-9d194de29e84 \
     --resource-group rg-valence-control-prod --name api-m5uymkuaf222o \
     --vnet-route-all-enabled true --query vnetRouteAllEnabled
   ```
   Then verify health, and verify the egress from inside the site with `az webapp ssh` (the image ships
   `curl` and `sqlcmd`): `curl -s https://api.ipify.org` must print `egressIpAddress`, and a disposable
   Azure SQL server whose only firewall rule is that address must accept
   `sqlcmd -S <server>.database.windows.net -d master -N true -G -Q "SELECT 1"` with the provisioner
   identity, while the same query fails after the rule is removed. That is the runner's bootstrap
   path (`sql-bootstrap.sql` runs through the same firewall rule), so it is the #310 acceptance check.
   Delete the disposable server afterwards.
4. Rollback: `az webapp vnet-integration remove` on the site, `--vnet-route-all-enabled false`, and the
   worker rollback file from `infra/control-worker-composition` if workers were enabled.

Once attached, `AZURE_API_EGRESS_SUBNET_ID` must be present in the azd environment before any
`azd provision`: the generated module sets `virtualNetworkSubnetId` to null when the variable is empty,
which detaches the site and returns it to the platform pool. Workers would then fail closed at the SQL
bootstrap firewall rule rather than silently continue, but the detachment itself is not announced.

For staging, use `main.parameters.staging.json` only with the staging Control resource group and API
site. Read back the staging subnet attachment, route-all setting and health before enabling workers.
The `test` GitHub environment must retain `AZURE_API_EGRESS_SUBNET_ID` across later infra deployments.
The staging NAT address is the `SqlBootstrapIp` in the staging worker composition; the two profiles
must never be interchanged. A live SQL bootstrap through the managed-engine path remains the decisive
egress proof when the API container has no interactive shell.

Sources: [App Service regional VNet integration](https://learn.microsoft.com/en-us/azure/app-service/overview-vnet-integration),
[NAT gateway with App Service](https://learn.microsoft.com/en-us/azure/app-service/networking/nat-gateway-integration),
[NAT gateway pricing](https://azure.microsoft.com/pricing/details/azure-nat-gateway/).
