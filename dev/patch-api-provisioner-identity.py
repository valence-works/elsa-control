"""Re-apply the hand-maintained patches to the Aspire-generated API module and azd parameter template.

Every patch is an idempotent, anchored, single-occurrence replacement: if the patched text is
already present nothing changes; if the anchor is missing or ambiguous the script exits before
writing, so a regeneration never leaves a half-patched file behind.
"""
from pathlib import Path


def replace_once(content: str, patched: str, anchor: str, replacement: str, what: str) -> str:
    """Return content with anchor replaced by replacement, unless patched is already present."""
    if patched in content:
        return content
    if content.count(anchor) != 1:
        raise SystemExit(f"Cannot find exactly one {what} to patch.")
    return content.replace(anchor, replacement, 1)


def lines(*parts: str) -> str:
    return "\n".join(parts) + "\n"


def patch_module(path: Path) -> None:
    content = path.read_text()

    # Optional provisioner identity: attached only when the host supplies it.
    provisioner_parameter = "param provisioner_identity_outputs_id string = ''"
    provisioner_description = (
        "@description('Optional full resource ID of the dedicated Azure provider provisioner identity. "
        "The identity must be in the same Microsoft Entra tenant as this app; it may be hosted in "
        "another subscription. Empty preserves the existing API and ACR identity set.')"
    )
    identity_anchor = "param api_identity_outputs_clientid string\n"
    content = replace_once(
        content, provisioner_parameter, identity_anchor,
        f"{identity_anchor}\n{provisioner_description}\n{provisioner_parameter}\n",
        "generated API identity parameter anchor")

    # Optional static egress (#310): the site joins the delegated NAT subnet only when the host supplies it.
    egress_parameter = "param api_egress_subnet_id string = ''"
    egress_description = (
        "@description('Optional resource ID of the delegated App Service integration subnet from "
        "infra/control-egress that carries all API egress through one static NAT address. Empty keeps "
        "the platform outbound address pool.')"
    )
    content = replace_once(
        content, egress_parameter, f"{provisioner_parameter}\n",
        f"{provisioner_parameter}\n\n{egress_description}\n{egress_parameter}\n",
        "provisioner parameter anchor for the egress parameter")
    egress_properties = lines(
        "    keyVaultReferenceIdentity: api_identity_outputs_id",
        "    // Regional VNet integration for one static egress (#310); empty keeps the platform pool.",
        "    virtualNetworkSubnetId: empty(api_egress_subnet_id) ? null : api_egress_subnet_id",
        "    siteConfig: {",
        "      numberOfWorkers: 1",
        "      vnetRouteAllEnabled: !empty(api_egress_subnet_id)",
    )
    content = replace_once(
        content, egress_properties,
        lines("    keyVaultReferenceIdentity: api_identity_outputs_id", "    siteConfig: {", "      numberOfWorkers: 1"),
        egress_properties, "generated site properties anchor for the egress settings")

    old_identity = lines(
        "  identity: {",
        "    type: 'UserAssigned'",
        "    userAssignedIdentities: {",
        "      '${elsa_control_outputs_azure_container_registry_managed_identity_id}': { }",
        "      '${api_identity_outputs_id}': { }",
        "    }",
        "  }",
    )
    new_identity = lines(
        "  identity: {",
        "    type: 'UserAssigned'",
        "    // A user-assigned identity is a standalone resource and App Service supports multiple",
        "    // user-assigned identities. Keep the existing API/ACR identities as the default; the",
        "    // optional provisioner identity is only attached when explicitly supplied by the host.",
        "    // Same-tenant/cross-subscription use follows Microsoft's App Service managed-identity",
        "    // contract: https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity",
        "    userAssignedIdentities: union(",
        "      {",
        "        '${elsa_control_outputs_azure_container_registry_managed_identity_id}': { }",
        "        '${api_identity_outputs_id}': { }",
        "      },",
        "      empty(provisioner_identity_outputs_id)",
        "        ? { }",
        "        : {",
        "            '${provisioner_identity_outputs_id}': { }",
        "          })",
        "  }",
    )
    content = replace_once(content, new_identity, old_identity, new_identity, "generated API identity block")

    # Aspire generates Default authentication. Pin Catalog to the API identity instead of
    # allowing a credential chain to select a different attached or developer identity.
    old_catalog = 'Server=tcp:${control_sql_outputs_sqlserverfqdn},1433;Encrypt=True;Authentication="Active Directory Default";Database=Catalog'
    new_catalog = 'Server=tcp:${control_sql_outputs_sqlserverfqdn},1433;Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Managed Identity;User Id=${api_identity_outputs_clientid};Database=Catalog'
    content = replace_once(content, new_catalog, old_catalog, new_catalog, "generated Catalog authentication setting")
    if content.count(new_catalog) != 1 or old_catalog in content:
        raise SystemExit("Generated Catalog authentication setting is ambiguous.")

    path.write_text(content)


def patch_parameter_template(parameters_path: Path) -> None:
    """Re-add the optional host parameters to the regenerated azd parameter template (idempotent)."""
    if not parameters_path.exists():
        return  # module-only fixtures carry no template
    parameters = parameters_path.read_text()
    anchor = "param api_identity_outputs_id = '{{ .Env.API_IDENTITY_ID }}'\n"
    provisioner_block = (
        '{{ if index .Env "AZURE_PROVISIONER_IDENTITY_ID" }}\n'
        "param provisioner_identity_outputs_id = '{{ .Env.AZURE_PROVISIONER_IDENTITY_ID }}'\n"
        "{{ else }}\n"
        "param provisioner_identity_outputs_id = ''\n"
        "{{ end }}\n"
    )
    egress_block = (
        '{{ if index .Env "AZURE_API_EGRESS_SUBNET_ID" }}\n'
        "param api_egress_subnet_id = '{{ .Env.AZURE_API_EGRESS_SUBNET_ID }}'\n"
        "{{ else }}\n"
        "param api_egress_subnet_id = ''\n"
        "{{ end }}\n"
    )
    parameters = replace_once(
        parameters, "provisioner_identity_outputs_id", anchor, anchor + provisioner_block,
        "generated API identity parameter anchor in the parameter template")
    parameters = replace_once(
        parameters, "api_egress_subnet_id", provisioner_block, provisioner_block + egress_block,
        "provisioner parameter block to anchor the egress parameter")
    parameters_path.write_text(parameters)


patch_module(Path("infra/api/api-website.module.bicep"))
patch_parameter_template(Path("src/Hosting/ElsaControl.AppHost/infra/api/api.tmpl.bicepparam"))
