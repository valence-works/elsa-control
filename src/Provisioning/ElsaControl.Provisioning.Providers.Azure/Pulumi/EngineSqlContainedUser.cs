using Microsoft.Data.SqlClient;
using Pulumi;

namespace ElsaControl.Provisioning.Providers.Azure.Programs;

internal sealed class EngineSqlContainedUserArgs
{
    public Input<string> FullyQualifiedDomainName { get; set; } = null!;
    public Input<string> ClientId { get; set; } = null!;
    public Input<string> DisplayName { get; set; } = null!;
    public Input<string> DatabaseId { get; set; } = null!;
}

internal interface IEngineSqlContainedUserGrant
{
    void Apply(string fullyQualifiedDomainName, string clientId, string displayName, string databaseId);
}

/// <summary>
/// Test-only seam. Production uses the Azure SQL grant. Never a public static
/// mutable Action on the component.
/// </summary>
internal static class EngineSqlContainedUserSeam
{
    private static readonly AsyncLocal<IEngineSqlContainedUserGrant?> Current = new();
    private static IEngineSqlContainedUserGrant? Fallback;

    internal static IEngineSqlContainedUserGrant Active =>
        Current.Value ?? Fallback ?? AzureSqlContainedUserGrant.Instance;

    internal static IDisposable Use(IEngineSqlContainedUserGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var previousLocal = Current.Value;
        var previousFallback = Fallback;
        Current.Value = grant;
        Fallback = grant;
        return new Reset(previousLocal, previousFallback);
    }

    private sealed class Reset(
        IEngineSqlContainedUserGrant? previousLocal,
        IEngineSqlContainedUserGrant? previousFallback) : IDisposable
    {
        public void Dispose()
        {
            Current.Value = previousLocal;
            Fallback = previousFallback;
        }
    }
}

internal sealed class AzureSqlContainedUserGrant : IEngineSqlContainedUserGrant
{
    internal static AzureSqlContainedUserGrant Instance { get; } = new();

    public void Apply(string fullyQualifiedDomainName, string clientId, string displayName, string databaseId) =>
        EngineSqlContainedUser.Grant(fullyQualifiedDomainName, clientId, displayName, databaseId);
}

/// <summary>
/// Tracked contained Azure SQL user matching Bicep's sql-bootstrap.sql.
/// Depends on the database and the server's Entra admin. Skipped during
/// Preview. The server Entra admin is the governed bootstrap principal,
/// not this runtime identity. The grant Apply waits on the database output
/// so it cannot run before the database exists.
/// </summary>
internal sealed class EngineSqlContainedUser : ComponentResource
{
    public const string ResourceTypeName = "elsacontrol:sql:ContainedUser";

    public EngineSqlContainedUser(
        string name,
        EngineSqlContainedUserArgs args,
        ComponentResourceOptions? options = null)
        : base(ResourceTypeName, name, options)
    {
        ArgumentNullException.ThrowIfNull(args);
        DisplayName = Output.Tuple(
            args.FullyQualifiedDomainName.Apply(value => value),
            args.ClientId.Apply(value => value),
            args.DisplayName.Apply(value => value),
            args.DatabaseId.Apply(value => value)).Apply(tuple =>
        {
            if (!IsPreview())
                EngineSqlContainedUserSeam.Active.Apply(tuple.Item1, tuple.Item2, tuple.Item3, tuple.Item4);
            return tuple.Item3;
        });
        RegisterOutputs(new Dictionary<string, object?> { ["displayName"] = DisplayName });
    }

    public Output<string> DisplayName { get; }

    internal static void Grant(
        string fullyQualifiedDomainName,
        string clientId,
        string displayName,
        string? databaseId = null)
    {
        if (IsPreview())
            return;

        if (!fullyQualifiedDomainName.EndsWith(".database.windows.net", StringComparison.OrdinalIgnoreCase))
            return;
        if (!Guid.TryParse(clientId, out var parsed) || parsed == Guid.Empty)
            throw new InvalidOperationException("Runtime identity is invalid.");
        if (!IsSafeSqlName(displayName))
            throw new InvalidOperationException("Runtime identity name is invalid.");

        var sidHex = "0x" + Convert.ToHexString(parsed.ToByteArray());
        var escaped = displayName.Replace("]", "]]", StringComparison.Ordinal);
        var sql = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{escaped}')
            BEGIN
                EXEC (N'CREATE USER [{escaped}] WITH SID = {sidHex}, TYPE = E');
            END;
            IF NOT EXISTS (
                SELECT 1 FROM sys.database_role_members drm
                JOIN sys.database_principals role_principal ON role_principal.principal_id = drm.role_principal_id
                JOIN sys.database_principals member_principal ON member_principal.principal_id = drm.member_principal_id
                WHERE role_principal.name = N'db_datareader' AND member_principal.name = N'{escaped}')
                ALTER ROLE db_datareader ADD MEMBER [{escaped}];
            IF NOT EXISTS (
                SELECT 1 FROM sys.database_role_members drm
                JOIN sys.database_principals role_principal ON role_principal.principal_id = drm.role_principal_id
                JOIN sys.database_principals member_principal ON member_principal.principal_id = drm.member_principal_id
                WHERE role_principal.name = N'db_datawriter' AND member_principal.name = N'{escaped}')
                ALTER ROLE db_datawriter ADD MEMBER [{escaped}];
            IF NOT EXISTS (
                SELECT 1 FROM sys.database_role_members drm
                JOIN sys.database_principals role_principal ON role_principal.principal_id = drm.role_principal_id
                JOIN sys.database_principals member_principal ON member_principal.principal_id = drm.member_principal_id
                WHERE role_principal.name = N'db_ddladmin' AND member_principal.name = N'{escaped}')
                ALTER ROLE db_ddladmin ADD MEMBER [{escaped}];
            """;

        var databaseName = string.IsNullOrWhiteSpace(databaseId) ? "Elsa" : databaseId;
        if (!IsSafeSqlName(databaseName))
            throw new InvalidOperationException("Engine database name is invalid.");

        using var connection = new SqlConnection(
            $"Server={fullyQualifiedDomainName};Database={databaseName};Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Default;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool IsPreview()
    {
        try
        {
            return Deployment.Instance.IsDryRun;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsSafeSqlName(string value) =>
        value.Length is >= 1 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
