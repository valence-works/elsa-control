# Keycloak SaaS Deployment

> **Legacy.** This hand-written Bicep stack (including its self-hosted Keycloak) is no longer
> the deployment path. Current deployments apply the Aspire-generated Bicep through
> `scripts/deploy-azure-elsa-control.sh`, use Microsoft Entra ID for sign-in, and use managed
> identity for Azure SQL. These files are kept under `infra-legacy/` for reference.

## Decision

Run Keycloak as a separate production identity service and keep Elsa Control as
an OIDC relying party. Elsa owns accounts, workspaces, roles, entitlements, and
tenant authorization. Keycloak owns users, passwords, MFA, federation, sessions,
and OIDC tokens.

```text
Browser -> Elsa API /api/auth/login -> Keycloak
Keycloak -> Elsa API /api/auth/callback -> platform session cookie
Console -> Elsa APIs -> platform session + role/workspace checks
```

Do not create one Keycloak realm per Elsa workspace. Use one production realm for
the SaaS identity boundary and keep `Workspace` as the SaaS tenant boundary.

## Azure Production Shape

The Bicep deployment can optionally provision:

- Elsa Control API Web App and Azure SQL catalog database.
- Keycloak Web App running the official Keycloak container.
- Azure Database for PostgreSQL Flexible Server for Keycloak.
- API app settings that point Elsa at the Keycloak realm.

Enable it with:

```bash
az deployment group create \
  --resource-group rg-elsa-control-prod \
  --template-file infra-legacy/main.bicep \
  --parameters @infra-legacy/parameters/prod.example.json \
  --parameters \
      adminApiKey='<strong-admin-key>' \
      sqlAdministratorPassword='<strong-sql-password>' \
      keycloakAdminPassword='<strong-keycloak-admin-password>' \
      keycloakPostgresAdministratorPassword='<strong-keycloak-db-password>' \
      keycloakClientSecret='<strong-oidc-client-secret>'
```

The deployment outputs include:

- `controlApiUrl`
- `keycloakUrl`
- `keycloakAuthority`
- `keycloakRealm`
- `keycloakClientId`

The first deployment creates the Keycloak service and database. Keycloak creates
its bootstrap admin only when the database is empty.

The current `scripts/deploy-azure-elsa-control.sh` deploys the Control API only
and does not provision Keycloak. The retired App Service Keycloak deployment is
kept under `infra-legacy` for historical reference. Do not use that development
shape for a current environment; configure the supported identity provider
through the current Control deployment contract instead.

## Keycloak Realm Setup

After infrastructure is deployed, bootstrap the realm and confidential client:

```bash
KEYCLOAK_URL='https://<keycloak-app>.azurewebsites.net' \
ELSA_CONTROL_API_URL='https://<elsa-control-api-app>.azurewebsites.net' \
KEYCLOAK_ADMIN_USERNAME='keycloak-admin' \
KEYCLOAK_ADMIN_PASSWORD='<strong-keycloak-admin-password>' \
KEYCLOAK_CLIENT_SECRET='<strong-oidc-client-secret>' \
scripts/bootstrap-keycloak-realm.sh
```

The bootstrap creates a `control_admin` realm role and configures the OIDC
client to emit realm roles as the `role` claim. For a disposable dev
environment, add `CREATE_DEV_USER=true` to create a test user using
`DEV_USERNAME` and `DEV_PASSWORD`; the dev user is assigned `control_admin`.

You can also sign into the Keycloak admin console:

```text
https://<keycloak-app>.azurewebsites.net
```

Use the bootstrap admin credentials supplied to the Bicep deployment. Then
complete any operational realm configuration that should not be hard-coded:

1. Configure SMTP for the realm.
2. Enable email verification and password reset policies.
3. Enable brute-force protection.
4. Configure MFA or WebAuthn policy for production users.
5. Create named admin users and remove or rotate the bootstrap admin.

Client settings:

```text
Client ID: elsa-control-console
Client authentication: On
Standard flow: On
Direct access grants: Off
PKCE method: S256
```

URLs:

```text
Valid redirect URIs:
  https://<elsa-control-api-app>.azurewebsites.net/api/auth/callback

Valid post logout redirect URIs:
  https://<elsa-control-api-app>.azurewebsites.net/admin/*

Web origins:
  https://<elsa-control-api-app>.azurewebsites.net

Role claim:

```text
Realm role: control_admin
Token claim: role
Mapper: user realm role mapper
```

The client secret must match the `keycloakClientSecret` Bicep parameter, which is
written to the API as:

```text
Authentication__ControlIdentity__ClientSecret
```

## Elsa API Settings

When `deployKeycloak=true`, Bicep writes these API settings:

```text
Authentication__ControlIdentity__Provider=Keycloak
Authentication__ControlIdentity__Authority=https://<keycloak-app>.azurewebsites.net/realms/elsa-control
Authentication__ControlIdentity__Issuer=https://<keycloak-app>.azurewebsites.net/realms/elsa-control
Authentication__ControlIdentity__Audience=elsa-control-console
Authentication__ControlIdentity__ClientId=elsa-control-console
Authentication__ControlIdentity__ClientSecret=<secret>
Authentication__ControlIdentity__RequireHttpsMetadata=true
Authentication__WorkspaceTrustedHeaders__Enabled=false
```

For custom domains, update both sides together:

- Keycloak `KC_HOSTNAME`.
- Elsa `Authentication__ControlIdentity__Authority`.
- Elsa `Authentication__ControlIdentity__Issuer`.
- Keycloak client redirect URIs and web origins.

## Smoke Test

After realm/client setup:

```bash
curl https://<elsa-control-api-app>.azurewebsites.net/health
curl https://<elsa-control-api-app>.azurewebsites.net/api/auth/session
```

Open:

```text
https://<elsa-control-api-app>.azurewebsites.net/admin/runtime-builder
```

Expected flow:

1. Anonymous browser request redirects to Keycloak.
2. Sign in with a realm user.
3. Browser returns to `/admin/runtime-builder`.
4. `GET /api/me/workspaces` succeeds and provisions/loads the user's personal workspace.
5. Admin API calls require the signed-in user to have `control_admin`.

## Production Hardening Checklist

- Replace default Azure App Service hostnames with custom domains before public launch.
- Enable HTTPS-only and HSTS at the edge.
- Store all secrets in environment secrets or Key Vault references.
- Back up the Keycloak PostgreSQL database and test restore.
- Configure SMTP and test password reset before inviting users.
- Disable or rotate bootstrap admin credentials after creating named admin users.
- Enable Keycloak brute-force protection.
- Configure MFA or WebAuthn for admins.
- Restrict Keycloak admin console access at the network or identity layer when possible.
- Monitor Keycloak health, login failures, and PostgreSQL storage/CPU.
- Export realm configuration after material changes and store it as an operational artifact without secrets.
- Keep `Authentication__WorkspaceTrustedHeaders__Enabled=false` in production.

## Known Follow-Ups

- Move Keycloak and Elsa secrets to Key Vault references in Bicep.
- Add custom domain and managed certificate resources.
- Add private networking between App Service and PostgreSQL.
- Move realm/client bootstrap to a one-shot deployment job once the desired
  production realm contract stabilizes.
