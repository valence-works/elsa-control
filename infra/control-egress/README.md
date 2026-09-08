# Control API static egress

The managed-instance provider runner bootstraps each instance's Azure SQL server through a temporary
firewall rule for exactly one IPv4 address (`Deployment:AzureProvider:Runner:SqlBootstrapIp`, which
refuses `0.0.0.0`). The production API on App Service egresses from a shared pool of fourteen
non-static addresses, so in-process bootstrap would be intermittent. This template (#310, part of
#264) gives the API one static egress: a small VNet in the Control resource group, a subnet delegated
to App Service, and a NAT gateway with a Standard static public IP. The API site joins the subnet
with regional VNet integration and routes all outbound traffic through it.

Nothing inbound changes. No customer compute is created and the customer workload subscription is not
touched. The Basic (B1) plan supports regional VNet integration; no plan change is needed.

| File | Purpose |
| --- | --- |
| `main.bicep` | VNet, delegated subnet, NAT gateway, static public IP; outputs the subnet id and the egress address. |
| `main.parameters.production.json` | Production names (`vnet-valence-control-prod-weu`, `snet-api-egress`, `natgw-valence-control-prod-weu`, `pip-natgw-valence-control-prod-weu`) and address space (`10.60.0.0/24`, subnet `/26`). |

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
     --vnet vnet-valence-control-prod-weu --subnet snet-api-egress
   az webapp config set --subscription 8e23037a-420f-4ad0-9594-9d194de29e84 \
     --resource-group rg-valence-control-prod --name api-m5uymkuaf222o \
     --vnet-route-all-enabled true --query vnetRouteAllEnabled
   ```
   Then verify health and that an outbound probe from the site reports the NAT address (for example
   the Kudu console `curl -s https://api.ipify.org`, or the App Service `outboundIpAddresses` no longer
   being the effective path). The runner preflight against a disposable SQL server is the final check
   (#310 acceptance).
4. Rollback: `az webapp vnet-integration remove` on the site, `--vnet-route-all-enabled false`, and the
   worker rollback file from `infra/control-worker-composition` if workers were enabled.

Sources: [App Service regional VNet integration](https://learn.microsoft.com/en-us/azure/app-service/overview-vnet-integration),
[NAT gateway with App Service](https://learn.microsoft.com/en-us/azure/app-service/networking/nat-gateway-integration),
[NAT gateway pricing](https://azure.microsoft.com/pricing/details/azure-nat-gateway/).
