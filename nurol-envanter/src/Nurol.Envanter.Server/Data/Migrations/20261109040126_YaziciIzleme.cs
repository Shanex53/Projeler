using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nurol.Envanter.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class YaziciIzleme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "printers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    snmp_version = table.Column<string>(type: "text", nullable: false),
                    community = table.Column<string>(type: "text", nullable: false),
                    location = table.Column<string>(type: "text", nullable: true),
                    low_threshold = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_polled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    online = table.Column<bool>(type: "boolean", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    sys_name = table.Column<string>(type: "text", nullable: true),
                    serial_number = table.Column<string>(type: "text", nullable: true),
                    page_count = table.Column<long>(type: "bigint", nullable: true),
                    supplies = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_printers", x => x.id);
                    table.ForeignKey(
                        name: "fk_printers_warehouses_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "printer_counters",
                columns: table => new
                {
                    printer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    first_count = table.Column<long>(type: "bigint", nullable: false),
                    last_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_printer_counters", x => new { x.printer_id, x.day });
                    table.ForeignKey(
                        name: "fk_printer_counters_printers_printer_id",
                        column: x => x.printer_id,
                        principalTable: "printers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "printer_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    printer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    user_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_printer_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_printer_events_printers_printer_id",
                        column: x => x.printer_id,
                        principalTable: "printers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "printer_supply_maps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    printer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supply_key = table.Column<string>(type: "text", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_printer_supply_maps", x => x.id);
                    table.ForeignKey(
                        name: "fk_printer_supply_maps_printers_printer_id",
                        column: x => x.printer_id,
                        principalTable: "printers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_printer_supply_maps_stock_items_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "printer_supply_states",
                columns: table => new
                {
                    printer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supply_key = table.Column<string>(type: "text", nullable: false),
                    last_percent = table.Column<int>(type: "integer", nullable: true),
                    low_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pending_replacement = table.Column<bool>(type: "boolean", nullable: false),
                    last_replaced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_printer_supply_states", x => new { x.printer_id, x.supply_key });
                    table.ForeignKey(
                        name: "fk_printer_supply_states_printers_printer_id",
                        column: x => x.printer_id,
                        principalTable: "printers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_printer_events_printer_id_at",
                table: "printer_events",
                columns: new[] { "printer_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_printer_supply_maps_printer_id_supply_key",
                table: "printer_supply_maps",
                columns: new[] { "printer_id", "supply_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_printer_supply_maps_stock_item_id",
                table: "printer_supply_maps",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_printers_host_port",
                table: "printers",
                columns: new[] { "host", "port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_printers_warehouse_id",
                table: "printers",
                column: "warehouse_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "printer_counters");

            migrationBuilder.DropTable(
                name: "printer_events");

            migrationBuilder.DropTable(
                name: "printer_supply_maps");

            migrationBuilder.DropTable(
                name: "printer_supply_states");

            migrationBuilder.DropTable(
                name: "printers");
        }
    }
}
