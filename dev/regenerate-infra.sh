#!/usr/bin/env bash
# Regenerates the Aspire infrastructure into infra/, preserves the manually authored
# Azure authorities, and re-applies the local patches we carry.
#
# Why the patch: Aspire emits an `api-roles-control-sql` deployment script to grant the API's managed
# identity access to Azure SQL. That script installs SqlServer PowerShell 22.3.0 onto Az PowerShell
# 14.0, where Invoke-Sqlcmd throws MissingMethodException before it reaches SQL, failing the whole
# provision. Aspire exposes no opt-out (WithRoleAssignments covers only Container Registry and
# Storage), and the same script is emitted by 13.3.5 and 13.4.6 alike. The contained SQL user is
# created out of band instead -- see docs/deployment/azure-app-service.md.
#
# Run this after changing AppHost.cs, then `azd provision` / `azd deploy`.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "Regenerating infrastructure from the Aspire AppHost..."
preserved_infra_paths=(
    azure-production
    azure-workload-proof
    azure-customer-subscription
    managed-telemetry
    control-deploy-identity
    control-worker-composition
)
preservation_dir="$(mktemp -d)"
restore_preserved_infra() {
    local command_status=$?
    local restore_status=$command_status
    trap - EXIT
    if [ -d "$preservation_dir" ]; then
        mkdir -p infra
        for relative_path in "${preserved_infra_paths[@]}"; do
            if [ ! -e "$preservation_dir/$relative_path" ]; then
                continue
            fi
            if [ -e "infra/$relative_path" ]; then
                echo "Refusing to overwrite a generated infra/$relative_path path; preserved copy remains at $preservation_dir" >&2
                restore_status=1
                continue
            fi
            mkdir -p "infra/$(dirname "$relative_path")"
            mv "$preservation_dir/$relative_path" "infra/$relative_path"
        done
    fi
    if [ "$restore_status" -eq 0 ]; then
        rm -rf "$preservation_dir"
    else
        echo "Preserved infrastructure remains at $preservation_dir" >&2
    fi
    exit "$restore_status"
}
trap restore_preserved_infra EXIT
for relative_path in "${preserved_infra_paths[@]}"; do
    if [ -e "infra/$relative_path" ]; then
        mkdir -p "$preservation_dir/$(dirname "$relative_path")"
        mv "infra/$relative_path" "$preservation_dir/$relative_path"
    fi
done
rm -rf infra
azd infra generate --force --no-prompt

echo "Re-applying the optional API provisioner identity parameter..."
python3 dev/patch-api-provisioner-identity.py

if [ -d infra/api-roles-control-sql ]; then
    echo "Stripping the broken api-roles-control-sql module..."
    python3 - <<'PY'
marker = "module api_roles_control_sql 'api-roles-control-sql/api-roles-control-sql.module.bicep' = {"
note = (
    "// NOTE: the Aspire-generated api-roles-control-sql module is removed by dev/regenerate-infra.sh.\n"
    "// Its deployment script is broken upstream (SqlServer PowerShell 22.3.0 on Az PowerShell 14.0),\n"
    "// so the API identity's contained SQL user is created out of band instead.\n"
)
with open("infra/main.bicep") as handle:
    content = handle.read()
start = content.index(marker)
end = content.index("\n}\n", start) + len("\n}\n")
with open("infra/main.bicep", "w") as handle:
    handle.write(content[:start] + note + content[end:])
PY
    rm -rf infra/api-roles-control-sql
fi

az bicep build --file infra/main.bicep --stdout > /dev/null
echo "infra/ regenerated and patched."
