# Aspire Deployment to Azure App Service

## Recommendation

Use the checked-in `scripts/deploy-azure-elsa-control.sh` helper as the
repeatable deployment path. The Aspire AppHost generates the reviewed Bicep
under `infra/`; the helper applies that subscription-scoped base, builds and
pushes the API image, resolves its immutable digest, and applies the generated
App Service module.

The previous manually provisioned Web App can be deleted once anything important
has been backed up.

## Local Tooling

Install the current .NET 10 SDK before deploying. As of May 15, 2026, the
current .NET 10 SDK line is `10.0.300`, released May 12, 2026.

The official Aspire templates are installed with:

```bash
dotnet new install Aspire.ProjectTemplates
dotnet tool install -g Aspire.Cli
```

On this machine, `~/.dotnet/tools/aspire` is `13.3.2`. If `aspire --version`
prints an older version, move `~/.dotnet/tools` before `~/.aspire/bin` in
`PATH`, or run `~/.dotnet/tools/aspire` explicitly.

## Deploy

Run `dev/regenerate-infra.sh` only when the AppHost resource model changes, and
review the generated diff before committing it. For deployment commands and
the first-run sequence, see
[Azure Elsa Control Deployment Plan](azure-elsa-control-deployment-plan.md).

## GitHub Actions Deployment

The `Azure Control API Deploy` workflow is manually dispatched from GitHub Actions.
Its existing `deploy_mode: app` path builds the console, builds the API container with
the console static assets mounted under `/admin`, pushes it to ACR, and updates the
existing App Service container:

```bash
docker build --file src/Hosting/ElsaControl.Api/Dockerfile --tag <acr>/<repo>:<sha> .
docker push <acr>/<repo>:<sha>
az webapp sitecontainers update ...
```

The Dockerfile uses a Node build stage for `src/Hosting/ElsaControl.Console` and copies
the Vite `dist` output into `src/Hosting/ElsaControl.Api/wwwroot/admin` before
`dotnet publish`. ASP.NET Core serves `/admin` as the console SPA and keeps the
admin API endpoints under `/api/admin`.

This is the fast path for application-only updates because the Azure resources
are expected to already exist. It also avoids reapplying the App Service Bicep
module on every code change. If the AppHost infrastructure shape changes, run
the same workflow manually and choose `deploy_mode: infra`; that path runs the
checked-in deployment helper:

```bash
scripts/deploy-azure-elsa-control.sh \
  --environment "$AZURE_ENV_NAME" \
  --resource-group "$AZURE_RESOURCE_GROUP" \
  --location "$AZURE_LOCATION" \
  --subscription "$AZURE_SUBSCRIPTION_ID" \
  --image-tag "$GITHUB_SHA"
```

The helper applies the current subscription-scoped Aspire-generated base Bicep,
pushes the API image, resolves it to an immutable digest, and applies the generated
App Service module. Keep infrastructure deployment as a manual choice so routine
code changes do not reapply Azure resources on every push.

### Staged immutable promotion

Choose `deploy_mode: build` from the `main` branch to run the required checks, build and push one candidate,
resolve its ACR manifest digest, and upload a safe candidate descriptor. This mode does
not inspect or mutate the Web App and does not run production startup migrations.

Choose `deploy_mode: promote` only after the controlled Catalog PITR rehearsal has been
performed by the operator. Supply the successful same-repository build run ID and the
descriptor's exact `sha256:` digest. The workflow verifies the descriptor, successful
workflow run, source commit ancestry to `main`, repository binding, and digest, and
confirms that the digest is still present in the configured ACR before capturing the
current deployment. It then deploys the exact `repository@sha256:digest` reference
without rebuilding or retagging.

The rehearsal remains an external operational gate: it must use a uniquely owned Catalog
PITR clone, the candidate image in Production/SQL Server mode, the API managed identity
(or an explicitly bootstrapped rehearsal identity), startup migration/integrity checks,
and the previous image against the migrated clone. A successful build descriptor or
workflow promotion does not claim that rehearsal was performed. Image rollback restores
the captured immutable image and settings only; it never reverses database migrations.

