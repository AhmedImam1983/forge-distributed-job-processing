using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forge.ControlApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseRenewalRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lease_renewal_requests",
                columns: table => new
                {
                    renew_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    worker_instance_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    fencing_token = table.Column<long>(type: "bigint", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lease_renewal_requests", x => x.renew_request_id);
                    table.CheckConstraint("ck_lease_renewal_requests_expiry", "(\n    outcome = 'Renewed'\n    AND lease_expires_at IS NOT NULL\n)\nOR\n(\n    outcome <> 'Renewed'\n    AND lease_expires_at IS NULL\n)");
                    table.CheckConstraint("ck_lease_renewal_requests_fencing_token", "fencing_token > 0");
                    table.CheckConstraint("ck_lease_renewal_requests_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_lease_renewal_requests_outcome", "outcome IN (\n    'Renewed',\n    'LeaseExpiredOrLost',\n    'CancellationPending',\n    'NotOwnerOrSuperseded',\n    'NotFound'\n)");
                    table.CheckConstraint("ck_lease_renewal_requests_worker", "length(btrim(worker_instance_id)) > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_lease_renewal_requests_attempt_decided_at",
                table: "lease_renewal_requests",
                columns: new[] { "attempt_id", "decided_at" });

            migrationBuilder.CreateIndex(
                name: "ix_lease_renewal_requests_job_decided_at",
                table: "lease_renewal_requests",
                columns: new[] { "job_id", "decided_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lease_renewal_requests");
        }
    }
}
