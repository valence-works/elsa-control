from pathlib import Path


path = Path("infra/api/api-website.module.bicep")
content = path.read_text()
parameter = "param provisioner_identity_outputs_id string = ''"
description = (
    "@description('Optional full resource ID of the dedicated Azure provider provisioner identity. "
    "The identity must be in the same Microsoft Entra tenant as this app; it may be hosted in "
    "another subscription. Empty preserves the existing API and ACR identity set.')"
)

if parameter not in content:
    anchor = "param api_identity_outputs_clientid string\n"
    if anchor not in content:
        raise SystemExit("Cannot find the generated API identity parameter anchor.")
    content = content.replace(anchor, f"{anchor}\n{description}\n{parameter}\n", 1)

old_identity = "\n".join(
    [
        "  identity: {",
        "    type: 'UserAssigned'",
        "    userAssignedIdentities: {",
        "      '${elsa_control_outputs_azure_container_registry_managed_identity_id}': { }",
        "      '${api_identity_outputs_id}': { }",
        "    }",
        "  }",
    ]
)
new_identity = "\n".join(
    [
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
    ]
)
if new_identity not in content:
    if old_identity not in content:
        raise SystemExit("Cannot find the generated API identity block to patch.")
    content = content.replace(old_identity, new_identity, 1)

# Aspire generates Default authentication. Pin Catalog to the API identity instead of
# allowing a credential chain to select a different attached or developer identity.
old_catalog = 'Server=tcp:${control_sql_outputs_sqlserverfqdn},1433;Encrypt=True;Authentication="Active Directory Default";Database=Catalog'
new_catalog = 'Server=tcp:${control_sql_outputs_sqlserverfqdn},1433;Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Managed Identity;User Id=${api_identity_outputs_clientid};Database=Catalog'
if new_catalog not in content:
    if content.count(old_catalog) != 1:
        raise SystemExit("Cannot find the generated Catalog authentication setting to patch.")
    content = content.replace(old_catalog, new_catalog, 1)
if content.count(new_catalog) != 1 or old_catalog in content:
    raise SystemExit("Generated Catalog authentication setting is ambiguous.")

path.write_text(content)

# The azd parameter template is regenerated too; re-add the optional provisioner parameter so the
# host can attach the identity without editing generated files by hand.
parameters_path = Path("src/Hosting/ElsaControl.AppHost/infra/api/api.tmpl.bicepparam")
if not parameters_path.exists():
    raise SystemExit(0)  # module-only fixtures (tests) have no parameter template to maintain
parameters = parameters_path.read_text()
provisioner_block = (
    '{{ if index .Env "AZURE_PROVISIONER_IDENTITY_ID" }}\n'
    "param provisioner_identity_outputs_id = '{{ .Env.AZURE_PROVISIONER_IDENTITY_ID }}'\n"
    "{{ else }}\n"
    "param provisioner_identity_outputs_id = ''\n"
    "{{ end }}\n"
)
if provisioner_block not in parameters:
    parameter_anchor = "param api_identity_outputs_id = '{{ .Env.API_IDENTITY_ID }}'\n"
    if parameters.count(parameter_anchor) != 1:
        raise SystemExit("Cannot find the generated API identity parameter anchor in the parameter template.")
    parameters = parameters.replace(parameter_anchor, parameter_anchor + provisioner_block, 1)
    parameters_path.write_text(parameters)
