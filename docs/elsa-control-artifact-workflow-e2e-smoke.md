# Elsa Control Artifact Workflow E2E Smoke

Status: planned smoke path

Last updated: 2026-06-01

Tracking issue: [#43](https://github.com/valence-works/elsa-control/issues/43)

## Purpose

This smoke path validates that the completed artifact-driven workflow slices operate as one product path:

```text
Elsa Studio Submit to Elsa Control
  -> Elsa Control artifact registry
  -> artifact-backed desired state and promotion
  -> deployment run and runtime command
  -> Elsa Workflows runtime applier
  -> command completion and rollback history
```

The smoke should stay thin. It exists to prove the seams between packages, APIs, and repositories; focused behavior remains covered by the feature specs and unit/integration tests.

## Preconditions

- `elsa-control/main` includes specs `024` through `030`.
- `elsa-core/main` includes the `Elsa.Studio.PlatformIntegration` module and neutral workflow action zones.
- A Elsa Control workspace exists with permission to register artifacts, manage desired state, preview promotion, deploy, and roll back.
- A Elsa Control environment exists with at least one registered runtime target advertising `workflow-definition.apply`.
- A runtime-integrated Elsa Workflows app can poll Elsa Control runtime commands and apply `elsa.workflow-definition` artifacts.
- The Studio integration and runtime applier are configured with Elsa Control endpoint, workspace, authentication, and approved payload transport settings.

## Smoke Sequence

1. Author or open a workflow definition in Elsa Studio.
2. Choose **Submit to Elsa Control** from the workflow editor toolbar.
3. Verify the Elsa Control artifact registry contains an `elsa.workflow-definition` artifact with:
   - safe display metadata,
   - Studio producer metadata,
   - content digest,
   - payload reference,
   - required capability hints,
   - no raw workflow definition content in catalog tables.
4. Submit the same workflow snapshot again and verify the result is idempotent rather than a duplicate artifact error.
5. Create or update a desired-state revision that references the submitted artifact.
6. Preview promotion into a target environment and verify the preview shows artifact identity, digest, safe metadata, environment configuration, tier policy, and runtime compatibility.
7. Confirm promotion and queue deployment.
8. Verify Elsa Control creates a deployment run and runtime command that references the artifact by safe identity/digest and does not embed raw workflow content.
9. Run the runtime applier sync loop.
10. Verify the runtime applier:
    - polls and claims the command,
    - fetches the artifact envelope and payload,
    - verifies digest and schema compatibility,
    - applies the workflow definition through the runtime store boundary,
    - records the apply journal entry,
    - reports progress and completion to Elsa Control.
11. Verify Elsa Control deployment history shows command progress, completion, runtime reference, observed digest, and safe diagnostics.
12. Trigger the same command delivery path again or replay the applied artifact and verify the idempotency guard prevents duplicate local apply.
13. Deploy a second artifact-backed revision.
14. Roll back to the previous successful revision and verify the rollback command references the known-good artifact.

## Failure Probes

Run these only after the happy path is stable:

- Submit with invalid Elsa Control credentials and verify Studio reports a safe unauthorized result.
- Submit unsafe display metadata and verify Elsa Control rejects or sanitizes it without storing unsafe metadata.
- Queue deployment to a runtime missing `workflow-definition.apply` and verify validation fails before command apply.
- Corrupt the payload digest and verify the runtime applier rejects the command with safe diagnostics.
- Let a claimed command lease expire and verify Elsa Control marks recovery state without issuing duplicate apply automatically.
- Enable advisory webhook dispatch, deliver duplicate command-available notifications, and verify the runtime still performs poll/claim before apply.

## Verification Artifacts

Record each smoke run with:

- Elsa Control commit SHA.
- Studio commit SHA.
- Runtime applier package version or commit SHA.
- Workspace ID or test fixture name.
- Artifact record ID and digest.
- Deployment run ID.
- Runtime command ID.
- Final run status.
- Any follow-up issue links.

## Related Packaging Artifacts

- Package naming and host configuration: [Elsa Control Integration Packaging And Host Configuration](elsa-control-integration-packaging.md).
- Host registration samples for Studio and runtime applications: [samples](../samples/README.md).
- Runtime credential bootstrap, rotation, and deployment-specific network trust guidance: [Runtime Transport Trust Policy](runtime-transport-trust-policy.md).