Configure the workflow in a GitHub environment named `production` unless you
change the workflow environment name. With OIDC, the Microsoft Entra federated
credential should trust this repository and environment. If using the default
GitHub environment subject, it is:

```text
repo:<owner>/<repo>:environment:production
```

The production environment must also enforce a selected-branch policy for `main`.
The workflow repeats this as a defense-in-depth check before Azure login for every
mutating mode; the protected GitHub environment policy remains the authoritative
remote boundary and must not be replaced by the workflow check.

### Optional Azure provider provisioner identity

The API App Service can optionally attach a dedicated provisioner user-assigned
managed identity by setting the exact full resource ID in
`AZURE_PROVISIONER_IDENTITY_ID` before the API site parameters are rendered. The
identity must belong to the same Microsoft Entra tenant as the App Service; a
cross-subscription resource ID is only valid when that tenant and the required
provider permissions have been independently verified. See Microsoft's
[App Service managed identity documentation](https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity).

Attaching or changing a user-assigned identity changes App Service configuration
and restarts the app. Schedule the change with a health/readback check. The
attachment does not change the API runtime identity: `AZURE_CLIENT_ID` remains
the API identity client ID and `keyVaultReferenceIdentity` remains the API
identity resource ID. Configure the provider runner's explicit client ID
separately when enabling provider operations; do not assume that attaching an
identity selects it for every Azure SDK call.

The current production host uses its existing classic `DOCKER` deployment mode.
Do not convert it to `SITECONTAINERS` by redeploying the full generated template
just to attach this identity. Use the intended staged identity update, preserve
the exact `AZURE_PROVISIONER_IDENTITY_ID` GitHub environment variable for later
infrastructure deployments, and verify that the deployed identity set contains
the existing API/ACR identities plus only the explicitly supplied provisioner.

Required GitHub Actions variables:

- `AZURE_CLIENT_ID`: client ID of the deploy identity
  `valence_control_contributor_mi-m5uymkuaf222o`, declared in
  `infra/control-deploy-identity/` together with its GitHub federated credential and
  exact deploy roles. Never delete that identity and do not re-run the bootstrap script
  against `production` (it would replace the client id); see its README.
- `AZURE_TENANT_ID`: Microsoft Entra tenant ID.
- `AZURE_SUBSCRIPTION_ID`: target Azure subscription ID.
- `AZURE_ENV_NAME`: Bicep environment name. `infra/main.bicep` owns the resource
  group `rg-<AZURE_ENV_NAME>`; for example, `test` maps to `rg-test`.
- `AZURE_LOCATION`: Azure region for the environment, for example
  `westeurope`.
- `AZURE_RESOURCE_GROUP`: resource group containing the deployed App Service,
  and exactly `rg-<AZURE_ENV_NAME>`, for example `rg-test`.
- `AZURE_WEBAPP_NAME`: API App Service name, for example `api-k35qdj734hds2`.
- `AZURE_CONTAINER_REGISTRY_ENDPOINT`: ACR login server for app image pushes,
  for example `elsacontrolacrk35qdj734hds2.azurecr.io`.
- `CONTROL_ENTRA_CLIENT_ID`: client ID of the environment's Control operator app.
- `CONTROL_ENTRA_TENANT_ID`: tenant ID of the environment's Control operator app.
- Optional `CLOUD_ACCOUNT_ISSUER`: exact Supabase Auth issuer for the matching Cloud
  environment, for example `https://<project-ref>.supabase.co/auth/v1`.
- `EXPECTED_CLOUD_ACCOUNT_ISSUER`: the separately stored, approved issuer for
  this GitHub environment. It must be established independently before running
  the bootstrap script and exactly match `CLOUD_ACCOUNT_ISSUER`.
- Optional `AZURE_PROVISIONER_IDENTITY_ID`: the exact full resource ID of the
  dedicated provider provisioner identity when the staged attachment is enabled.
