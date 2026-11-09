using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nurol.Envanter.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class HatVeIlisik : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "offboarding_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    no = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    last_working_day = table.Column<DateOnly>(type: "date", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    opened_by = table.Column<string>(type: "text", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_by = table.Column<string>(type: "text", nullable: true),
                    cancel_reason = table.Column<string>(type: "text", nullable: true),
                    certificate_key = table.Column<string>(type: "text", nullable: true),
                    certificate_sha256 = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_offboarding_cases", x => x.id);
                    table.ForeignKey(
                        name: "fk_offboarding_cases_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "phone_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "text", nullable: false),
                    @operator = table.Column<string>(name: "operator", type: "text", nullable: false),
                    line_type = table.Column<string>(type: "text", nullable: false),
                    tariff = table.Column<string>(type: "text", nullable: true),
                    monthly_fee = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    iccid = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cost_center = table.Column<string>(type: "text", nullable: true),
                    contract_end = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_phone_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_phone_lines_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_phone_lines_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_phone_lines_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "offboarding_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    done = table.Column<bool>(type: "boolean", nullable: false),
                    done_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    done_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_offboarding_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_offboarding_tasks_offboarding_cases_case_id",
                        column: x => x.case_id,
                        principalTable: "offboarding_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "phone_line_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    user_name = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_phone_line_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_phone_line_events_phone_lines_line_id",
                        column: x => x.line_id,
                        principalTable: "phone_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_offboarding_cases_employee_id",
                table: "offboarding_cases",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_offboarding_cases_no",
                table: "offboarding_cases",
                column: "no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_offboarding_cases_open_employee",
                table: "offboarding_cases",
                column: "employee_id",
                unique: true,
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "ix_offboarding_tasks_case_id",
                table: "offboarding_tasks",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_phone_line_events_line_id_at",
                table: "phone_line_events",
                columns: new[] { "line_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_phone_lines_company_id",
                table: "phone_lines",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_phone_lines_employee_id",
                table: "phone_lines",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "ix_phone_lines_iccid",
                table: "phone_lines",
                column: "iccid",
                unique: true,
                filter: "iccid IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_phone_lines_item_id",
                table: "phone_lines",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_phone_lines_number",
                table: "phone_lines",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "offboarding_tasks");

            migrationBuilder.DropTable(
                name: "phone_line_events");

            migrationBuilder.DropTable(
                name: "offboarding_cases");

            migrationBuilder.DropTable(
                name: "phone_lines");
        }
    }
}
