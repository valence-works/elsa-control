namespace ElsaControl.PackageCatalog.Core.Accounts;

/// <summary>
/// Normalized replay identity for an already-seen billing provider event.
/// Same-id deliveries are compared by these facts, not by a hash of the raw
/// webhook body.
/// </summary>
/// <remarks>
/// Compared fields, in order:
/// <list type="number">
/// <item><see cref="OrganizationId"/> — Control org metadata (<c>elsa_control_organization_id</c> or checkout <c>client_reference_id</c>).</item>
/// <item><see cref="EventType"/> — provider event type.</item>
/// <item><see cref="State"/> — mapped commercial lifecycle, when the event is known.</item>
/// <item><see cref="OccurredAt"/> — provider event created timestamp.</item>
/// <item><see cref="ProviderCustomerReference"/> — customer object id.</item>
/// <item><see cref="ProviderSubscriptionReference"/> — subscription object id.</item>
/// <item><see cref="ProviderObjectReference"/> — Stripe data object id (session, subscription, or customer).</item>
/// <item><see cref="PriceReference"/> — opaque price id, when present on the payload.</item>
/// <item><see cref="AmountMinorUnits"/> — commercial amount in minor units, when present on the payload.</item>
/// </list>
/// <see cref="BillingProviderEvent.EventHash"/> is a delivery fingerprint of the
/// raw request body and is not compared. Stripe redeliveries can change envelope
/// fields such as <c>pending_webhooks</c> without changing these facts.
/// Extra commercial facts (object id, price, amount) may be absent on inbox rows
/// written before those columns existed. A stored null means "not recorded" and
/// does not conflict with a later delivery that supplies the same core facts.
/// </remarks>
public readonly record struct BillingProviderEventReplayFacts(
    Guid OrganizationId,
    string EventType,
    OrganizationSubscriptionState? State,
    DateTimeOffset OccurredAt,
    string? ProviderCustomerReference,
    string? ProviderSubscriptionReference,
    string? ProviderObjectReference,
    string? PriceReference,
    long? AmountMinorUnits)
{
    public static BillingProviderEventReplayFacts From(BillingProviderEvent providerEvent) =>
        new(
            providerEvent.OrganizationId,
            providerEvent.EventType,
            providerEvent.State,
            providerEvent.OccurredAt.ToUniversalTime(),
            providerEvent.ProviderCustomerReference,
            providerEvent.ProviderSubscriptionReference,
            providerEvent.ProviderObjectReference,
            providerEvent.PriceReference,
            providerEvent.AmountMinorUnits);

    public static BillingProviderEventReplayFacts From(BillingProviderEventInboxEntry inbox) =>
        new(
            inbox.OrganizationId,
            inbox.EventType,
            inbox.State,
            inbox.OccurredAt.ToUniversalTime(),
            inbox.ProviderCustomerReference,
            inbox.ProviderSubscriptionReference,
            inbox.ProviderObjectReference,
            inbox.PriceReference,
            inbox.AmountMinorUnits);

    public bool ConflictsWith(BillingProviderEventReplayFacts incoming) =>
        OrganizationId != incoming.OrganizationId ||
        !string.Equals(EventType, incoming.EventType, StringComparison.Ordinal) ||
        State != incoming.State ||
        OccurredAt != incoming.OccurredAt ||
        !string.Equals(ProviderCustomerReference, incoming.ProviderCustomerReference, StringComparison.Ordinal) ||
        !string.Equals(ProviderSubscriptionReference, incoming.ProviderSubscriptionReference, StringComparison.Ordinal) ||
        RecordedExtraConflicts(ProviderObjectReference, incoming.ProviderObjectReference) ||
        RecordedExtraConflicts(PriceReference, incoming.PriceReference) ||
        AmountMinorUnits is not null && AmountMinorUnits != incoming.AmountMinorUnits;

    private static bool RecordedExtraConflicts(string? stored, string? incoming) =>
        stored is not null && !string.Equals(stored, incoming, StringComparison.Ordinal);
}
