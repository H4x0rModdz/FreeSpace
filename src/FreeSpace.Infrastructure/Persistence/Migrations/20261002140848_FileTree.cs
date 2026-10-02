using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FreeSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FileTree : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "stored_objects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    mime_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stored_objects", x => x.id);
                    table.ForeignKey(
                        name: "fk_stored_objects_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "nodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    object_id = table.Column<Guid>(type: "uuid", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    mime_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trashed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    trashed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trash_root_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nodes", x => x.id);
                    table.ForeignKey(
                        name: "fk_nodes_nodes_parent_id",
                        column: x => x.parent_id,
                        principalTable: "nodes",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_nodes_stored_objects_object_id",
                        column: x => x.object_id,
                        principalTable: "stored_objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_nodes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_nodes_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "replicas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_object_id = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delete_attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_replicas", x => x.id);
                    table.ForeignKey(
                        name: "fk_replicas_storage_accounts_storage_account_id",
                        column: x => x.storage_account_id,
                        principalTable: "storage_accounts",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_replicas_stored_objects_object_id",
                        column: x => x.object_id,
                        principalTable: "stored_objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_replicas_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_nodes_created_by_user_id",
                table: "nodes",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_nodes_name",
                table: "nodes",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_nodes_object_id",
                table: "nodes",
                column: "object_id");

            migrationBuilder.CreateIndex(
                name: "ix_nodes_parent_id",
                table: "nodes",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_nodes_tenant_id_parent_id_normalized_name",
                table: "nodes",
                columns: new[] { "tenant_id", "parent_id", "normalized_name" },
                unique: true,
                filter: "trashed_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_nodes_tenant_id_trash_root_id",
                table: "nodes",
                columns: new[] { "tenant_id", "trash_root_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replicas_object_id",
                table: "replicas",
                column: "object_id");

            migrationBuilder.CreateIndex(
                name: "ix_replicas_status",
                table: "replicas",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_replicas_storage_account_id",
                table: "replicas",
                column: "storage_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_replicas_tenant_id",
                table: "replicas",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_stored_objects_status",
                table: "stored_objects",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_stored_objects_tenant_id",
                table: "stored_objects",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nodes");

            migrationBuilder.DropTable(
                name: "replicas");

            migrationBuilder.DropTable(
                name: "stored_objects");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
