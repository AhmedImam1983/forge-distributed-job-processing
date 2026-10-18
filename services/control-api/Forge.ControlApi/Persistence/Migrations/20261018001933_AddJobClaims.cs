using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forge.ControlApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "last_fencing_token",
                table: "jobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "job_attempts",
                columns: table => new
                {
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    worker_instance_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    fencing_token = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Running"),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    failure_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_attempts", x => x.attempt_id);
                    table.CheckConstraint("ck_job_attempts_attempt_number", "attempt_number > 0");
                    table.CheckConstraint("ck_job_attempts_fencing_token", "fencing_token > 0");
                    table.CheckConstraint("ck_job_attempts_lease_after_claim", "lease_expires_at > claimed_at");
                    table.CheckConstraint("ck_job_attempts_status", "status IN (\n    'Running',\n    'Succeeded',\n    'Failed',\n    'Cancelled',\n    'LeaseExpired'\n)");
                    table.CheckConstraint("ck_job_attempts_worker_instance_id", "length(btrim(worker_instance_id)) > 0");
                    table.ForeignKey(
                        name: "fk_job_attempts_jobs",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "job_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "claim_requests",
                columns: table => new
                {
                    claim_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    worker_instance_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fencing_token = table.Column<long>(type: "bigint", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_requests", x => x.claim_request_id);
                    table.CheckConstraint("ck_claim_requests_fencing_token", "fencing_token IS NULL OR fencing_token > 0");
                    table.CheckConstraint("ck_claim_requests_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_claim_requests_outcome", "outcome IN (\n    'Granted',\n    'AlreadyOwnedBySameRequest',\n    'AlreadyClaimed',\n    'NotEligible',\n    'NotFound',\n    'ExpiredOrLost'\n)");
                    table.CheckConstraint("ck_claim_requests_worker_instance_id", "length(btrim(worker_instance_id)) > 0");
                    table.ForeignKey(
                        name: "fk_claim_requests_job_attempts",
                        column: x => x.attempt_id,
                        principalTable: "job_attempts",
                        principalColumn: "attempt_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_last_fencing_token",
                table: "jobs",
                sql: "last_fencing_token >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_claim_requests_attempt_id",
                table: "claim_requests",
                column: "attempt_id");

            migrationBuilder.CreateIndex(
                name: "ix_claim_requests_job_decided_at",
                table: "claim_requests",
                columns: new[] { "job_id", "decided_at" });

            migrationBuilder.CreateIndex(
                name: "ix_job_attempts_status_lease_expires_at",
                table: "job_attempts",
                columns: new[] { "status", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ux_job_attempts_job_attempt_number",
                table: "job_attempts",
                columns: new[] { "job_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_job_attempts_job_fencing_token",
                table: "job_attempts",
                columns: new[] { "job_id", "fencing_token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_job_attempts_one_running_per_job",
                table: "job_attempts",
                column: "job_id",
                unique: true,
                filter: "status = 'Running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "claim_requests");

            migrationBuilder.DropTable(
                name: "job_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_last_fencing_token",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "last_fencing_token",
                table: "jobs");
        }
    }
}
