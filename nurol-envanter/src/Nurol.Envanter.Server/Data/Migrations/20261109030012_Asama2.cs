using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nurol.Envanter.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Asama2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "directory_company_text",
                table: "employees",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "directory_dn",
                table: "employees",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "directory_id",
                table: "employees",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "directory_synced_at",
                table: "employees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "directory_user_name",
                table: "employees",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "directory_id",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "employee_id",
                table: "AspNetUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "directory_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    attribute = table.Column<string>(type: "text", nullable: false),
                    match_type = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_directory_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_directory_rules_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "directory_sync_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    user_name = table.Column<string>(type: "text", nullable: false),
                    found = table.Column<int>(type: "integer", nullable: false),
                    created = table.Column<int>(type: "integer", nullable: false),
                    updated = table.Column<int>(type: "integer", nullable: false),
                    deactivated = table.Column<int>(type: "integer", nullable: false),
                    unmatched = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "text", nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_directory_sync_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "saved_views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    page = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    state = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_views", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    no = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_by = table.Column<string>(type: "text", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    received_by = table.Column<string>(type: "text", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transfers", x => x.id);
                    table.CheckConstraint("ck_transfer_distinct_warehouses", "source_warehouse_id <> target_warehouse_id");
                    table.ForeignKey(
                        name: "fk_transfers_warehouses_source_warehouse_id",
                        column: x => x.source_warehouse_id,
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transfers_warehouses_target_warehouse_id",
                        column: x => x.target_warehouse_id,
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transfer_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transfer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transfer_lines", x => x.id);
                    table.CheckConstraint("ck_transfer_line_kind", "(item_id IS NOT NULL AND stock_item_id IS NULL AND quantity IS NULL) OR (item_id IS NULL AND stock_item_id IS NOT NULL AND quantity > 0)");
                    table.ForeignKey(
                        name: "fk_transfer_lines_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transfer_lines_stock_items_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transfer_lines_transfers_transfer_id",
                        column: x => x.transfer_id,
                        principalTable: "transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_employees_directory_id",
                table: "employees",
                column: "directory_id",
                unique: true,
                filter: "directory_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_users_directory_id",
                table: "AspNetUsers",
                column: "directory_id",
                unique: true,
                filter: "directory_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_directory_rules_company_id",
                table: "directory_rules",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_directory_sync_runs_started_at",
                table: "directory_sync_runs",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_saved_views_user_id_page_name",
                table: "saved_views",
                columns: new[] { "user_id", "page", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transfer_lines_item_id",
                table: "transfer_lines",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_lines_stock_item_id",
                table: "transfer_lines",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_transfer_lines_transfer_id",
                table: "transfer_lines",
                column: "transfer_id");

            migrationBuilder.CreateIndex(
                name: "ix_transfers_no",
                table: "transfers",
                column: "no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transfers_source_warehouse_id",
                table: "transfers",
                column: "source_warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ix_transfers_status_target_warehouse_id",
                table: "transfers",
                columns: new[] { "status", "target_warehouse_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transfers_target_warehouse_id",
                table: "transfers",
                column: "target_warehouse_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "directory_rules");

            migrationBuilder.DropTable(
                name: "directory_sync_runs");

            migrationBuilder.DropTable(
                name: "saved_views");

            migrationBuilder.DropTable(
                name: "transfer_lines");

            migrationBuilder.DropTable(
                name: "transfers");

            migrationBuilder.DropIndex(
                name: "ix_employees_directory_id",
                table: "employees");

            migrationBuilder.DropIndex(
                name: "ix_asp_net_users_directory_id",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "directory_company_text",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "directory_dn",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "directory_id",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "directory_synced_at",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "directory_user_name",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "directory_id",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "employee_id",
                table: "AspNetUsers");
        }
    }
}
