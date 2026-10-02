using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FreeSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StorageAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oauth_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_states", x => x.id);
                    table.ForeignKey(
                        name: "fk_oauth_states_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_oauth_states_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "storage_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    external_account_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    config = table.Column<string>(type: "jsonb", nullable: true),
                    secret_ciphertext = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    used_bytes = table.Column<long>(type: "bigint", nullable: false),
                    last_quota_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_storage_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_storage_accounts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_storage_accounts_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oauth_states_state_hash",
                table: "oauth_states",
                column: "state_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_states_tenant_id",
                table: "oauth_states",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_states_user_id",
                table: "oauth_states",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_storage_accounts_created_by_user_id",
                table: "storage_accounts",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_storage_accounts_status_last_quota_sync_at",
                table: "storage_accounts",
                columns: new[] { "status", "last_quota_sync_at" });

            migrationBuilder.CreateIndex(
                name: "ix_storage_accounts_tenant_id_provider_external_account_id",
                table: "storage_accounts",
                columns: new[] { "tenant_id", "provider", "external_account_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oauth_states");

            migrationBuilder.DropTable(
                name: "storage_accounts");
        }
    }
}
