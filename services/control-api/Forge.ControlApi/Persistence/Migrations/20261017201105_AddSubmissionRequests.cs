using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forge.ControlApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubmissionRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "submission_requests",
                columns: table => new
                {
                    client_scope_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    submission_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    response_status_code = table.Column<int>(type: "integer", nullable: false),
                    response_body_json = table.Column<string>(type: "jsonb", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_submission_requests", x => new { x.client_scope_id, x.submission_request_id });
                    table.CheckConstraint("ck_submission_requests_client_scope", "length(btrim(client_scope_id)) > 0");
                    table.CheckConstraint("ck_submission_requests_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_submission_requests_outcome", "(\n    outcome = 'Accepted'\n    AND job_id IS NOT NULL\n    AND response_status_code BETWEEN 200 AND 299\n)\nOR\n(\n    outcome = 'Rejected'\n    AND job_id IS NULL\n    AND response_status_code BETWEEN 400 AND 499\n)");
                    table.ForeignKey(
                        name: "fk_submission_requests_jobs",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "job_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_submission_requests_job_id",
                table: "submission_requests",
                column: "job_id",
                unique: true,
                filter: "job_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "submission_requests");
        }
    }
}
