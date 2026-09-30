using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

/// <summary>
/// Records whether a managed engine's admitted image supports the exact Studio grant set, bound to the
/// current deployment id. The columns are additive and outside every operation, plan and reconciliation
/// hash, so a Control build that predates them still restores, deletes and recovers the rows this build writes.
/// </summary>
[DbContext(typeof(CatalogDbContext))]
[Migration("20260928040001_AddManagedStudioGrantCapability")]
public sealed class AddManagedStudioGrantCapability : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Staging may already have applied an earlier version of this additive schema.
        // Add each missing column independently and validate existing columns without rewriting values.
        migrationBuilder.Sql(AddBitColumnIfMissingOrValidate(
            table: "ElsaInstances",
            column: "CurrentDeploymentStudioGrants",
            constraint: "DF_ElsaInstances_CurrentDeploymentStudioGrants"));

        migrationBuilder.Sql(AddNullableStringColumnIfMissingOrValidate(
            table: "ElsaInstances",
            column: "CurrentDeploymentStudioGrantsDeploymentId"));

        migrationBuilder.Sql(AddBitColumnIfMissingOrValidate(
            table: "AzureProviderOperations",
            column: "ManagedHandoffStudioGrants",
            constraint: "DF_AzureProviderOperations_ManagedHandoffStudioGrants"));
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // These additive columns may have been deployed before this migration was recorded.
        // Keeping them makes rollback safe for old binaries and preserves their data; a later Up
        // validates and reuses the same columns.
    }

    private static string AddBitColumnIfMissingOrValidate(string table, string column, string constraint)
    {
        var tableName = $"[dbo].[{table}]";

        return $"""
            IF COL_LENGTH(N'dbo.{table}', N'{column}') IS NULL
            BEGIN
                ALTER TABLE {tableName}
                    ADD [{column}] bit NOT NULL CONSTRAINT [{constraint}] DEFAULT (0);
            END
            ELSE IF NOT EXISTS
            (
                SELECT 1
                FROM sys.columns AS c
                INNER JOIN sys.tables AS t ON t.object_id = c.object_id
                INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
                INNER JOIN sys.default_constraints AS dc
                    ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
                WHERE s.name = N'dbo'
                    AND t.name = N'{table}'
                    AND c.name = N'{column}'
                    AND ty.name = N'bit'
                    AND ty.is_user_defined = 0
                    AND c.is_nullable = 0
                    AND c.is_computed = 0
                    AND UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(dc.definition,
                        '(', ''), ')', ''), '[', ''), ']', ''), ' ', ''), CHAR(9), ''), ',', ''))
                        IN (N'0', N'CONVERTBIT0', N'CAST0ASBIT')
            )
            BEGIN
                THROW 51000, 'Existing {table}.{column} must be a non-nullable bit with a zero default.', 1;
            END;
            """;
    }

    private static string AddNullableStringColumnIfMissingOrValidate(string table, string column)
    {
        return $"""
            IF COL_LENGTH(N'dbo.{table}', N'{column}') IS NULL
            BEGIN
                ALTER TABLE [dbo].[{table}]
                    ADD [{column}] nvarchar(128) NULL;
            END
            ELSE IF NOT EXISTS
            (
                SELECT 1
                FROM sys.columns AS c
                INNER JOIN sys.tables AS t ON t.object_id = c.object_id
                INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
                WHERE s.name = N'dbo'
                    AND t.name = N'{table}'
                    AND c.name = N'{column}'
                    AND ty.name = N'nvarchar'
                    AND ty.is_user_defined = 0
                    AND c.max_length = 256
                    AND c.is_nullable = 1
                    AND c.is_computed = 0
                    AND c.default_object_id = 0
            )
            BEGIN
                THROW 51000, 'Existing {table}.{column} must be a nullable nvarchar(128).', 1;
            END;
            """;
    }
}