- Optional `AZURE_API_EGRESS_SUBNET_ID`: the exact resource ID of the delegated App Service
  integration subnet created by `infra/control-egress` (#310). When set, the API site joins that
  subnet with regional VNet integration and routes all outbound traffic through its NAT gateway, so
  the API has one static egress address for the provider runner's SQL bootstrap firewall rule.
  Empty keeps the platform outbound address pool. Attaching or detaching restarts the app once.
  Once the production site is attached, keep this variable set in the GitHub
  environment: an infrastructure deployment without it renders
  `virtualNetworkSubnetId` as null and detaches the site.

Required GitHub Actions secrets:

- `ADMIN_API_KEY`: strong API key passed to the AppHost `adminApiKey` parameter
  and surfaced to the API as `Authentication__ApiKey`.
- `BUILDER_CLIENT_API_KEY`: client key used by the public builder endpoint.
- `CONTROL_ENTRA_CLIENT_SECRET`: secret for the environment's Control operator app.

The persistent `test` environment also requires the staging-only Stripe values
`STRIPE_TEST_SECRET_KEY` and `STRIPE_TEST_WEBHOOK_SIGNING_SECRET`, plus the
non-secret variables `STRIPE_HOSTED_PRICE_ID` and
`ELSA_CLOUD_STAGING_ORIGIN`. The Stripe key must be test mode. The normal
deployment workflow passes these values only to the configuration preflight and
the staging reconciliation step; they are not job-wide environment variables.

Each non-build deployment to `test` runs
`scripts/staging_stripe_reconcile.py --apply-azure-settings`. It fails closed
unless the configured Hosted price is the active €99 monthly test price, the
exact Control webhook is enabled for the admitted checkout/subscription event
set, the Checkout callbacks resolve to the staging Cloud routes, and the active
Customer Portal policy supports invoice history, payment-method updates, and
period-end cancellation. Azure settings are applied from a mode-0600 temporary
JSON file so credentials do not appear in the command line or logs. A failed
post-write verification restores the prior billing-setting values and removes
settings that were previously absent. The reconciler refuses to mutate these
managed keys if Azure marks any of them as deployment-slot settings, because
silently clearing slot stickiness would change swap behavior.
The same staging reconciliation enables the billing lifecycle worker with a
15-second poll interval so confirmed Stripe test cleanup can reach `Deleted`
before a new paid Hosted checkout; these worker settings remain staging-only.

If the first `infra` run creates a new test environment and a later deployment,
reconciliation, or health gate fails, there is no prior runtime image to
restore. The workflow reports that case explicitly and retains the isolated,
empty test resources for diagnosis. Correct the failed gate and rerun the
idempotent `infra` deployment. Remove the test resource group only through the
environment teardown procedure; the deployment workflow does not guess that a
partially provisioned resource group is safe to delete.

Before building or deploying, the workflow runs the same Stripe resource audit
without reading or changing Azure. The staging-only helper accepts Control hosts
under `azurewebsites.net` and Cloud hosts under `azurestaticapps.net`; a public
production origin therefore cannot satisfy the staging contract. Stripe list
checks follow pagination, and the one-time webhook and portal bootstraps use
stable idempotency keys so a safe retry cannot silently create a duplicate.

For a new test Stripe account, create the webhook once from a trusted operator
machine and capture its one-time secret in a private file. The command refuses
live keys, existing or ambiguous endpoints, and stdout as a secret destination:

```bash
STRIPE_SECRET_KEY='<test-secret>' \
STRIPE_HOSTED_PRICE_ID='<test-price-reference>' \
CONTROL_STAGING_WEBHOOK_URL='https://<staging-control-host>/api/billing/webhooks/stripe' \
CLOUD_PORTAL_RETURN_URL='https://<staging-cloud-host>/dashboard' \
AZURE_RESOURCE_GROUP='<staging-resource-group>' \
AZURE_WEBAPP_NAME='<staging-webapp>' \
python3 scripts/staging_stripe_reconcile.py \
  --bootstrap-webhook-secret /private/path/control-staging-webhook.secret \
  --bootstrap-portal
```

Store the new signing secret as the protected GitHub `test` environment secret,
then delete the local file. The portal bootstrap is idempotent when one valid
active test configuration already exists. Product and price creation remains an
explicit commercial bootstrap decision; the reconciler verifies the reviewed
price rather than silently creating or changing it.

The workflow validates the configuration, restores the solution, builds the
Aspire AppHost, runs the API test project, signs in to Azure with GitHub
federated credentials, and then deploys either the application container or the
current repository-owned Bicep infrastructure path. Secure parameters are written
only to temporary mode-0600 files and removed when the helper exits.

On the first deployment of a new environment, run the helper with `--base-only`,
follow the contained-user runbook below, and then run the full helper to create
the Web App. The generated
Aspire SQL-role deployment script is intentionally excluded because its upstream
PowerShell dependency is incompatible with the current deployment image. This is
the only manual first-run database step; later infrastructure and application
deployments reuse that identity and grant.

### First-run Catalog contained user

Run this from a trusted operator machine with Azure CLI, Python 3, and the Go
`sqlcmd` installed. Supply one exact public IPv4 address for the temporary
firewall rule. The cleanup trap restores the generated SQL administrator and
removes that firewall rule even when `sqlcmd` fails.

```bash
set -euo pipefail
export AZURE_ENV_NAME=test
export AZURE_RESOURCE_GROUP="rg-$AZURE_ENV_NAME"
export AZURE_SUBSCRIPTION_ID='<target-subscription-id>'
export SQL_BOOTSTRAP_IP='<exact-public-ipv4>'

az account set --subscription "$AZURE_SUBSCRIPTION_ID"

python3 - "$SQL_BOOTSTRAP_IP" <<'PY'
import ipaddress, sys
address = ipaddress.ip_address(sys.argv[1])
if address.version != 4 or not address.is_global:
    raise SystemExit("SQL_BOOTSTRAP_IP must be one exact public IPv4 address")
PY

deployment_name="elsa-control-$AZURE_ENV_NAME"
outputs="$(az deployment sub show --subscription "$AZURE_SUBSCRIPTION_ID" --name "$deployment_name" --query properties.outputs -o json)"
api_identity_id="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["API_IDENTITY_ID"]["value"])' <<<"$outputs")"
api_client_id="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["API_IDENTITY_CLIENTID"]["value"])' <<<"$outputs")"
sql_fqdn="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["CONTROL_SQL_SQLSERVERFQDN"]["value"])' <<<"$outputs")"
api_identity_name="${api_identity_id##*/}"
sql_server_name="${sql_fqdn%%.*}"

original_admin="$(az sql server ad-admin list --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" --server-name "$sql_server_name" --query '[0]' -o json)"
original_login="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["login"])' <<<"$original_admin")"
original_sid="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["sid"])' <<<"$original_admin")"
# `az ad` is tenant-scoped; the selected subscription above pins the intended tenant.
operator_id="$(az ad signed-in-user show --query id -o tsv)"
operator_login="$(az ad signed-in-user show --query userPrincipalName -o tsv)"

sql_file="$(mktemp)"
firewall_rule="CatalogBootstrap-$(date +%s)"
admin_changed=false
firewall_created=false
cleanup() {
  set +e
  if [[ "$firewall_created" == true ]]; then
    az sql server firewall-rule delete --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" --server "$sql_server_name" --name "$firewall_rule" >/dev/null
  fi
  if [[ "$admin_changed" == true ]]; then
    az sql server ad-admin create --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" --server-name "$sql_server_name" --display-name "$original_login" --object-id "$original_sid" >/dev/null
  fi
  rm -f "$sql_file"
}
trap cleanup EXIT

python3 - "$api_identity_name" "$api_client_id" "$sql_file" <<'PY'
import os, re, sys, uuid
name, client_id, path = sys.argv[1:]
if not re.fullmatch(r"[A-Za-z0-9._-]{1,128}", name):
    raise SystemExit("Unsafe API identity name")
uuid.UUID(client_id)
template = open("infra/azure-production/sql-bootstrap.sql", encoding="utf-8").read()
payload = template.replace("__WORKLOAD_IDENTITY_NAME__", name).replace("__WORKLOAD_IDENTITY_CLIENT_ID__", client_id)
fd = os.open(path, os.O_WRONLY | os.O_TRUNC, 0o600)
with os.fdopen(fd, "w", encoding="utf-8") as handle:
    handle.write(payload)
PY

admin_changed=true
az sql server ad-admin create --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" --server-name "$sql_server_name" --display-name "$operator_login" --object-id "$operator_id" >/dev/null
firewall_created=true
az sql server firewall-rule create --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" --server "$sql_server_name" --name "$firewall_rule" --start-ip-address "$SQL_BOOTSTRAP_IP" --end-ip-address "$SQL_BOOTSTRAP_IP" >/dev/null
sqlcmd -S "tcp:$sql_fqdn,1433" -d Catalog --authentication-method ActiveDirectoryAzCli -N true -b -i "$sql_file"
```

Wait for the cleanup commands to finish, then verify that the SQL administrator
matches the original generated identity and that the temporary firewall rule is
absent before running the full deployment helper.

### Multi-tenant Microsoft Entra sign-in (guided design partners)

Control can accept work or school sign-ins from customer Microsoft Entra tenants, so a
design partner signs in with its own Entra identity. This is Preview, guided onboarding:
Valence sets it up per partner; it is not self-serve and not an Azure Marketplace
offer. Signing in only creates the partner's organization. It does not bind an Azure
subscription or grant any entitlement.

It is off by default. Production currently pins a single Valence tenant through
`Authentication__ControlIdentity__Authority` and `__Issuer`. To enable it:

- Operator: change the Control Entra app registration's sign-in audience to accounts in any
  organizational directory, and have each partner's tenant administrator grant consent.
- `Authentication__ControlIdentity__Provider=MicrosoftEntra`
- `Authentication__ControlIdentity__Authority=https://login.microsoftonline.com/organizations/v2.0`
- `Authentication__ControlIdentity__Issuer` removed (empty). Each token is validated against
  `https://login.microsoftonline.com/{tid}/v2.0` for its own `tid`, and personal Microsoft
  accounts are rejected.
- `Authentication__ControlIdentity__Entra__MultiTenant=true`
- `Authentication__ControlIdentity__Entra__DogfoodTenantIds__0=<Valence tenant id>` (one entry per
  Valence-operated tenant).
- `Authentication__Admin__AllowAuthenticatedCustomerSession=false`. Startup refuses anything else,
  because otherwise any customer tenant could administer Control.

Behaviour once enabled:

- Dogfood tenants keep today's identity key, `https://login.microsoftonline.com/{tid}/v2.0`
  plus the configured subject claim, so existing Valence accounts still resolve. They never
  take the customer mint.
- A customer tenant's first sign-in creates one organization bound to its `tid`, plus a default
  shared workspace. The organization is named after the sign-in domain. The first user owns both.
  Every later user from that tenant joins that organization as a member with no workspace access,
  and owners grant workspaces through the existing membership endpoints. A tenant never gets a
  second organization from sign-in, and a sign-in never merges into an existing Stripe or
  personal organization.
- Customer accounts are keyed by the tenant issuer and the Entra object id (`oid`). Display
  claims such as `preferred_username`, `email` and `name` are never keys.
- The `control_admin` role is honoured only from dogfood tenants. A customer tenant
  administrator can assign the app's roles inside their own tenant.

## GitHub/Azure Bootstrap

Use the bootstrap script to recreate or refresh a GitHub environment from the
matching subscription deployment:

```bash
scripts/bootstrap-github-azure.sh \
  --environment test \
  --azure-environment test \
  --resource-group rg-test
```

The script:

- creates the GitHub environment if needed;
- creates or reuses a dedicated Microsoft Entra application and its GitHub
  environment federated credential;
- reads the current subscription deployment outputs and deployed Web App;
- sets the required GitHub environment variables with `gh variable set`;
- sets supplied deployment secrets with `gh secret set` without printing values.

When Cloud JWT admission is enabled, first establish
`EXPECTED_CLOUD_ACCOUNT_ISSUER` independently in the protected GitHub
environment. Then supply the matching `CLOUD_ACCOUNT_ISSUER` to the bootstrap
process. Bootstrap reads and compares the approved value; it never creates or
overwrites that approval variable. To disable Cloud JWT admission, run bootstrap
with `--disable-cloud-account-issuer`; this removes only the active issuer and
retains the approved value for a later reviewed re-enable.

For a preview that does not modify Azure or GitHub:

```bash
scripts/bootstrap-github-azure.sh --environment test --azure-environment test --dry-run
```

Supply the Control identity values and secrets in the process environment when
bootstrapping infrastructure delivery:

```bash
ADMIN_API_KEY='<strong-secret>' \
BUILDER_CLIENT_API_KEY='<strong-secret>' \
CONTROL_ENTRA_CLIENT_ID='<client-id>' \
CONTROL_ENTRA_TENANT_ID='<tenant-id>' \
CONTROL_ENTRA_CLIENT_SECRET='<client-secret>' \
scripts/bootstrap-github-azure.sh --environment test --azure-environment test
```

## Removing Existing Resources

If the old resources are in a dedicated resource group, delete the group:

```bash
az group delete --name <old-resource-group>
```

If the group has shared resources, delete only the old Web App, plan, registry,
storage account, and related managed identities after confirming they are not
used elsewhere.

## Database Provider

The API supports two EF Core providers:

- `Database:Provider=Sqlite`
- `Database:Provider=SqlServer`

Local development defaults to SQLite. Aspire publish mode provisions Azure SQL,
injects `ConnectionStrings__Catalog`, and sets `Database__Provider=SqlServer`.

The checked-in Azure template and regeneration patch explicitly select
`Authentication=Active Directory Managed Identity;User Id=<API identity client ID>`
with `Encrypt=True;TrustServerCertificate=False`. Catalog authentication must use
the API identity, not the optional customer-workload provisioner or ACR identity.
There is no credential-chain fallback on this path. Other explicitly configured
SQL authentication modes and SQLite keep their existing behavior; an explicit
managed-identity connection without a User Id selects the system-assigned identity.

For explicit managed-identity Catalog connections, the host supplies a stable
SqlClient token callback while EF owns each connection. The callback validates
the configured identity and public Azure SQL resource, acquires tokens only in
memory, and supports cancellation and token refresh. Before EF opens a tracked
connection, the interceptor rejects changes to its callback or connection string
(including target and TLS settings). SQL connection and EF execution retries
remain enabled.

The credential policy preserves Azure.Core 1.60.0 managed-identity retry behavior,
including token 404/410 handling and the extended 410 delay, except that a 404 from
the optional IMDS capability probe is not retried. Retrying that unsupported
endpoint delayed token acquisition in the candidate rehearsal. Do not replace
this with globally disabled retries, a dependency downgrade or weaker TLS. Recheck
the policy tests when upgrading Azure.Core; see [its versioned policy source](https://github.com/Azure/azure-sdk-for-net/tree/Azure.Core_1.60.0/sdk/core/Azure.Core/src/Identity/Policies).

**Existing installations:** app-only immutable promotion does not update app
settings. A deployment still using `Active Directory Default` must explicitly
align its Catalog setting to the reviewed API identity as part of the controlled
rollout. First prove the candidate's full startup/migration on a Catalog clone
and the retained prior image against the migrated clone using the intended MI
configuration. Capture the prior image and settings for rollback, preserve the
server/database and retry/TLS values, and change only the intended authentication
selection. Do not deploy the entire infrastructure merely to change this setting.
An image rollback cannot undo schema migrations; token-only or SQL-open tests do
not satisfy the full rehearsal gate. No live rollout is claimed by these instructions.

Each provider has its own EF Core migration assembly:

- SQLite: `ElsaControl.PackageCatalog.Persistence.SqliteMigrations`
- SQL Server/Azure SQL: `ElsaControl.PackageCatalog.Persistence.SqlServerMigrations`

The API selects the matching migration assembly with the provider and applies
migrations at startup outside the `Testing` environment.

SQL Server connections default to a 120-second connect timeout with provider
retry enabled. This gives Azure SQL room to complete slow post-login handshakes,
for example after an idle/serverless database resumes. Override
`Database__SqlServer__ConnectTimeoutSeconds` or an explicit `Connect Timeout`
connection-string value only when the target database has a known lower latency
profile.

SQLite remains fine for local development and single-process test runs. For
production and App Service scale-out, use Azure SQL. SQLite on shared App
Service storage or Azure Files is not a good production target because SQLite
depends on filesystem locking, and WAL mode does not support clients on
different machines through a network filesystem.
