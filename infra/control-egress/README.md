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

Before previewing this mode, perform a read-only check of the existing network:

```sh
az network vnet show --subscription 8e23037a-420f-4ad0-9594-9d194de29e84 \
  --resource-group rg-valence-control-prod \
  --name vnet-valence-control-prod-bec \
  --query '{location:location,tags:tags,addressPrefixes:addressSpace.addressPrefixes,privateEndpointVNetPolicies:privateEndpointVNetPolicies,subnets:subnets[].{name:name,addressPrefix:addressPrefix,natGatewayId:natGateway.id,delegations:delegations,serviceEndpoints:serviceEndpoints,privateEndpointNetworkPolicies:privateEndpointNetworkPolicies,privateLinkServiceNetworkPolicies:privateLinkServiceNetworkPolicies,networkSecurityGroupId:networkSecurityGroup.id,routeTableId:routeTable.id}}' \
  --output json
```

The expected production shape is Belgium Central, the tags and address prefixes in `main.parameters.production.json`, exactly one `snet-api-egress` subnet with the configured prefix and existing NAT gateway, the App Service delegation, no service endpoints yet, `privateEndpointVNetPolicies` set to `Disabled`, and no subnet NSG or route table. Stop if any additional subnet, service endpoint, delegation, NAT target, NSG, route table, or unrepresented writable property is present. Do not guess that an incremental VNet update will preserve it.

Then run the read-only preview with the explicit narrow mode:

```sh
az deployment group what-if --subscription 8e23037a-420f-4ad0-9594-9d194de29e84 \
  --resource-group rg-valence-control-prod --name control-egress-keyvault-endpoint-preview \
  --template-file infra/control-egress/main.bicep \
  --parameters @infra/control-egress/main.parameters.production.json egressDeploymentMode=keyVaultEndpointOnly
```

For the production cutover, accept only one VNet `Modify` that adds the Key Vault service endpoint; provider normalization of the endpoint `locations` is `NoEffect`. NAT, Public IP, and unrelated resources must be `Ignore`. Stop on another action or any VNet property change beyond the endpoint and that `NoEffect` metadata. A what-if is not approval to apply: production still needs the separate #911 approval and a fresh reviewed preview. This issue authorizes no production apply.

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
