using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FreeSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Uploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "round_robin_cursor",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "upload_routing_policy",
                table: "tenants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "reserved_bytes",
                table: "storage_accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateOnly>(
                name: "upload_day",
                table: "storage_accounts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "uploaded_today_bytes",
                table: "storage_accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "upload_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    chunk_size = table.Column<long>(type: "bigint", nullable: false),
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    provider_state_ciphertext = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    node_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_upload_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_upload_sessions_storage_accounts_storage_account_id",
                        column: x => x.storage_account_id,
                        principalTable: "storage_accounts",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_upload_sessions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_upload_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_status_expires_at",
                table: "upload_sessions",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_storage_account_id",
                table: "upload_sessions",
                column: "storage_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_tenant_id",
                table: "upload_sessions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_user_id",
                table: "upload_sessions",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "round_robin_cursor",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "upload_routing_policy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "reserved_bytes",
                table: "storage_accounts");

            migrationBuilder.DropColumn(
                name: "upload_day",
                table: "storage_accounts");

            migrationBuilder.DropColumn(
                name: "uploaded_today_bytes",
                table: "storage_accounts");
        }
    }
}
