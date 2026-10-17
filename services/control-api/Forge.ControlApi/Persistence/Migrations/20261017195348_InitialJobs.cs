using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forge.ControlApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Queued"),
                    input_payload_json = table.Column<string>(type: "jsonb", nullable: true),
                    input_reference = table.Column<string>(type: "text", nullable: true),
                    input_revision = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    next_eligible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.job_id);
                    table.CheckConstraint("ck_jobs_input_representation", "(\n    input_payload_json IS NOT NULL\n    AND input_reference IS NULL\n    AND input_revision IS NULL\n)\nOR\n(\n    input_payload_json IS NULL\n    AND input_reference IS NOT NULL\n    AND input_revision IS NOT NULL\n)");
                    table.CheckConstraint("ck_jobs_status", "status IN (\n    'Queued',\n    'Running',\n    'CancelRequested',\n    'Succeeded',\n    'Failed',\n    'Cancelled'\n)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_status_next_eligible_at",
                table: "jobs",
                columns: new[] { "status", "next_eligible_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jobs");
        }
    }
}
