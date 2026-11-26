using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.Provisioning.Persistence.SqlServerMigrations.Migrations
{
    /// <inheritdoc />
    public partial class InitialProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "provisioning");

            migrationBuilder.CreateTable(
                name: "Events",
                schema: "provisioning",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ResourceKind = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Locks",
                schema: "provisioning",
                columns: table => new
                {
                    LockKey = table.Column<string>(type: "nvarchar(192)", maxLength: 192, nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locks", x => x.LockKey);
                });

            migrationBuilder.CreateTable(
                name: "Operations",
                schema: "provisioning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Command = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProviderId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StackKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StackName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LockKey = table.Column<string>(type: "nvarchar(192)", maxLength: 192, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RequestJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_OperationId",
                schema: "provisioning",
                table: "Events",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_IdempotencyKey",
                schema: "provisioning",
                table: "Operations",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Operations_LockKey_State",
                schema: "provisioning",
                table: "Operations",
                columns: new[] { "LockKey", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_State_AcceptedAt",
                schema: "provisioning",
                table: "Operations",
                columns: new[] { "State", "AcceptedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events",
                schema: "provisioning");

            migrationBuilder.DropTable(
                name: "Locks",
                schema: "provisioning");

            migrationBuilder.DropTable(
                name: "Operations",
                schema: "provisioning");
        }
    }
}
