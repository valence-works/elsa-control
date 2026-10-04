// Copied from valence-works/elsa-cloud supabase/functions/control-bff/handler.ts
// Pinned source SHA: 30ffdc1f (elsa-cloud#146). Re-check against that commit
// before loosening this fixture. The staging fixture Python mirror and the
// live K probe bodies must stay valid against this schema.
import { z } from "zod";

const catalog = z.string().min(1).max(120);

const ManagedElsaIntentSchema = z.object({
  release: z.object({
    distributionId: catalog,
    releaseLine: catalog,
    requestedVersion: catalog,
    channel: catalog,
    patchUpdates: z.literal("automatic-within-minor"),
    minorUpdates: z.literal("explicit-approval"),
    majorMigrations: z.literal("explicit-migration"),
  }),
  application: z.object({
    topologyId: catalog,
    featurePresetId: z.null(),
    featureOverrides: z.record(z.never()),
    packagePolicy: z.null(),
    configurationShapeRevisionId: z.null(),
  }),
  placement: z.object({
    targetMode: catalog,
    regionCode: catalog,
    isolationProfile: catalog,
    capacityProfile: catalog,
    networkOutcome: catalog,
    domainOutcome: catalog,
  }),
  desiredLifecycle: z.literal("Running"),
});

export const BodySchema = z.discriminatedUnion("action", [
  z.object({ action: z.literal("compatibility") }),
  z.object({ action: z.literal("bootstrap") }),
  z.object({ action: z.literal("listOrganizations") }),
  z.object({
    action: z.literal("updateInstance"),
    organizationId: z.string().uuid(),
    workspaceId: z.string().uuid(),
    instanceId: z.string().uuid(),
    version: z.number().int().positive(),
    intent: ManagedElsaIntentSchema,
    idempotencyKey: z.string().min(8).max(200),
  }),
  z.object({
    action: z.literal("createInstanceDeleteConfirmation"),
    organizationId: z.string().uuid(),
    workspaceId: z.string().uuid(),
    instanceId: z.string().uuid(),
  }),
]);

export const PINNED_SOURCE_SHA = "30ffdc1f";

// Same bytes as scripts/staging-compat-fixture.sh k_probe_payload
// updateInstance --argjson intent.
export const UPDATE_INSTANCE_INTENT_FIXTURE_JSON =
  '{"release":{"distributionId":"valence-runtime","releaseLine":"3.8","requestedVersion":"3.8.0","channel":"stable","patchUpdates":"automatic-within-minor","minorUpdates":"explicit-approval","majorMigrations":"explicit-migration"},"application":{"topologyId":"combined","featurePresetId":null,"featureOverrides":{},"packagePolicy":null,"configurationShapeRevisionId":null},"placement":{"targetMode":"managed","regionCode":"westeurope","isolationProfile":"dedicated","capacityProfile":"standard-small","networkOutcome":"public","domainOutcome":"managed"},"desiredLifecycle":"Running"}';
