# Azure Elsa Control Deployment Plan

> The active infrastructure is generated from the Aspire AppHost into `infra/` and then patched
> by `dev/regenerate-infra.sh` for the reviewed identity and networking requirements. The older
> hand-written stack remains under `infra-legacy/` for reference only.

## Objective

Deploy Elsa Control repeatably into any Azure subscription from repository-owned infrastructure as code. The first production deployment unit is the Elsa Control API container, including the built Console assets under `/admin`.

The deployment must be reproducible without depending on an existing `azd` environment. Aspire/azd remains useful for developer-driven deployments, but the subscription-neutral path is Bicep plus an explicit container image build/push step.

## Target Architecture

- Azure Resource Group per environment.
- Azure Container Registry for the platform API image.
- Linux Azure App Service Plan.
- Linux Web App running the Elsa Control API image built from `src/Hosting/ElsaControl.Api/Dockerfile`.
- Azure SQL logical server and catalog database.
- Application Insights and Log Analytics for runtime telemetry.
- Dedicated user-assigned Web App and ACR-pull identities.

The API runs with:

- `ASPNETCORE_ENVIRONMENT=Production`
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so ASP.NET Core uses App Service forwarded scheme/host headers for HTTPS-aware auth, redirects, and same-origin checks
- `Database__Provider=SqlServer`
- `ConnectionStrings__Catalog=<Azure SQL connection string>`
- `Authentication__ApiKey=<strong deployment secret>`
- optional `Authentication__BuilderClientApiKey=<strong deployment secret>`

The API applies EF Core SQL Server migrations at startup outside the `Testing` environment.

## Deployment Flow

1. Select target subscription, resource group, location, and environment name.
2. Provision or update the subscription-scoped base resources from `infra/main.bicep`.
   For a new environment, use `--base-only`, grant the generated API identity a
   contained user in `Catalog`, and then run the full helper.
3. Build the API container with the Console baked in. The helper script targets `linux/amd64` by default so local Apple Silicon builds run correctly on Linux App Service.
4. Push the image to the provisioned Azure Container Registry.
5. Resolve the pushed image to its immutable digest and deploy
   `infra/api/api-website.module.bicep` into the environment resource group.
6. Verify `/health` and `/admin`.

Use the helper script for the full flow:

```bash
AZURE_SUBSCRIPTION_ID=<subscription-id> \
ADMIN_API_KEY='<strong-secret>' \
BUILDER_CLIENT_API_KEY='<strong-secret>' \
CONTROL_ENTRA_TENANT_ID='<tenant-id>' \
CONTROL_ENTRA_CLIENT_ID='<client-id>' \
CONTROL_ENTRA_CLIENT_SECRET='<client-secret>' \
scripts/deploy-azure-elsa-control.sh \
  --environment prod \
  --resource-group rg-prod \
  --location westeurope
```

For an infrastructure preview:

```bash
scripts/deploy-azure-elsa-control.sh --environment prod --resource-group rg-prod --what-if
```

## Environment Strategy

Use one GitHub Actions environment per Azure resource group:

| GitHub environment | Azure environment | Example resource group | Notes |
| --- | --- | --- |
| `development` | `dev` | `rg-dev` | Lower SKU, disposable data. |
| `test` | `test` | `rg-test` | Production-like config for release validation. |
| `production` | `prod` | `rg-prod` | Strong secrets, backups, access review. |

Every environment should set a distinct `environmentName` parameter. Resource names are derived from that value plus subscription/resource-group uniqueness.

After a resource group has been provisioned once, bootstrap the matching GitHub environment from the Azure deployment outputs:

```bash
scripts/bootstrap-github-azure.sh \
  --environment development \
  --azure-environment dev \
  --resource-group rg-dev \
  --location westeurope
```

The bootstrap script creates or reuses an Entra app registration for GitHub Actions OIDC, adds a federated credential scoped to the selected GitHub environment, assigns Azure roles to the target resource group and registry, and writes the environment variables consumed by `.github/workflows/azure-api-deploy.yml`.

For infrastructure deployments from GitHub Actions, also set these GitHub environment secrets:

- `ADMIN_API_KEY`
- `BUILDER_CLIENT_API_KEY`
- `CONTROL_ENTRA_CLIENT_SECRET`

Set `CONTROL_ENTRA_CLIENT_ID` and `CONTROL_ENTRA_TENANT_ID` as environment variables.
When Cloud JWT admission is enabled, set both `CLOUD_ACCOUNT_ISSUER` and
`EXPECTED_CLOUD_ACCOUNT_ISSUER` to the exact approved Supabase Auth issuer for
that environment. Establish `EXPECTED_CLOUD_ACCOUNT_ISSUER` independently in
the protected GitHub environment before running bootstrap; bootstrap refuses a
mismatch and does not write the approval variable. The workflow also refuses an
issuer mismatch.

For app-only deployments, the workflow needs only the OIDC and Azure resource variables written by the bootstrap script.

## Secret Handling

The deployment helper uses secure Bicep parameters for the API key, builder key, and
Control Entra client secret. Do not commit real parameter files. For CI/CD, pass these
values from the target environment secret store; the helper writes mode-0600 temporary
parameter files and removes them on exit.

The admin API key is for machine-to-machine administration only. Browser users
sign in through the platform OIDC provider and receive a platform session cookie.
Admin UI/API access is granted by the `control_admin` role.

Catalog uses Microsoft Entra managed-identity authentication. A new environment still
requires one first-run contained-user grant for the generated API identity before the
Web App can start; subsequent deployments reuse that identity and grant.

## Operational Checks

After deployment:

```bash
curl https://<web-app-name>.azurewebsites.net/health
curl -I https://<web-app-name>.azurewebsites.net/admin
```

Then open `/admin`. Anonymous browser requests redirect to the platform OIDC
sign-in flow.

## Known Follow-Ups

- Add custom domain, managed certificate, and front-door/WAF integration when public production DNS is known.
- Add backup, restore, and retention policy decisions for Azure SQL before production data is material.
- Add private networking once the platform has stable network boundaries.
- Add Key Vault references and Entra-only SQL auth as a hardening slice.
- Add Key Vault references and private networking for the optional Keycloak
  PostgreSQL database before public SaaS launch.
