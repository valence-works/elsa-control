using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.Provisioning.Persistence.SqlServerMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddProvisioningLeaseAndCancel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelRequested",
                schema: "provisioning",
                table: "Operations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "FencingToken",
                schema: "provisioning",
                table: "Operations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                schema: "provisioning",
                table: "Operations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                schema: "provisioning",
                table: "Operations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StateLockMayBeHeld",
                schema: "provisioning",
                table: "Operations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "FencingToken",
                schema: "provisioning",
                table: "Locks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                schema: "provisioning",
                table: "Locks",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "Owner",
                schema: "provisioning",
                table: "Locks",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_StateLockMayBeHeld",
                schema: "provisioning",
                table: "Operations",
                column: "StateLockMayBeHeld");

            migrationBuilder.CreateIndex(
                name: "IX_Locks_LeaseExpiresAt",
                schema: "provisioning",
                table: "Locks",
                column: "LeaseExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Operations_StateLockMayBeHeld",
                schema: "provisioning",
                table: "Operations");

            migrationBuilder.DropIndex(
                name: "IX_Locks_LeaseExpiresAt",
                schema: "provisioning",
                table: "Locks");

            migrationBuilder.DropColumn(
                name: "CancelRequested",
                schema: "provisioning",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "FencingToken",
                schema: "provisioning",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                schema: "provisioning",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                schema: "provisioning",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "StateLockMayBeHeld",
                schema: "provisioning",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "FencingToken",
                schema: "provisioning",
                table: "Locks");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                schema: "provisioning",
                table: "Locks");

            migrationBuilder.DropColumn(
                name: "Owner",
                schema: "provisioning",
                table: "Locks");
        }
    }
}
