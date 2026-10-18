using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forge.ControlApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "result_descriptor_json",
                table: "jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "completion_requests",
                columns: table => new
                {
                    completion_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    worker_instance_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    fencing_token = table.Column<long>(type: "bigint", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    request_payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_completion_requests", x => x.completion_request_id);
                    table.CheckConstraint("ck_completion_requests_fencing_token", "fencing_token > 0");
                    table.CheckConstraint("ck_completion_requests_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_completion_requests_outcome", "outcome IN (\n    'AcceptedSucceeded',\n    'AcceptedRetryScheduled',\n    'AcceptedFailed',\n    'AcceptedCancelled',\n    'LeaseExpiredOrLost',\n    'NotOwnerOrSuperseded',\n    'CancellationPending',\n    'AttemptAlreadyFinalized',\n    'NotFound',\n    'IdempotencyKeyConflict',\n    'InvalidOutcomeForState'\n)");
                    table.CheckConstraint("ck_completion_requests_payload", "jsonb_typeof(request_payload_json) = 'object'");
                    table.CheckConstraint("ck_completion_requests_worker", "length(btrim(worker_instance_id)) > 0");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_result_descriptor_object",
                table: "jobs",
                sql: "result_descriptor_json IS NULL OR jsonb_typeof(result_descriptor_json) = 'object'");

            migrationBuilder.CreateIndex(
                name: "ix_completion_requests_attempt_decided_at",
                table: "completion_requests",
                columns: new[] { "attempt_id", "decided_at" });

            migrationBuilder.CreateIndex(
                name: "ix_completion_requests_job_decided_at",
                table: "completion_requests",
                columns: new[] { "job_id", "decided_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "completion_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_result_descriptor_object",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "result_descriptor_json",
                table: "jobs");
        }
    }
}
