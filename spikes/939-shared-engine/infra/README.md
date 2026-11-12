# Staging SQL for #939

Operator-applied only. Staging resource group `rg-valence-control-staging`. At most three Standard S0 databases on one logical server. Tags: `purpose=shared-engine-spike`, `issue=939`. Tear down within 7 days of the spike verdict unless a later leaf takes the resources over.

This environment could not authenticate to Azure (no managed identity / IMDS). Do not apply from an unauthenticated session.

```bash
az deployment group create \
  --resource-group rg-valence-control-staging \
  --template-file spikes/939-shared-engine/infra/main.bicep \
  --parameters administratorLogin=<staging-only-login>
```
