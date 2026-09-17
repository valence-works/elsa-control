# Elsa Cloud BFF authentication contract

The hosted Elsa Cloud frontend may use a server-side Lovable/Cloud BFF to call
Elsa Control. The Entra bridge performs the
[OAuth 2.0 authorization-code flow with PKCE](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow)
against the same configured identity provider as Control, requests the dedicated
`ElsaCloud.Dashboard` delegated scope, and keeps the resulting Control access
token on the server. Browser code never receives or stores that access token;
the BFF forwards it to Control as `Authorization: Bearer ...`.

For Hosted accounts, the BFF can instead forward the already authenticated Elsa
Cloud user's Supabase access JWT. Control independently verifies its issuer,
audience, signature against the issuer's OIDC/JWKS discovery, and lifetime. It
maps the immutable issuer and user ID to the Control account and restricts this
bearer to the same Cloud endpoint allowlist. Google, Microsoft, and email Cloud
accounts therefore share one Hosted sign-in. No Entra delegation is needed to
create a Hosted workspace.

Configure this only for the Elsa Cloud project's OIDC issuer, whose signing
keys must be asymmetric and published as JWKS:

```text
Authentication__CloudAccount__Enabled=true
Authentication__CloudAccount__Issuer=https://<project-ref>.supabase.co/auth/v1
Authentication__CloudAccount__Audience=authenticated
```

Deploy Control with this validation enabled before changing the Cloud BFF to
forward Supabase user tokens. Keep the existing Entra scheme for operator login
and Azure-specific customer integrations. Neither a browser-supplied user ID
nor a privileged service key substitutes for the validated user JWT.
For the current Hosted site, set the optional `cloudaccountissuer_value` input of
`infra/api/api-website.module.bicep` to
`https://jhrcnclyydzngnyvhdht.supabase.co/auth/v1`. Verify its JWKS contains
asymmetric public keys before enabling it. After Control has been deployed,
publish the Cloud BFF and site together, then exercise Google, email confirmation,
and Microsoft sign-in through checkout on the deployed origins. Check that each
account sees only its own workspace, and that an admin route rejects a Cloud JWT.

The current production API is released through `.github/workflows/azure-api-deploy.yml`.
Set its production environment variable `CLOUD_ACCOUNT_ISSUER` to that exact
issuer before dispatching a deployment from `main`. The workflow verifies the
project issuer and sets the three `Authentication__CloudAccount__*` app settings
in every mutating deploy mode. If the variable is empty, it disables the Cloud
scheme; the Bicep module parameter alone does not configure the current
production deploy path.

## Required configuration and claims

Enable the bridge only when the BFF is deployed and its identity-provider
client has been configured:

```text
Authentication__CloudBff__Enabled=true
Authentication__CloudBff__ClientId=<registered BFF client id>
Authentication__CloudBff__Scope=ElsaCloud.Dashboard
```

For Microsoft Entra, request the fully qualified API scope
`api://<Control application client id>/ElsaCloud.Dashboard`; the validated
access token's space-delimited `scp` claim contains `ElsaCloud.Dashboard`.
Microsoft documents `scp` for delegated permissions and `azp`/`appid` for the
calling application in its
[claims-validation guidance](https://learn.microsoft.com/en-us/entra/identity-platform/claims-validation).

`Scope` defaults to `ElsaCloud.Dashboard`. Control still performs its normal
JWT issuer, audience, signature, and lifetime checks. A BFF token must then
carry the exact configured client ID in `azp` or `appid` and the exact scope as
one space-delimited token in one `scp` claim. Duplicate or conflicting client
claims, duplicate `scp` claims, and malformed values fail closed. A token with
the configured client but without the dedicated scope, or with the dedicated
scope from another client, is rejected.

## BFF allowlist

The Cloud BFF reads the static capability envelope at
[`GET /api/cloud/compatibility`](cloud-compatibility-contract.md). Its versioning,
capability mappings, and rollout rules are documented separately there.

Validated Entra BFF and Cloud account tokens are accepted only on these customer endpoints:

```text
GET  /api/cloud/compatibility
POST /api/cloud/bootstrap
GET  /api/me/workspaces
GET  /api/me/organizations
GET  /api/workspaces/{workspaceId}/instances
GET  /api/workspaces/{workspaceId}/instances/onboarding-options
GET  /api/workspaces/{workspaceId}/instances/{instanceId}/delete-operations/{operationId}
POST /api/workspaces/{workspaceId}/instances
PATCH /api/workspaces/{workspaceId}/instances/{instanceId}
POST /api/workspaces/{workspaceId}/instances/{instanceId}/delete-confirmations
POST /api/workspaces/{workspaceId}/instances/{instanceId}/delete
POST /api/organizations/{organizationId}/billing/prepare-hosted-trial
POST /api/managed-elsa/handoff/issue
GET  /api/workspaces/{workspaceId}/external-engine-connections
POST /api/workspaces/{workspaceId}/external-engine-connections
GET  /api/workspaces/{workspaceId}/external-engine-connections/{connectionId}
GET  /api/workspaces/{workspaceId}/external-engine-connections/{connectionId}/pairing
POST /api/workspaces/{workspaceId}/external-engine-connections/{connectionId}/disconnect
POST /api/workspaces/{workspaceId}/external-engine-connections/{connectionId}/repair
POST /api/workspaces/{workspaceId}/external-engine-connections/{connectionId}/studio-destination/confirm
```

Every other customer endpoint and every `/api/admin/...` endpoint rejects these
Cloud tokens. Admin routes continue to require the existing admin API key or
Control administrator authorization. The allowlist does not change normal
customer cookies or non-BFF Control bearer tokens. No CORS policy is added for
the BFF; deployment networking and browser-facing origin policy remain
separate concerns.

## Curl contract check

Use a short-lived token held by the BFF for a sanitized contract check (never
paste a real token into source control, logs, tickets, or browser storage):

```bash
CONTROL_BASE_URL='https://control.example.test'
BFF_ACCESS_TOKEN='<server-held-access-token>'

curl --fail-with-body --request GET \
  --url "$CONTROL_BASE_URL/api/me/workspaces" \
  --header "Authorization: Bearer $BFF_ACCESS_TOKEN"

# Dry-run: inspect the command without making a network request.
printf '%s\n' "curl GET $CONTROL_BASE_URL/api/me/workspaces with a server-held bearer token"
```

The same token must receive `403 Forbidden` on an unlisted customer route or
an admin route. A missing, wrong, or malformed client/scope pair also receives
`403 Forbidden`; failed JWT cryptographic validation remains the normal `401`
authentication failure.

## Security notes

- The Entra bridge must use authorization-code + PKCE and keep its delegated
  refresh/access tokens in a server-side protected store. The Hosted bridge
  forwards only a verified Cloud user session JWT to Control.
- The BFF OAuth client credential, when the provider requires one, is not a
  Control Admin API key and must be limited to the delegated dashboard scope.
- Use HTTPS for the BFF and Control in deployed environments; do not log
  authorization headers or token contents.
- Register only the exact redirect URIs and delegated scope required by the BFF.
- Treat the configured client ID and scope as an admission boundary, not as a
  shared secret. Rotate provider credentials and revoke the BFF client through
  the identity provider when needed.
- Workspace membership, permission checks, commercial entitlement gates, and
  managed-instance lifecycle validation still run on every allowlisted route.
