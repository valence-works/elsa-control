using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.Provisioning.Persistence.SqliteMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddProvisioningLeaseAndCancel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelRequested",
                table: "Operations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "FencingToken",
                table: "Operations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "Operations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "Operations",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StateLockMayBeHeld",
                table: "Operations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "FencingToken",
                table: "Locks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "Locks",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "Owner",
                table: "Locks",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_StateLockMayBeHeld",
                table: "Operations",
                column: "StateLockMayBeHeld");

            migrationBuilder.CreateIndex(
                name: "IX_Locks_LeaseExpiresAt",
                table: "Locks",
                column: "LeaseExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Operations_StateLockMayBeHeld",
                table: "Operations");

            migrationBuilder.DropIndex(
                name: "IX_Locks_LeaseExpiresAt",
                table: "Locks");

            migrationBuilder.DropColumn(
                name: "CancelRequested",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "FencingToken",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "StateLockMayBeHeld",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "FencingToken",
                table: "Locks");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "Locks");

            migrationBuilder.DropColumn(
                name: "Owner",
                table: "Locks");
        }
    }
}
