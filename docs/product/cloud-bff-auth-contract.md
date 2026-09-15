# Elsa Cloud BFF authentication contract

The hosted Elsa Cloud frontend may use a server-side Lovable/Cloud BFF to call
Elsa Control. The BFF performs the
[OAuth 2.0 authorization-code flow with PKCE](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow)
against the same configured identity provider as Control, requests the dedicated
`ElsaCloud.Dashboard` delegated scope, and keeps the resulting Control access
token on the server. Browser code never receives or stores that access token;
the BFF forwards it to Control as `Authorization: Bearer ...`.

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

Validated BFF tokens are accepted only on these customer endpoints:

```text
POST /api/cloud/bootstrap
GET  /api/me/workspaces
GET  /api/me/organizations
GET  /api/workspaces/{workspaceId}/instances
GET  /api/workspaces/{workspaceId}/instances/onboarding-options
POST /api/workspaces/{workspaceId}/instances
POST /api/managed-elsa/handoff/issue
```

Every other customer endpoint and every `/api/admin/...` endpoint rejects a
BFF token. Admin routes continue to require the existing admin API key or
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

- The BFF must use authorization-code + PKCE, keep refresh/access tokens in a
  server-side protected store, and avoid forwarding tokens to the browser.
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
