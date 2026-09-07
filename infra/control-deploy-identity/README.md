# Control API deploy identity

`valence_control_contributor_mi-m5uymkuaf222o` is the user-assigned managed identity that GitHub
Actions logs in as for the `production` environment: its client id is the environment's
`AZURE_CLIENT_ID`, and its federated credential trusts
`repo:valence-works/elsa-control:environment:production`. Aspire originally provisioned it next to
the Aspire dashboard; the dashboard was retired (#302, #307), so this template is the source of
truth for the identity, the credential and its three deploy roles (#308).

**Never delete this identity.** Doing so breaks every build, promote and infra deployment and
requires re-bootstrapping the GitHub environment with a new client id.

## Roles (exact scopes)

| Role | Scope | Purpose |
| --- | --- | --- |
| Reader | `rg-valence-control-prod` | read the web app, registry and deployment state |
| Website Contributor | `api-m5uymkuaf222o` | image promotion, app settings, restart |
| AcrPush | `valencecontrolacrm5uymkuaf222o` | candidate image publication |

## Adopting the live resources

`main.parameters.production.json` carries the live identity name, its region, the exact federated
subject (GitHub's id-based form) and the existing role-assignment names, so a deployment against
`rg-valence-control-prod` is a no-op. Verify before deploying:

```sh
az deployment group what-if --subscription 8e23037a-420f-4ad0-9594-9d194de29e84 \
  --resource-group rg-valence-control-prod \
  --template-file infra/control-deploy-identity/main.bicep \
  --parameters @infra/control-deploy-identity/main.parameters.production.json
```

Every resource must report `NoChange`. New environments omit the role-assignment names and let
the template derive deterministic ones. `dev/regenerate-infra.sh` preserves this directory.
