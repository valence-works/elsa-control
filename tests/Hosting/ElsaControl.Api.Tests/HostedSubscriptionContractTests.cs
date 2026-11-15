using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ElsaControl.Api.OrganizationBilling;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Tests;

/// <summary>
/// Pins Hub-facing hosted-subscription JSON so an accidental contract change
/// fails CI. <c>elsa-hub</c> <c>cloud_test.ts</c> uses the same files.
/// </summary>
public sealed class HostedSubscriptionContractTests
{
    public const string RelativeDirectory = "contracts/hosted-subscription";
    public const string RegenerateEnvironmentVariable = "ELSA_CONTROL_REGENERATE_HOSTED_SUBSCRIPTION_CONTRACTS";

    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static TheoryData<string, HostedSubscriptionManagementResponse> HostedSubscriptionScenarios() => new()
    {
        { "no-billing-linkage.json", NeverSubscribed() },
        { "active.json", Active() },
        { "suspended-canceled.json", Suspended(OrganizationSubscriptionSuspensionReason.Canceled, OrganizationBillingRecoveryActions.Subscribe) },
        { "suspended-non-payment.json", Suspended(OrganizationSubscriptionSuspensionReason.NonPayment, OrganizationBillingRecoveryActions.UpdatePaymentMethod) },
        { "suspended-deletion-requested.json", Suspended(OrganizationSubscriptionSuspensionReason.DeletionRequested, OrganizationBillingRecoveryActions.None) },
        { "retained.json", Retained() }
    };

    public static TheoryData<string, OrganizationBillingStatusResponse> BillingStatusScenarios() => new()
    {
        { "billing-status-no-linkage.json", BillingStatusNeverSubscribed() },
        { "billing-status-active.json", BillingStatusActive() }
    };

    [Theory]
    [MemberData(nameof(HostedSubscriptionScenarios))]
    public void Hosted_subscription_response_matches_the_committed_golden_file(
        string fileName,
        HostedSubscriptionManagementResponse response)
    {
        AssertCommitted(fileName, response);
    }

    [Theory]
    [MemberData(nameof(BillingStatusScenarios))]
    public void Billing_status_response_matches_the_committed_golden_file(
        string fileName,
        OrganizationBillingStatusResponse response)
    {
        AssertCommitted(fileName, response);
    }

    [Fact]
    public void Golden_files_do_not_embed_prices_secrets_or_customer_references()
    {
        foreach (var file in Directory.GetFiles(CommittedDirectory(), "*.json"))
        {
            var json = File.ReadAllText(file);
            Assert.DoesNotContain("price_", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sk_test", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("whsec_", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cus_", json, StringComparison.Ordinal);
            Assert.DoesNotContain("sub_", json, StringComparison.Ordinal);
            Assert.DoesNotContain("providerCustomerReference", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static HostedSubscriptionManagementResponse NeverSubscribed() =>
        new(
            OrganizationId,
            null,
            false,
            false,
            true,
            HostedSubscriptionManagementActions.NoBillingLinkage,
            HostedBillingCopy.ForMissingLinkage(),
            null,
            null,
            OrganizationBillingRecoveryActions.None);

    private static HostedSubscriptionManagementResponse Active() =>
        Linked(
            nameof(OrganizationSubscriptionState.Active),
            HostedSubscriptionManagementActions.OpenPortal,
            portalAvailable: true,
            suspensionReason: null,
            recoveryAction: OrganizationBillingRecoveryActions.None);

    private static HostedSubscriptionManagementResponse Suspended(
        OrganizationSubscriptionSuspensionReason reason,
        string recoveryAction) =>
        Linked(
            nameof(OrganizationSubscriptionState.Suspended),
            HostedSubscriptionManagementActions.OpenPortal,
            portalAvailable: true,
            suspensionReason: reason.ToString(),
            recoveryAction: recoveryAction);

    private static HostedSubscriptionManagementResponse Retained() =>
        Linked(
            nameof(OrganizationSubscriptionState.Retained),
            HostedSubscriptionManagementActions.OpenPortal,
            portalAvailable: true,
            suspensionReason: null,
            recoveryAction: OrganizationBillingRecoveryActions.None);

    private static HostedSubscriptionManagementResponse Linked(
        string state,
        string managementAction,
        bool portalAvailable,
        string? suspensionReason,
        string recoveryAction) =>
        new(
            OrganizationId,
            state,
            true,
            portalAvailable,
            true,
            managementAction,
            HostedBillingCopy.ForLinkedCustomer(),
            BillingWebhookSources.Hub,
            suspensionReason,
            recoveryAction);

    private static OrganizationBillingStatusResponse BillingStatusNeverSubscribed() =>
        OrganizationBillingStatusResponse.From(
            OrganizationId,
            subscription: null,
            entitlement: null,
            activeWorkspaces: 1,
            activeDedicatedInstances: 0);

    private static OrganizationBillingStatusResponse BillingStatusActive()
    {
        var started = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(OrganizationId, BillingProviderNames.Stripe, started);
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Active, started);
        subscription.UpdatedAt = started;
        var entitlement = new OrganizationEntitlementSnapshot
        {
            OrganizationId = OrganizationId,
            ManagedHostingEnabled = true,
            DeploymentTargetsEnabled = true,
            MaxInstances = 1,
            MaxWorkspaces = 1,
            MaxSources = 1,
            SyncedAt = started,
            CreatedAt = started,
            UpdatedAt = started,
            SubscriptionState = OrganizationSubscriptionState.Active
        };
        return OrganizationBillingStatusResponse.From(
            OrganizationId,
            subscription,
            entitlement,
            activeWorkspaces: 1,
            activeDedicatedInstances: 1);
    }

    private static void AssertCommitted<T>(string fileName, T response)
    {
        var actual = CanonicalJson(response);
        var file = new FileInfo(Path.Combine(CommittedDirectory(), fileName));
        var regenerate = IsTruthy(Environment.GetEnvironmentVariable(RegenerateEnvironmentVariable));
        if (regenerate && (IsTruthy(Environment.GetEnvironmentVariable("CI")) || IsTruthy(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))))
        {
            Assert.Fail(
                "Hosted-subscription contract regeneration is disabled when CI or GITHUB_ACTIONS is set. " +
                $"Unset {RegenerateEnvironmentVariable}.");
        }

        if (regenerate)
        {
            Directory.CreateDirectory(file.DirectoryName!);
            File.WriteAllText(file.FullName, actual + Environment.NewLine);
        }

        Assert.True(file.Exists, $"Committed golden file is missing at {RelativeDirectory}/{fileName}.");
        Assert.Equal(CanonicalJson(File.ReadAllText(file.FullName)), actual);
    }

    private static string CanonicalJson<T>(T value) =>
        CanonicalJson(JsonSerializer.Serialize(value, JsonOptions));

    private static string CanonicalJson(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("JSON was empty.");
        return node.ToJsonString(JsonOptions);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private static string CommittedDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElsaControl.sln")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("Could not locate repository root.");
        return Path.Combine(directory.FullName, RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
