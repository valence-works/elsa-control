using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Azure;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Observes Azure Lighthouse from the managing tenant. Lighthouse delegation is represented by
/// Microsoft.ManagedServices registration resources in the customer subscription; an IAM query in
/// the managing tenant is intentionally not used because it does not expose delegated grants.
/// </summary>
public sealed class AzureLighthouseAuthorityObserver : IAzureLighthouseAuthorityObserver
{
    private readonly AzureProviderRunnerOptions _options;
    private readonly IAzureCommandProcess _process;

    public AzureLighthouseAuthorityObserver(AzureProviderRunnerOptions options)
        : this(options, new AzureCommandProcess(options.CommandTimeout, options.MaximumOutputCharacters))
    {
    }

    internal AzureLighthouseAuthorityObserver(AzureProviderRunnerOptions options, IAzureCommandProcess process)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        if (string.IsNullOrWhiteSpace(_options.AzureCliPath) || string.IsNullOrWhiteSpace(_options.AzureCliClientId))
            throw new ArgumentException("Azure CLI identity settings are required for Lighthouse observation.", nameof(options));
    }

    public async Task<AzureLighthouseAuthorityObservationResult> ObserveAsync(
        AzureLighthouseAuthorityObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeGuid(request.CustomerTenantId, out var customerTenantId) || !TryNormalizeGuid(request.SubscriptionId, out var subscriptionId) ||
            !TryNormalizeGuid(request.ManagingTenantId, out var managingTenantId) || !TryNormalizeGuid(request.ManagingPrincipalObjectId, out var managingPrincipalObjectId) ||
            !TryNormalizeGuid(request.ManagingPrincipalClientId, out var managingPrincipalClientId) ||
            !IsRegistrationDefinitionPath(request.RegistrationDefinitionId))
            return Failed("azure.lighthouse.observation-invalid", "The Lighthouse observation request is invalid.");
        if (!TryNormalizeGuid(_options.AzureCliClientId, out var configuredClientId) ||
            !TryNormalizeGuid(_options.SqlBootstrapObjectId, out var configuredObjectId) ||
            !TryNormalizeGuid(_options.LighthouseManagingTenantId, out var configuredManagingTenantId))
            return Failed("azure.lighthouse.authority-unconfigured", "The configured Azure identity and tenant IDs are invalid.");
        if (!string.Equals(managingPrincipalClientId, configuredClientId, StringComparison.Ordinal))
            return Failed("azure.lighthouse.principal-mismatch", "The requested managing principal is not the configured Azure identity.");
        if (!string.Equals(managingPrincipalObjectId, configuredObjectId, StringComparison.Ordinal))
            return Failed("azure.lighthouse.principal-mismatch", "The requested managing principal is not the configured provider authority.");
        if (!string.Equals(managingTenantId, configuredManagingTenantId, StringComparison.Ordinal))
            return Failed("azure.lighthouse.tenant-mismatch", "The requested managing tenant is not the configured provider tenant.");
        if (!string.Equals(
                request.RegistrationDefinitionId,
                AzureLighthouseOfferIdentity.RegistrationDefinitionId(subscriptionId),
                StringComparison.OrdinalIgnoreCase))
            return Failed("azure.lighthouse.registration-mismatch", "The requested Lighthouse registration is not the versioned provider registration.");

        try
        {
            var login = await _process.ExecuteAsync(
                Command(["login", "--identity", "--allow-no-subscriptions", "--client-id", configuredClientId, "--output", "none", "--only-show-errors"]),
                static _ => AzureCommandNoOutput.Instance,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!login.Succeeded)
                return Failed("azure.lighthouse.authentication-failed", "The managing Azure identity could not authenticate.");

            var account = await _process.ExecuteAsync(
                Command(["account", "show", "--subscription", subscriptionId, "--query", "{id:id,tenantId:tenantId,name:user.name,type:user.type,identity:user.assignedIdentityInfo}", "--output", "json", "--only-show-errors"]),
                ParseSubscription,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!account.Succeeded)
                return Failed("azure.lighthouse.subscription-not-visible", "The customer subscription is not visible to the managing Azure identity.");
            if (!string.Equals(account.Value!.SubscriptionId, subscriptionId, StringComparison.Ordinal) ||
                !string.Equals(account.Value.TenantId, customerTenantId, StringComparison.Ordinal) ||
                !string.Equals(account.Value.ClientId, configuredClientId, StringComparison.Ordinal))
                return Failed("azure.lighthouse.subscription-mismatch", "The observed subscription does not match the requested bind.");

            var assignments = await _process.ExecuteAsync(
                Command(["rest", "--method", "get", "--url",
                    ManagementUrl($"/subscriptions/{subscriptionId}/providers/Microsoft.ManagedServices/registrationAssignments?api-version=2019-06-01"),
                    "--output", "json", "--only-show-errors"]),
                output => ParseRegistrationAssignments(output, subscriptionId),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!assignments.Succeeded)
                return Failed("azure.lighthouse.registration-observation-failed", "The Lighthouse registration assignment could not be observed.");
            var expectedAssignmentId = AzureLighthouseOfferIdentity.RegistrationAssignmentId(subscriptionId);
            if (!assignments.Value!.Assignments.Any(x =>
                    string.Equals(x.AssignmentId, expectedAssignmentId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.RegistrationDefinitionId, request.RegistrationDefinitionId, StringComparison.OrdinalIgnoreCase)))
                return Failed("azure.lighthouse.registration-not-found", "The requested Lighthouse registration is not assigned to the customer subscription.");

            var definition = await _process.ExecuteAsync(
                Command(["rest", "--method", "get", "--url", ManagementUrl(request.RegistrationDefinitionId + "?api-version=2019-06-01"),
                    "--output", "json", "--only-show-errors"]),
                ParseRegistrationDefinition,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!definition.Succeeded)
                return Failed("azure.lighthouse.registration-observation-failed", "The Lighthouse registration definition could not be observed.");

            var observed = definition.Value!;
            if (!string.Equals(observed.ManagedByTenantId, managingTenantId, StringComparison.Ordinal) ||
                observed.Authorizations.Count != 2 ||
                !observed.Authorizations.Any(x =>
                    string.Equals(x.PrincipalId, managingPrincipalObjectId, StringComparison.Ordinal) &&
                    string.Equals(x.RoleDefinitionId, AzureProviderAuthorityRoleDefinitionIds.Contributor, StringComparison.OrdinalIgnoreCase) &&
                    x.DelegatedRoleDefinitionIds.Count == 0) ||
                !observed.Authorizations.Any(x =>
                    string.Equals(x.PrincipalId, managingPrincipalObjectId, StringComparison.Ordinal) &&
                    string.Equals(x.RoleDefinitionId, AzureProviderAuthorityRoleDefinitionIds.UserAccessAdministrator, StringComparison.OrdinalIgnoreCase) &&
                    x.DelegatedRoleDefinitionIds.Count == 1 &&
                    x.DelegatedRoleDefinitionIds.Contains(AzureProviderAuthorityRoleDefinitionIds.KeyVaultSecretsUser, StringComparer.OrdinalIgnoreCase)))
                return Failed("azure.lighthouse.rbac-insufficient", "The Lighthouse registration does not grant Contributor and limited User Access Administrator authority to the managing principal.");

            if (!string.IsNullOrWhiteSpace(request.RegistrationDefinitionFingerprint) &&
                !string.Equals(request.RegistrationDefinitionFingerprint, observed.Fingerprint, StringComparison.OrdinalIgnoreCase))
                return Failed("azure.lighthouse.registration-fingerprint-mismatch", "The observed Lighthouse registration does not match the recorded definition fingerprint.");

            return new(true, "azure.lighthouse.observation-succeeded", "The customer subscription and Lighthouse delegation are available to the managing identity.", observed.Fingerprint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failed("azure.lighthouse.observation-failed", "The delegated Azure authority could not be observed.");
        }
    }

    private AzureCommandProcessRequest Command(IReadOnlyList<string> arguments) =>
        new(_options.AzureCliPath, arguments.Select(AzureCommandArgument.Safe).ToArray());

    private static string ManagementUrl(string path) => "https://management.azure.com" + path;

    private static SafeSubscription ParseSubscription(ReadOnlyMemory<char> output)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) ||
            !root.TryGetProperty("tenantId", out var tenant) || !root.TryGetProperty("name", out var name) ||
            !root.TryGetProperty("type", out var type) || !root.TryGetProperty("identity", out var identity) ||
            id.ValueKind != JsonValueKind.String || tenant.ValueKind != JsonValueKind.String ||
            name.ValueKind != JsonValueKind.String || type.ValueKind != JsonValueKind.String || identity.ValueKind != JsonValueKind.String ||
            !TryNormalizeGuid(id.GetString(), out var subscriptionId) || !TryNormalizeGuid(tenant.GetString(), out var tenantId) ||
            !string.Equals(name.GetString(), "userAssignedIdentity", StringComparison.Ordinal) ||
            !string.Equals(type.GetString(), "servicePrincipal", StringComparison.Ordinal))
            throw new FormatException("The Azure subscription observation is invalid.");
        const string prefix = "MSIClient-";
        var identityValue = identity.GetString();
        if (identityValue is null || !identityValue.StartsWith(prefix, StringComparison.Ordinal) ||
            !TryNormalizeGuid(identityValue[prefix.Length..], out var clientId))
            throw new FormatException("The Azure account identity observation is invalid.");
        return new(subscriptionId, tenantId, clientId);
    }

    private static SafeRegistrationAssignments ParseRegistrationAssignments(ReadOnlyMemory<char> output, string subscriptionId)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new FormatException("The Lighthouse registration assignment observation is invalid.");

        var assignments = new List<SafeRegistrationAssignment>();
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var assignmentId) ||
                assignmentId.ValueKind != JsonValueKind.String ||
                !IsRegistrationAssignmentPath(assignmentId.GetString(), subscriptionId) ||
                !item.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty("registrationDefinitionId", out var id) ||
                id.ValueKind != JsonValueKind.String || !IsRegistrationDefinitionPath(id.GetString(), subscriptionId))
                throw new FormatException("The Lighthouse registration assignment observation is invalid.");
            assignments.Add(new(assignmentId.GetString()!, id.GetString()!));
        }
        return new(assignments);
    }

    private static SafeRegistrationDefinition ParseRegistrationDefinition(ReadOnlyMemory<char> output)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            !properties.TryGetProperty("managedByTenantId", out var managedBy) || managedBy.ValueKind != JsonValueKind.String ||
            !TryNormalizeGuid(managedBy.GetString(), out var managedByTenantId) || !properties.TryGetProperty("authorizations", out var authorizations) ||
            authorizations.ValueKind != JsonValueKind.Array)
            throw new FormatException("The Lighthouse registration definition observation is invalid.");

        var normalized = new List<SafeAuthorization>();
        foreach (var item in authorizations.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("principalId", out var principal) ||
                principal.ValueKind != JsonValueKind.String || !TryNormalizeGuid(principal.GetString(), out var principalId) ||
                !item.TryGetProperty("roleDefinitionId", out var role) || role.ValueKind != JsonValueKind.String ||
                !TryReadGuidTail(role.GetString(), out var roleId))
                throw new FormatException("The Lighthouse authorization observation is invalid.");
            var delegated = new List<string>();
            if (item.TryGetProperty("delegatedRoleDefinitionIds", out var delegatedElement))
            {
                if (delegatedElement.ValueKind != JsonValueKind.Array)
                    throw new FormatException("The Lighthouse delegated role observation is invalid.");
                foreach (var value in delegatedElement.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String || !TryReadGuidTail(value.GetString(), out var delegatedId))
                        throw new FormatException("The Lighthouse delegated role observation is invalid.");
                    delegated.Add(delegatedId);
                }
            }
            normalized.Add(new(principalId, roleId, delegated));
        }

        var canonical = JsonSerializer.Serialize(new
        {
            managedByTenantId,
            authorizations = normalized
                .OrderBy(x => x.PrincipalId, StringComparer.Ordinal)
                .ThenBy(x => x.RoleDefinitionId, StringComparer.Ordinal)
                .Select(x => new { x.PrincipalId, x.RoleDefinitionId, delegatedRoleDefinitionIds = x.DelegatedRoleDefinitionIds.Order(StringComparer.Ordinal).ToArray() })
                .ToArray()
        });
        return new(managedByTenantId, normalized, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private static bool IsRegistrationDefinitionPath(string? value, string? subscriptionId = null) =>
        IsRegistrationResourcePath(value, subscriptionId, "registrationDefinitions");

    private static bool IsRegistrationAssignmentPath(string? value, string subscriptionId) =>
        IsRegistrationResourcePath(value, subscriptionId, "registrationAssignments");

    private static bool IsRegistrationResourcePath(string? value, string? subscriptionId, string resourceType)
    {
        if (value is null || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
            value.Contains('?', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal))
            return false;

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 6 &&
               string.Equals(segments[0], "subscriptions", StringComparison.OrdinalIgnoreCase) &&
               TryNormalizeGuid(segments[1], out var observedSubscription) &&
               (subscriptionId is null || string.Equals(observedSubscription, subscriptionId, StringComparison.Ordinal)) &&
               string.Equals(segments[2], "providers", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[3], "Microsoft.ManagedServices", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[4], resourceType, StringComparison.OrdinalIgnoreCase) &&
               TryNormalizeGuid(segments[5], out _);
    }

    private static bool TryReadGuidTail(string? value, out string normalized)
    {
        normalized = "";
        if (value is null)
            return false;
        var tail = value[(value.LastIndexOf('/') + 1)..];
        if (!Guid.TryParseExact(tail, "D", out var guid) || guid == Guid.Empty)
            return false;
        normalized = guid.ToString("D");
        return true;
    }

    private static bool TryNormalizeGuid(string? value, out string normalized)
    {
        normalized = "";
        return Guid.TryParseExact(value, "D", out var guid) && guid != Guid.Empty && (normalized = guid.ToString("D")) is not null;
    }

    private static AzureLighthouseAuthorityObservationResult Failed(string code, string message) => new(false, code, message);

    private sealed class SafeSubscription(string subscriptionId, string tenantId, string clientId) : AzureCommandSafeOutput
    {
        public string SubscriptionId { get; } = subscriptionId;
        public string TenantId { get; } = tenantId;
        public string ClientId { get; } = clientId;
    }

    private sealed class SafeRegistrationAssignments(IReadOnlyList<SafeRegistrationAssignment> assignments) : AzureCommandSafeOutput
    {
        public IReadOnlyList<SafeRegistrationAssignment> Assignments { get; } = assignments;
    }

    private sealed record SafeRegistrationAssignment(string AssignmentId, string RegistrationDefinitionId);

    private sealed class SafeRegistrationDefinition(string managedByTenantId, IReadOnlyList<SafeAuthorization> authorizations, string fingerprint) : AzureCommandSafeOutput
    {
        public string ManagedByTenantId { get; } = managedByTenantId;
        public IReadOnlyList<SafeAuthorization> Authorizations { get; } = authorizations;
        public string Fingerprint { get; } = fingerprint;
    }

    private sealed record SafeAuthorization(string PrincipalId, string RoleDefinitionId, IReadOnlyList<string> DelegatedRoleDefinitionIds);
}
