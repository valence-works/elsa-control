using Microsoft.Data.SqlClient;
using Pulumi;

namespace ElsaControl.Provisioning.Providers.Azure.Programs;

/// <summary>
/// Creates the runtime identity as a contained Azure SQL database user, matching
/// Bicep's sql-bootstrap.sql. The server Entra admin is the governed bootstrap
/// principal, not this runtime identity.
/// </summary>
internal static class EngineSqlContainedUser
{
    public static Output<string> Ensure(
        Output<string> fullyQualifiedDomainName,
        Output<string> clientId,
        Output<string> displayName)
    {
        return Output.Tuple(fullyQualifiedDomainName, clientId, displayName).Apply(tuple =>
        {
            Grant(tuple.Item1, tuple.Item2, tuple.Item3);
            return tuple.Item3;
        });
    }

    internal static void Grant(string fullyQualifiedDomainName, string clientId, string displayName)
    {
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

        using var connection = new SqlConnection(
            $"Server={fullyQualifiedDomainName};Database=Elsa;Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Default;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool IsSafeSqlName(string value) =>
        value.Length is >= 1 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
