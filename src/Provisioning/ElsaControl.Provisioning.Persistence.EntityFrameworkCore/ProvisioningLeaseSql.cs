namespace ElsaControl.Provisioning.Persistence.EntityFrameworkCore;

/// <summary>
/// SQL Server lease statements. Schema and table names are C# string text,
/// never FormattableString holes, so EF cannot turn them into parameters.
/// </summary>
internal static class ProvisioningLeaseSql
{
    internal static string LocksTable { get; } =
        $"[{ProvisioningDbContext.SchemaName}].[Locks]";

    internal static string StampExpiry() =>
        $$"""
          UPDATE {{LocksTable}}
          SET [LeaseExpiresAt] = TODATETIMEOFFSET(DATEADD(second, {0}, SYSUTCDATETIME()), 0)
          WHERE [LockKey] = {1}
            AND [Owner] = {2}
            AND [FencingToken] = {3}
          """;

    internal static string ListExpired() =>
        $"""
         SELECT [OperationId] AS [Value]
         FROM {LocksTable}
         WHERE [LeaseExpiresAt] <= TODATETIMEOFFSET(SYSUTCDATETIME(), 0)
         """;

    internal static string LeaseExpired() =>
        $$"""
          SELECT CASE WHEN EXISTS (
              SELECT 1 FROM {{LocksTable}}
              WHERE [LockKey] = {0}
                AND [OperationId] = {1}
                AND [LeaseExpiresAt] <= TODATETIMEOFFSET(SYSUTCDATETIME(), 0)
          ) THEN 1 ELSE 0 END AS [Value]
          """;
}
