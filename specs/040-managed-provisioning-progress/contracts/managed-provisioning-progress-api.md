# Contract: Managed Provisioning Progress API

## Capability

Control advertises the additive compatibility capability:

```text
hosted.instances.provisioning-progress.v1
```

An older client may ignore it. A client that requires provisioning progress fails closed when it is absent.

## Read Progress

```http
GET /api/workspaces/{workspaceId}/instances/{instanceId}/provisioning-progress
Authorization: Bearer <customer identity or configured Cloud BFF identity>
Cache-Control: private, no-store
```

The endpoint uses the established workspace authorization boundary and is explicitly available to the narrow Cloud BFF caller. It resolves lifecycle/provider correlation server-side.

### Success: 200

```json
{
  "state": "active",
  "provider": "azure",
  "currentStage": "runtime-deployment",
  "startedAt": "2026-09-21T10:00:00Z",
  "lastUpdatedAt": "2026-09-21T10:04:35Z",
  "completedAt": null,
  "diagnosticCode": null,
  "stages": [
    { "code": "request-accepted", "status": "completed", "startedAt": "2026-09-21T10:00:00Z", "completedAt": "2026-09-21T10:00:08Z" },
    { "code": "hosting-foundation", "status": "completed", "startedAt": "2026-09-21T10:00:08Z", "completedAt": "2026-09-21T10:04:32Z" },
    { "code": "configuration", "status": "completed", "startedAt": "2026-09-21T10:02:10Z", "completedAt": "2026-09-21T10:04:32Z" },
    { "code": "runtime-deployment", "status": "current", "startedAt": "2026-09-21T10:04:35Z", "completedAt": null },
    { "code": "health-verification", "status": "pending", "startedAt": null, "completedAt": null },
    { "code": "traffic-routing", "status": "pending", "startedAt": null, "completedAt": null },
    { "code": "ready", "status": "pending", "startedAt": null, "completedAt": null }
  ],
  "activity": [
    { "sequence": 1, "stage": "request-accepted", "status": "started", "messageCode": "request.accepted", "occurredAt": "2026-09-21T10:00:00Z" },
    { "sequence": 4, "stage": "runtime-deployment", "status": "started", "messageCode": "runtime.deploying", "occurredAt": "2026-09-21T10:04:35Z" }
  ]
}
```

### Stable values

Overall state: `queued`, `active`, `stale`, `ready`, `failed`, `unavailable`.

Stage code, in fixed order: `request-accepted`, `hosting-foundation`, `configuration`, `runtime-deployment`, `health-verification`, `traffic-routing`, `ready`.

Stage status: `pending`, `current`, `completed`, `blocked`, `unknown`.

Activity status: `started`, `completed`, `blocked`, `ready`.

Initial public diagnostic codes:

- `provisioning.requires-attention`
- `provisioning.failed`
- `provisioning.cancelled`
- `provisioning.history-unavailable`

Initial activity message codes:

- `request.accepted`
- `foundation.preparing`
- `foundation.ready`
- `configuration.registry-ready`
- `configuration.secrets-ready`
- `configuration.database-ready`
- `runtime.deploying`
- `runtime.deployed`
- `health.verifying`
- `health.verified`
- `traffic.routing`
- `traffic.routed`
- `engine.ready`
- `provisioning.requires-attention`
- `provisioning.failed`

Unknown internal phases or diagnostic codes are reduced to the applicable known public state; they are not echoed.

## Error semantics

- `401`: unauthenticated caller.
- `403` or the repository's established concealed-resource response: caller lacks workspace access.
- `404`: no visible managed instance in the scoped workspace.
- `409`: lifecycle/provider topology changed during a consistency-sensitive read; client may retry.
- `5xx`: progress is temporarily unavailable. The engine-list result remains authoritative.

Problem responses use the existing safe Control problem contract and contain no provider payload.

## Explicit exclusions

The response never contains lifecycle or provider operation IDs; Azure resource, resource-group, deployment, subscription, or tenant IDs; target keys, endpoints, images, fingerprints, or resource references; worker IDs, leases, heartbeat values, hashes, recovery evidence, or secret references; raw provider messages, diagnostics, exceptions, or stack traces; numeric percentage; or predicted completion time.
