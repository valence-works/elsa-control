using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Explicit immutable Azure placement authority for one provider operation. Its fingerprint is
/// persisted with the operation so a restarted worker cannot silently move the target or registry.
/// </summary>
public sealed record AzureProviderTargetScope(
    string SubscriptionId,
    string ResourceGroupName,
    string RegistrySubscriptionId,
    string RegistryResourceGroupName,
    string RegistryName,
    string Location)
{
    public const string ConfigurationSection = "Deployment:AzureProvider:Runner:TargetScope";
    public string ComputeFingerprint()
    {
        Validate();
        var canonical = JsonSerializer.Serialize(new
        {
            subscriptionId = SubscriptionId.ToLowerInvariant(),
            resourceGroupName = ResourceGroupName.ToLowerInvariant(),
            registrySubscriptionId = RegistrySubscriptionId.ToLowerInvariant(),
            registryResourceGroupName = RegistryResourceGroupName.ToLowerInvariant(),
            registryName = RegistryName.ToLowerInvariant(),
            location = Location.Trim().ToLowerInvariant()
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public void Validate()
    {
        ValidateGuid(SubscriptionId, nameof(SubscriptionId));
        ValidateGuid(RegistrySubscriptionId, nameof(RegistrySubscriptionId));
        ValidateResourceGroup(ResourceGroupName, nameof(ResourceGroupName));
        ValidateResourceGroup(RegistryResourceGroupName, nameof(RegistryResourceGroupName));
        if (!Regex.IsMatch(RegistryName ?? "", "^[a-z0-9]{5,50}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new ArgumentException("The Azure registry name is unsafe.", nameof(RegistryName));
        if (!AzureWorkloadPlanTranslator.IsSupportedLocation(Location))
            throw new ArgumentException("The Azure location is outside the governed provider profile.", nameof(Location));
    }

    private static void ValidateGuid(string? value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out _) || !string.Equals(value, value?.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException("The Azure subscription ID must be a canonical GUID.", name);
    }

    private static void ValidateResourceGroup(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 90 ||
            !Regex.IsMatch(value, "^[A-Za-z0-9._()\\-]+\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new ArgumentException("The Azure resource group name is unsafe.", name);
    }
}

/// <summary>
/// Control-owned inputs for the runtime's managed Elsa handoff (<c>managed-elsa-handoff-v1</c>): the Control
/// origin the runtime redeems codes at, the console route it returns the browser to, the runtime-session ceiling
/// Control enforces and the runtime grant of a handed-off operator. The host derives them from Control's own
/// configuration. They are bound into the provider scope fingerprint, so an admitted operation cannot deploy a
/// different Control trust anchor, session ceiling or grant than the one it was admitted under.
/// </summary>
public sealed record AzureManagedHandoffOptions(
    string ControlBaseUrl,
    string ControlContinuationUrl,
    TimeSpan RuntimeMaximumLifetime,
    IReadOnlyList<string> RuntimePermissions)
{
    /// <summary>The runtime rejects a longer session ceiling at startup.</summary>
    public static readonly TimeSpan MaximumRuntimeLifetime = TimeSpan.FromHours(8);

    /// <summary>Exact <c>hh:mm:ss</c> value the runtime binds its session ceiling from.</summary>
    public string RuntimeMaximumLifetimeValue =>
        RuntimeMaximumLifetime.ToString("c", System.Globalization.CultureInfo.InvariantCulture);

    public void Validate()
    {
        if (!TryGetHttpsUri(ControlBaseUrl, out var controlBase) || controlBase.AbsolutePath != "/" ||
            !string.Equals(ControlBaseUrl, controlBase.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            throw new ArgumentException("The managed handoff Control base URL must be a canonical HTTPS origin without a path.", nameof(ControlBaseUrl));
        if (!TryGetHttpsUri(ControlContinuationUrl, out var continuation) || continuation.AbsolutePath == "/" ||
            !string.Equals(continuation.GetLeftPart(UriPartial.Authority), ControlBaseUrl, StringComparison.Ordinal))
            throw new ArgumentException("The managed handoff continuation must be a route on the Control origin without a query or fragment.", nameof(ControlContinuationUrl));
        if (RuntimeMaximumLifetime <= TimeSpan.Zero || RuntimeMaximumLifetime > MaximumRuntimeLifetime ||
            RuntimeMaximumLifetime.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(RuntimeMaximumLifetime), "The runtime session ceiling must be whole seconds, positive and at most eight hours.");
        if (RuntimePermissions is not { Count: > 0 and <= 32 } ||
            RuntimePermissions.Distinct(StringComparer.Ordinal).Count() != RuntimePermissions.Count ||
            RuntimePermissions.Any(permission => string.IsNullOrWhiteSpace(permission) || permission.Length > 128 ||
                                                 permission.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is ',' or '"')))
            throw new ArgumentException("The runtime grant must be one to 32 distinct, safe permission names.", nameof(RuntimePermissions));
    }

    private static bool TryGetHttpsUri(string? value, out Uri uri)
    {
        uri = null!;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 2048 && !value.Any(char.IsControl) &&
               Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
               uri.Scheme == Uri.UriSchemeHttps && uri.HostNameType == UriHostNameType.Dns &&
               string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }
}

/// <summary>
/// Hardened local tooling and policy options for the checked-in Azure Bicep lifecycle.
/// Construction does not enable the provider; hosts must validate these options explicitly.
/// </summary>
public sealed record AzureProviderRunnerOptions
{
    public const string ConfigurationSection = "Deployment:AzureProvider:Runner";
    public const string DefaultReleaseFeedServiceIndex = "https://api.nuget.org/v3/index.json";
    public bool Enabled { get; init; }
    /// <summary>Absolute Azure CLI executable bound into provider authority.</summary>
    public string AzureCliPath { get; init; } = "";
    /// <summary>Required user-assigned managed identity client ID used by Azure CLI login.</summary>
    public string? AzureCliClientId { get; init; }
    /// <summary>Absolute sqlcmd executable bound into provider authority.</summary>
    public string SqlCmdPath { get; init; } = "";
    /// <summary>Absolute curl executable used for the post-promotion health probe.</summary>
    public string CurlPath { get; init; } = "";
    public string TemplateRoot { get; init; } = "";
    public string SqlBootstrapObjectId { get; init; } = "";
    public string SqlBootstrapLogin { get; init; } = "";
    public string SqlBootstrapIp { get; init; } = "";
    public string RuntimeAdminUsername { get; init; } = "";
    /// <summary>Server-governed Nuplane release package feed. Customer requests cannot override it.</summary>
    public string ReleaseFeedServiceIndex { get; init; } = DefaultReleaseFeedServiceIndex;
    /// <summary>Selects the legacy disposable-proof template and ownership dialect.</summary>
    public bool DisposableProofMode { get; init; }
    /// <summary>Expiry bound to disposable-proof ownership. Production deployments omit it.</summary>
    public DateOnly? DisposableExpiryUtc { get; init; }
    /// <summary>
    /// Registry authority dialect. The built-in default intentionally leaves the legacy
    /// execution-authority fingerprint byte-compatible for retained operations.
    /// </summary>
    public AzureProviderRegistryAuthorityMode RegistryAuthorityMode { get; init; }
    /// <summary>Full resource ID of the reviewed custom registry metadata role definition.</summary>
    public string? RegistryDeploymentMetadataRoleDefinitionId { get; init; }
    /// <summary>Full resource ID of the exact registry resource-group role assignment.</summary>
    public string? RegistryDeploymentMetadataRoleAssignmentId { get; init; }
    /// <summary>Full resource ID of the exact registry-scoped RBAC administrator assignment.</summary>
    public string? RegistryRoleAdministrationAssignmentId { get; init; }
    public string Owner { get; init; } = "elsa-control";
    /// <summary>
    /// Control's managed handoff inputs. Absent, a release that declares the handoff cannot be deployed:
    /// the runner fails closed rather than creating a runtime Control would advertise as openable.
    /// </summary>
    public AzureManagedHandoffOptions? ManagedHandoff { get; init; }
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public int MaximumOutputCharacters { get; init; } = 1_048_576;
    public int ObservationAttempts { get; init; } = 60;
    public TimeSpan ObservationDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Binds the durable operation to every safe configuration value that selects or authorizes
    /// remote Azure mutation. Template contents are bound separately by TemplateFingerprint.
    /// </summary>
    public string ComputeProviderScopeFingerprint(AzureProviderTargetScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Validate();
        scope.Validate();
        ValidateRegistryAuthority(scope);
        // Property order is a persisted contract, including the original BuiltIn payload.
        using var canonical = new MemoryStream();
        using var writer = new Utf8JsonWriter(canonical);
        writer.WriteStartObject();
        writer.WriteString("targetScopeFingerprint", scope.ComputeFingerprint());
        writer.WriteString("azureCliPath", Path.GetFullPath(AzureCliPath));
        writer.WriteString("azureCliDigest", ComputeFileDigest(AzureCliPath));
        writer.WriteString("azureCliClientId", AzureCliClientId?.ToLowerInvariant());
        writer.WriteString("sqlCmdPath", Path.GetFullPath(SqlCmdPath));
        writer.WriteString("sqlCmdDigest", ComputeFileDigest(SqlCmdPath));
        writer.WriteString("curlPath", Path.GetFullPath(CurlPath));
        writer.WriteString("curlDigest", ComputeFileDigest(CurlPath));
        writer.WriteString("templateRoot", NormalizeRoot(TemplateRoot));
        writer.WriteString("templateAuthorityFingerprint", ComputeTemplateAuthorityFingerprint());
        writer.WriteString("sqlBootstrapObjectId", SqlBootstrapObjectId.ToLowerInvariant());
        writer.WriteString("sqlBootstrapLogin", SqlBootstrapLogin);
        writer.WriteString("sqlBootstrapIp", SqlBootstrapIp);
        writer.WriteString("runtimeAdminUsername", RuntimeAdminUsername);
        writer.WriteString("releaseFeedServiceIndex", NormalizeReleaseFeedServiceIndex());
        writer.WriteBoolean("disposableProofMode", DisposableProofMode);
        writer.WriteString("disposableExpiryUtc", DisposableExpiryUtc?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString("owner", Owner.ToLowerInvariant());
        if (RegistryAuthorityMode == AzureProviderRegistryAuthorityMode.Narrow)
        {
            writer.WriteStartObject("registryAuthority");
            writer.WriteString("mode", RegistryAuthorityMode.ToString());
            writer.WriteString("roleDefinitionId", RegistryDeploymentMetadataRoleDefinitionId!.ToLowerInvariant());
            writer.WriteString("roleAssignmentId", RegistryDeploymentMetadataRoleAssignmentId!.ToLowerInvariant());
            writer.WriteString("roleAdministrationAssignmentId", RegistryRoleAdministrationAssignmentId!.ToLowerInvariant());
            writer.WriteEndObject();
        }
        // Written only when configured, so a scope without the handoff keeps its prior fingerprint.
        if (ManagedHandoff is { } handoff)
        {
            writer.WriteStartObject("managedHandoff");
            writer.WriteString("controlBaseUrl", handoff.ControlBaseUrl);
            writer.WriteString("controlContinuationUrl", handoff.ControlContinuationUrl);
            writer.WriteString("runtimeMaximumLifetime", handoff.RuntimeMaximumLifetimeValue);
            writer.WriteStartArray("runtimePermissions");
            foreach (var permission in handoff.RuntimePermissions)
                writer.WriteStringValue(permission);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(canonical.ToArray()));
    }

    /// <summary>
    /// Recomputes every local and remote mutation authority immediately before execution.
    /// Legacy durable rows may omit a scope fingerprint; the concrete runner never may.
    /// </summary>
    public void ValidateExecutionAuthority(
        AzureProviderExecutionContext context,
        AzureProviderTargetScope scope)
    {
        ArgumentNullException.ThrowIfNull(context);
        var actual = context.ProviderScopeFingerprint;
        var expected = ComputeProviderScopeFingerprint(scope);
        if (actual is null || actual.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expected)))
            throw new InvalidOperationException("The Azure runner authority does not match the durable operation.");
    }

    public void Validate()
    {
        if (!Enabled)
            throw new InvalidOperationException("The concrete Azure provider runner is not enabled.");
        ValidateExecutable(AzureCliPath, nameof(AzureCliPath));
        if (!DisposableProofMode && (!Guid.TryParseExact(AzureCliClientId, "D", out _) ||
            !string.Equals(AzureCliClientId, AzureCliClientId?.ToLowerInvariant(), StringComparison.Ordinal)))
            throw new ArgumentException("The Azure CLI managed identity client ID must be a canonical GUID.", nameof(AzureCliClientId));
        ValidateExecutable(SqlCmdPath, nameof(SqlCmdPath));
        ValidateExecutable(CurlPath, nameof(CurlPath));
        if (string.IsNullOrWhiteSpace(TemplateRoot) || !Path.IsPathFullyQualified(TemplateRoot))
            throw new ArgumentException("The Azure template root must be an absolute path.", nameof(TemplateRoot));
        var normalizedRoot = NormalizeRoot(TemplateRoot);
        if (!Directory.Exists(normalizedRoot) || IsSymbolicLink(normalizedRoot))
            throw new ArgumentException("The Azure template root must be a regular trusted directory.", nameof(TemplateRoot));
        RequireCheckedInFile(normalizedRoot, "main.bicep");
        RequireCheckedInFile(normalizedRoot, "acr-pull-role.bicep");
        RequireCheckedInFile(normalizedRoot, "sql-bootstrap.sql");
        _ = ComputeTemplateAuthorityFingerprint(normalizedRoot);
        if (!Guid.TryParseExact(SqlBootstrapObjectId, "D", out _) ||
            !string.Equals(SqlBootstrapObjectId, SqlBootstrapObjectId.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException("The SQL bootstrap object ID must be a canonical GUID.", nameof(SqlBootstrapObjectId));
        if (!Regex.IsMatch(SqlBootstrapLogin ?? "", "^[A-Za-z0-9._@#-]{1,128}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new ArgumentException("The SQL bootstrap login is unsafe.", nameof(SqlBootstrapLogin));
        if (!Regex.IsMatch(RuntimeAdminUsername ?? "", "^[A-Za-z0-9._@#-]{1,128}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new ArgumentException("The runtime administrator username is unsafe.", nameof(RuntimeAdminUsername));
        if (DisposableProofMode != DisposableExpiryUtc.HasValue)
            throw new ArgumentException("Disposable proof mode requires one explicit expiry, and production mode must not carry one.", nameof(DisposableExpiryUtc));
        if (!System.Net.IPAddress.TryParse(SqlBootstrapIp, out var ip) ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            SqlBootstrapIp == "0.0.0.0" ||
            !string.Equals(SqlBootstrapIp, ip.ToString(), StringComparison.Ordinal))
            throw new ArgumentException("The SQL bootstrap address must be one exact non-zero IPv4 address.", nameof(SqlBootstrapIp));
        if (!Regex.IsMatch(Owner ?? "", "^[a-z0-9][a-z0-9-]{0,62}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new ArgumentException("The Azure owner tag is unsafe.", nameof(Owner));
        AzureProviderRegistryAuthority.ValidateConfiguration(
            RegistryAuthorityMode,
            RegistryDeploymentMetadataRoleDefinitionId,
            RegistryDeploymentMetadataRoleAssignmentId,
            RegistryRoleAdministrationAssignmentId);
        _ = NormalizeReleaseFeedServiceIndex();
        if (ManagedHandoff is not null)
        {
            if (DisposableProofMode)
                throw new ArgumentException("The disposable template does not configure the managed handoff.", nameof(ManagedHandoff));
            ManagedHandoff.Validate();
        }
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout), "The command timeout must be positive and no longer than one hour.");
        if (MaximumOutputCharacters is < 1024 or > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(MaximumOutputCharacters), "The command output cap is outside the governed range.");
        if (ObservationAttempts is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(ObservationAttempts));
        if (ObservationDelay < TimeSpan.Zero || ObservationDelay > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(ObservationDelay));
    }

    /// <summary>Validates pinned registry authority against the target scope without filesystem access.</summary>
    public void ValidateRegistryAuthority(AzureProviderTargetScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        AzureProviderRegistryAuthority.ValidateForScope(
            scope,
            RegistryAuthorityMode,
            RegistryDeploymentMetadataRoleDefinitionId,
            RegistryDeploymentMetadataRoleAssignmentId,
            RegistryRoleAdministrationAssignmentId);
    }

    private static void ValidateExecutable(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) || !File.Exists(value) || IsSymbolicLink(value))
            throw new ArgumentException("The executable locator is unsafe.", name);
    }

    internal string NormalizeReleaseFeedServiceIndex()
    {
        if (string.IsNullOrWhiteSpace(ReleaseFeedServiceIndex) || ReleaseFeedServiceIndex.Length > 2048 ||
            ReleaseFeedServiceIndex.Any(char.IsControl) || ReleaseFeedServiceIndex.Contains('\\') ||
            ReleaseFeedServiceIndex.Contains('?') || ReleaseFeedServiceIndex.Contains('#') ||
            ReleaseFeedServiceIndex.Contains('@'))
            throw new ArgumentException("The release feed service index is unsafe.", nameof(ReleaseFeedServiceIndex));

        var value = ReleaseFeedServiceIndex.Trim();
        if (value.Any(char.IsWhiteSpace) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.HostNameType != UriHostNameType.Dns ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.AbsolutePath is "" or "/" || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("The release feed service index is unsafe.", nameof(ReleaseFeedServiceIndex));

        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri;
    }

    public string ComputeTemplateAuthorityFingerprint()
    {
        if (string.IsNullOrWhiteSpace(TemplateRoot) || !Path.IsPathFullyQualified(TemplateRoot))
            throw new ArgumentException("The Azure template root must be an absolute path.", nameof(TemplateRoot));
        return ComputeTemplateAuthorityFingerprint(
            NormalizeRoot(TemplateRoot));
    }

    private static string ComputeTemplateAuthorityFingerprint(string root)
    {
        var directories = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Take(33).ToArray()
            : [];
        if (!Directory.Exists(root) || IsSymbolicLink(root) || directories.Length > 32 || directories.Any(IsSymbolicLink))
            throw new ArgumentException("The Azure template root must be a regular trusted directory.", nameof(TemplateRoot));

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(257).ToArray();
        if (files.Length > 256)
            throw new ArgumentException("The checked-in Azure provider authority is outside the governed bounds.", nameof(TemplateRoot));
        var authorityFiles = files
            .Where(path => string.Equals(Path.GetExtension(path), ".bicep", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (authorityFiles.Length is 0 or > 64 || authorityFiles.Any(IsSymbolicLink))
            throw new ArgumentException("The checked-in Azure provider authority is incomplete.", nameof(TemplateRoot));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in authorityFiles)
        {
            var relativePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relativePath.StartsWith("../", StringComparison.Ordinal))
                throw new ArgumentException("The checked-in Azure provider authority is incomplete.", nameof(TemplateRoot));
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);
            AppendFileContents(hash, path);
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void RequireCheckedInFile(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(path) ||
            IsSymbolicLink(path))
            throw new ArgumentException("The checked-in Azure provider authority is incomplete.", nameof(TemplateRoot));
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Attribute races, access failures and disappearing authority files are all unsafe.
            return true;
        }
    }

    private static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private static string ComputeFileDigest(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void AppendFileContents(IncrementalHash hash, string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hash.AppendData(buffer, 0, read);
    }
}
