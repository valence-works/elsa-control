# Package catalog account extension points

## Implementable contributor interfaces

### `IWorkspaceOwnerProvisioner`

- **Layer:** Core — `ElsaControl.PackageCatalog.Core`
- **Kind:** action-named contributor
- **Signature:** `Task ProvisionAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken = default)`
- **Registration:** register each implementation as `IWorkspaceOwnerProvisioner` in dependency injection.
- **Aggregator:** `AccountWorkspaceService` invokes all registered provisioners sequentially after a personal or organization workspace owner is persisted, when a membership transitions to Owner, and whenever an existing owner resolves their account or workspace access. The read-only `FindAsync` lookup (behind `GET /api/me/workspace-memberships`) is the exception: it never invokes provisioners.
- **Contract:** implementations must be idempotent. Existing-owner reads intentionally reconcile provisioning so a retry repairs partial setup after a transient contributor failure.
- **Known implementations:** `WorkspacePermissionOwnerProvisioner` *(cross-domain — ElsaControl.Api)* provisions the contributed workspace permission defaults.

## Events

This domain does not publish owner-provisioning events. Provisioning is part of the synchronous ownership mutation so setup failures remain visible to the caller.
