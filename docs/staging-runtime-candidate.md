# Isolated staging runtime candidate

This runbook stages a signed Valence Runtime image for a managed-engine customer-flow
rehearsal before production image publication. It is limited to the persistent staging
Control deployment and the staging ACR. Production release admission remains pinned to
the production registry and the producer's `main` or version-tag workflow identity.

## Producer and admission boundary

1. Review the runtime-image PR and run its tests and vulnerability gates. Push the
   reviewed commit to the protected `candidate/staging` branch in
   `valence-works/elsa-production-image`. Its GitHub `staging` environment must permit
   only that branch, use a federated identity with `AcrPush` at the staging ACR scope,
   and set `STAGING_RUNTIME_REGISTRY` to that exact ACR hostname. The workflow fails
   closed if the selected registry is missing or equals production.
2. Record the successful workflow run and its `signed-release-manifest-*` artifact.
   Use the immutable manifest reference and digest from the publication envelope;
   never infer authority from a tag. Verify the signature and evidence against the
   exact producer workflow identity ending in `@refs/heads/candidate/staging`.
3. Before admitting the candidate, record the previous non-secret staging Control
   authority settings and image reference in a protected rollback record. Do not
   copy credentials into that record. Change only
   staging Control's `ReleaseCatalog__Verification__RegistryHost`,
   `ReleaseCatalog__Verification__Repository`,
   `ReleaseCatalog__Admission__ExpectedSignatureSubject`, the three
   `Deployment__AzureProvider__Runner__TargetScope__RegistrySubscriptionId`,
   `RegistryResourceGroupName`, and `RegistryName` values, plus the three
   `Deployment__AzureProvider__Runner__RegistryDeploymentMetadataRoleDefinitionId`,
   `RegistryDeploymentMetadataRoleAssignmentId`, and
   `RegistryRoleAdministrationAssignmentId` values to the isolated staging registry
   and exact candidate signer. Preserve the existing strict OIDC issuer
   and all other verifier checks. The registry scope must match the signed paid
   `runtime-combined` image repository exactly. Before restart, grant the verifier
   identity read access to that ACR, and configure the narrow provider's exact
   deployment-metadata and conditional role-administration assignments at the
   new staging registry scope. Never reuse assignment IDs from a different
   registry. If ACR blob reads redirect, add only the exact observed staging blob
   host to the verifier allowlist.
4. Deploy the matching Control candidate to the staging API/worker using the
   `candidate/staging` ref, `test` target, and immutable build-then-promote workflow.
   The workflow refuses this ref for production. Admit the signed manifest through
   the existing authorized catalog API; a PR check or source inspection alone does
   not establish admission.
5. Create or upgrade a staging managed engine through the customer path and verify
   the exact owner/admin Studio dashboard, Workflow Definitions/designer, Structured
   Logs, lower-role denial, and cross-account denial on desktop and 390×844. Record
   sanitized outcomes in the active Issue Bus issue before any production promotion.

## Rollback

If admission, provisioning, authorization, or the customer flow fails, stop new
staging Create/upgrade requests. Do not remove an engine or cancel billing merely to
roll back a release. First inspect the durable operations and provider resources
under the staging registry target. If no operation or engine was created against
that target, restore the captured staging Control image and app settings, then
verify API/worker health and the old catalog authority before allowing mutations.

If a candidate operation has started, retain the matching registry target and a
Control worker capable of observing it. An Azure operation may still be running:
use provider observation/recovery to establish its terminal state, not a blind
replay. Do not restore the old global registry target while a candidate engine or
unresolved operation remains; that would make its persisted scope fail closed for
recovery or Delete. Either keep the candidate target and recover the engine there,
or perform an authorized customer Delete under that same target and confirm
provider absence before restoring the prior settings. Keep the candidate ACR
artifacts for forensic comparison until resolution. No production registry,
identity, release alias, or customer environment is part of this rollback.
