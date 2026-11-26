"""Re-apply the staging-only Pulumi provisioning module include after Aspire regeneration.

Aspire does not emit this module. The SQL SKU patch recreates the environment
parameter files, including enablePulumiProvisioning. This patch only restores
the subscription-template parameters and the conditional module include.
It is idempotent.
"""

from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MAIN = ROOT / "infra" / "main.bicep"

PULUMI_PARAMS = """@description('When true, deploy the staging-only Pulumi provisioning state backend, secrets key, and provisioner data-plane roles. Production stays false.')
param enablePulumiProvisioning bool = false

@description('Principal ID of the existing provisioner user-assigned identity. Required when enablePulumiProvisioning is true. Role assignments use this object ID, not the client ID.')
param pulumiProvisionerPrincipalId string = ''

@description('API egress subnet resource ID for the dedicated Pulumi seed vault firewall. Required when enablePulumiProvisioning is true. Unused in production.')
param pulumiApiEgressSubnetId string = ''

"""

PULUMI_MODULE = """
module control_pulumi_provisioning 'control-pulumi-provisioning/pulumi-provisioning.module.bicep' = if (enablePulumiProvisioning) {
  name: 'control-pulumi-provisioning'
  scope: rg
  params: {
    location: location
    provisionerPrincipalId: pulumiProvisionerPrincipalId
    apiEgressSubnetId: pulumiApiEgressSubnetId
    tags: tags
  }
}
"""

PARAMS_ANCHOR = "var resolvedSqlDatabaseSkuName"
MODULE_ANCHOR = "\noutput "


def patch_main(content: str) -> str:
    if "param enablePulumiProvisioning bool = false" not in content:
        if content.count(PARAMS_ANCHOR) != 1:
            raise SystemExit("Cannot find exactly one resolvedSqlDatabaseSkuName to insert Pulumi parameters.")
        content = content.replace(PARAMS_ANCHOR, PULUMI_PARAMS + PARAMS_ANCHOR, 1)
    if "module control_pulumi_provisioning" not in content:
        if MODULE_ANCHOR not in content:
            raise SystemExit("Cannot find an output block to insert the Pulumi module.")
        content = content.replace(MODULE_ANCHOR, PULUMI_MODULE + MODULE_ANCHOR, 1)
    return content


def main() -> None:
    if not MAIN.exists():
        return
    MAIN.write_text(patch_main(MAIN.read_text()))


if __name__ == "__main__":
    main()
