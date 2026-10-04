# Managed lifecycle telemetry sink

This separate resource-group template creates a workspace-based Application
Insights sink for Control's existing managed lifecycle signals. It does not deploy,
restart, convert or configure the API, change its identities, expose the Aspire
dashboard, or enable lifecycle workers. It belongs in the reviewed **Control**
subscription, not in the customer workload subscription or Pay-As-You-Go.

## Security and cost boundary

- The template resolves an existing Control API user-assigned identity and grants
  only Monitoring Metrics Publisher at the exact Application Insights resource.
  Do not use the customer workload provisioner identity.
- Local authentication is disabled on Application Insights and Log Analytics.
  The exporter must use the matching managed identity, with no developer-credential
  or instrumentation-key authentication fallback. Azure calls the role “Metrics
  Publisher”, but it authorizes telemetry publication for all signal types.
- The Azure service ingestion/query endpoints use public TLS endpoints protected
  by Entra authentication and RBAC. This is **not** a private-link/AMPLS deployment.
  The Aspire dashboard web app is no longer provisioned in production (#302); the
  operator observation surface is this workspace and Application Insights behind
  Entra RBAC, so there is no public dashboard route to defend or prove.
- The workspace uses consumption pricing without reserved capacity, 30-day
  retention, and a default 1 GB/day ingestion safety brake. The quota is not a
  guaranteed spend cap and reaching it makes an observation window incomplete;
  missing samples must never count as healthy. Keep normal collection limited to
  managed lifecycle signals, not general request/dependency/log auto-capture.
- Outputs contain resource and identity references only. Retrieve the connection
  metadata privately from the exact deployed component during operator setup; do
  not paste it into issue comments, CI logs, or acceptance evidence.

Microsoft documents [Entra-authenticated ingestion and the required scoped role](https://learn.microsoft.com/en-us/azure/azure-monitor/app/azure-ad-authentication).

## Validation and rollout

1. Run `python3 scripts/tests/test_managed_telemetry_infrastructure.py`. It compiles
   the template and inspects the actual generated resource/role boundary, including
   the RecoveryRequired scheduled query rule and email action group.
2. Resolve and verify the intended Control subscription, resource group, existing
   API identity, supported sink region, and resource names. The separately
   guarded `telemetry` deploy mode of `azure-api-deploy.yml` runs
   `scripts/deploy-managed-telemetry.sh` at resource-group scope. The default
   `telemetry_action` is `what-if` and never creates. `create` is a separate
   dispatch and requires `confirm_environment` equal to the target environment
   name. It does not run subscription-scoped `infra/main.bicep`. Infra mode
   does not run this preview or preflight. Full infra remains #705. The script
   passes `environment=staging` plus `STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT`
   on `test`, and `environment=production` plus
   `PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT` on `production`. An unset
   recipient fails the deploy. Staging also requires
   `PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT` to be visible and different
   so the mailbox cannot silently reuse production. A read-only preflight
   reports present, missing, component-absent, or error and never blocks the
   what-if. It matches the exact API-identity principal, Monitoring Metrics
   Publisher role id, and Insights component scope; an RG-level grant does
   not satisfy it. An `az` error is reported as an error, never as a missing
   assignment. When the component is absent, `create` deploys the workspace,
   component, action group and alert without the role assignment, then prints
   the exact grant. Ingestion stays off until a later preflight reports
   present. The deploy identity never writes Authorization.
   A telemetry what-if, create, or preflight failure never rolls back the
   API in production, test, or development.
   Do not rely on the CLI's default subscription. Review every proposed change
   before deployment. A local `az deployment group what-if` must pass the same
   `environment` and `recoveryRequiredAlertEmail` parameters.
3. Deploy only this reviewed template in Incremental mode. Verify the exact identity
   role, local-auth disablement, workspace linkage, quota/retention settings, the
   environment-scoped `qr-recovery-required-entered-{environment}` rule, and the
   `ag-recovery-required-{environment}` action group bound to the pipeline-supplied
   mailbox. The rule queries the Log Analytics `AppDependencies` table for
   `Name == 'managed_lifecycle.recovery_required.entered'`, filters
   `Properties.environment`, looks back 1 hour every 5 minutes, splits by
   identity, and is stateless (`autoMitigate: false`). One email per
   RecoveryRequired entry. A resend of the same entry more than an hour
   after its first ingestion can email again. The template has no default
   email and no paging receivers.
4. Enable the reviewed source exporter only through the existing immutable API
   image promotion and migration-compatibility gates. Per environment, set
   `ManagedLifecycleTelemetry:AzureMonitor:Enabled=true` (the deploy workflow
   applies `MANAGED_LIFECYCLE_AZURE_MONITOR_ENABLED` when that variable is set),
   the reviewed connection string and API managed-identity client id, and
   `APPLICATIONINSIGHTS_STATSBEAT_DISABLED=true`. The workflow also sets
   `ManagedLifecycleTelemetry:AzureMonitor:Environment` to `staging` or
   `production`. Delivery is at most once (exporter `MaxRetries = 0`, no
   offline storage). The live API's classic Docker mode must not be converted
   to site containers to enable observability.
5. Prove positive managed-identity ingestion and negative unauthorized ingestion,
   then positive authorized-operator access to the workspace/Application Insights
   queries and negative access for an account outside the operator role. Verify
   actual received signal names/labels and authorized trace correlation, not merely
   exporter setup.
6. Run the separate bounded sampler against fresh managed runtime/provider
   observations for at least five minutes. Retained database health is not a fresh
   probe. Retain safe UTC window timestamps and healthy/total/unknown counts; no raw
   credentials, endpoints, customer identifiers or provider responses in evidence.

These live gates remain open under [#266](https://github.com/valence-works/elsa-control/issues/266).
Compilation and local transport tests alone do not satisfy them.
