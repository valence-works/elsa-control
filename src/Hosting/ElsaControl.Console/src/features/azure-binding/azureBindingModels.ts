/**
 * Organization-level Azure Lighthouse binding contracts.
 *
 * The API for this slice is intentionally kept separate from deployment and
 * billing contracts. The bind is an operate-authority prerequisite; it does
 * not mint an entitlement or select a customer-subscription deployment target.
 */
export type AzureSubscriptionBindState =
  | "PendingConsent"
  | "Verifying"
  | "Active"
  | "Degraded"
  | "Unbound";

export type AzureSubscriptionBind = {
  id: string;
  organizationId: string;
  customerTenantId: string;
  subscriptionId: string;
  managingTenantId: string | null;
  managingPrincipalIds: string[];
  registrationDefinitionId: string | null;
  registrationDefinitionFingerprint: string | null;
  state: AzureSubscriptionBindState;
  verifiedAt: string | null;
  lastPreflightCode: string | null;
  createdByAccountId: string | null;
  unbindReason: string | null;
};

export type AzureLighthouseOffer = {
  version: string;
  artifactUrl: string;
  artifactLabel?: string | null;
};

export type AzureBindingReadiness = {
  entitlement?: string | null;
  targeting?: string | null;
};

export type AzureSubscriptionBindingView = {
  bind: AzureSubscriptionBind | null;
  offer: AzureLighthouseOffer;
  readiness?: AzureBindingReadiness | null;
};

export type CreateAzureSubscriptionBindRequest = {
  subscriptionId: string;
  customerTenantId: string;
  consentConfirmed: true;
};
