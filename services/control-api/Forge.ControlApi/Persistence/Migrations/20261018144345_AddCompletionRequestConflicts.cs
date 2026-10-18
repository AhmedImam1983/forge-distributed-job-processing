using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forge.ControlApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompletionRequestConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "completion_request_conflicts",
                columns: table => new
                {
                    completion_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    request_payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_completion_request_conflicts", x => new { x.completion_request_id, x.request_fingerprint });
                    table.CheckConstraint("ck_completion_request_conflicts_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_completion_request_conflicts_outcome", "outcome = 'IdempotencyKeyConflict'");
                    table.CheckConstraint("ck_completion_request_conflicts_payload", "jsonb_typeof(request_payload_json) = 'object'");
                    table.ForeignKey(
                        name: "FK_completion_request_conflicts_completion_requests_completion~",
                        column: x => x.completion_request_id,
                        principalTable: "completion_requests",
                        principalColumn: "completion_request_id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "completion_request_conflicts");
        }
    }
}
