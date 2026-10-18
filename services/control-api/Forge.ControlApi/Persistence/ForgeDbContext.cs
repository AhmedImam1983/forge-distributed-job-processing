using Forge.ControlApi.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Forge.ControlApi.Persistence;

public sealed class ForgeDbContext : DbContext
{
    public ForgeDbContext(
        DbContextOptions<ForgeDbContext> options)
        : base(options)
    {
    }

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<SubmissionRequest> SubmissionRequests
        => Set<SubmissionRequest>();

    public DbSet<OutboxMessage> OutboxMessages
        => Set<OutboxMessage>();

    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();

    public DbSet<ClaimRequest> ClaimRequests => Set<ClaimRequest>();

    public DbSet<LeaseRenewalRequest> LeaseRenewalRequests => Set<LeaseRenewalRequest>();

    public DbSet<CompletionRequest> CompletionRequests => Set<CompletionRequest>();

    public DbSet<CompletionRequestConflict> CompletionRequestConflicts
        => Set<CompletionRequestConflict>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureJob(modelBuilder);
        ConfigureSubmissionRequest(modelBuilder);
        ConfigureOutbox(modelBuilder);
        ConfigureJobAttempt(modelBuilder);
        ConfigureClaimRequest(modelBuilder);
        ConfigureLeaseRenewalRequest(modelBuilder);
        ConfigureCompletionRequest(modelBuilder);
        ConfigureCompletionRequestConflict(modelBuilder);
    }

    private static void ConfigureJob(
        ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Entity<Job>();

        job.ToTable("jobs", table =>
        {
            table.HasCheckConstraint(
                "ck_jobs_status",
                """
                status IN (
                    'Queued',
                    'Running',
                    'CancelRequested',
                    'Succeeded',
                    'Failed',
                    'Cancelled'
                )
                """);

            table.HasCheckConstraint(
                "ck_jobs_last_fencing_token",
                "last_fencing_token >= 0");

            table.HasCheckConstraint(
                "ck_jobs_input_representation",
                """
                (
                    input_payload_json IS NOT NULL
                    AND input_reference IS NULL
                    AND input_revision IS NULL
                )
                OR
                (
                    input_payload_json IS NULL
                    AND input_reference IS NOT NULL
                    AND input_revision IS NOT NULL
                )
                """);
            table.HasCheckConstraint(
                "ck_jobs_result_descriptor_object",
                "result_descriptor_json IS NULL OR jsonb_typeof(result_descriptor_json) = 'object'");
        });

        job.HasKey(x => x.JobId);

        job.Property(x => x.JobId)
            .HasColumnName("job_id")
            .ValueGeneratedNever();

        job.Property(x => x.JobType)
            .HasColumnName("job_type")
            .HasMaxLength(100)
            .IsRequired();

        job.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(JobStatus.Queued)
            .IsRequired();

        job.Property(x => x.InputPayloadJson)
            .HasColumnName("input_payload_json")
            .HasColumnType("jsonb");

        job.Property(x => x.InputReference)
            .HasColumnName("input_reference");

        job.Property(x => x.InputRevision)
            .HasColumnName("input_revision");

        job.Property(x => x.ResultDescriptorJson)
            .HasColumnName("result_descriptor_json")
            .HasColumnType("jsonb");

        job.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd();

        job.Property(x => x.NextEligibleAt)
            .HasColumnName("next_eligible_at")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd();

        job.Property(x => x.LastFencingToken)
            .HasColumnName("last_fencing_token")
            .HasDefaultValue(0L)
            .IsRequired();

        job.HasIndex(x => new
            {
                x.Status,
                x.NextEligibleAt
            })
            .HasDatabaseName(
                "ix_jobs_status_next_eligible_at");
    }

    private static void ConfigureSubmissionRequest(
        ModelBuilder modelBuilder)
    {
        var request =
            modelBuilder.Entity<SubmissionRequest>();

        request.ToTable("submission_requests", table =>
        {
            table.HasCheckConstraint(
                "ck_submission_requests_client_scope",
                "length(btrim(client_scope_id)) > 0");

            table.HasCheckConstraint(
                "ck_submission_requests_fingerprint",
                """
                request_fingerprint ~ '^[0-9a-f]{64}$'
                """);

            table.HasCheckConstraint(
                "ck_submission_requests_outcome",
                """
                (
                    outcome = 'Accepted'
                    AND job_id IS NOT NULL
                    AND response_status_code BETWEEN 200 AND 299
                )
                OR
                (
                    outcome = 'Rejected'
                    AND job_id IS NULL
                    AND response_status_code BETWEEN 400 AND 499
                )
                """);
        });

        request.HasKey(x => new
            {
                x.ClientScopeId,
                x.SubmissionRequestId
            })
            .HasName("pk_submission_requests");

        request.Property(x => x.ClientScopeId)
            .HasColumnName("client_scope_id")
            .HasMaxLength(512)
            .IsRequired();

        request.Property(x => x.SubmissionRequestId)
            .HasColumnName("submission_request_id")
            .ValueGeneratedNever();

        request.Property(x => x.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasMaxLength(64)
            .IsRequired();

        request.Property(x => x.Outcome)
            .HasColumnName("outcome")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        request.Property(x => x.ResponseStatusCode)
            .HasColumnName("response_status_code")
            .IsRequired();

        request.Property(x => x.ResponseBodyJson)
            .HasColumnName("response_body_json")
            .HasColumnType("jsonb")
            .IsRequired();

        request.Property(x => x.JobId)
            .HasColumnName("job_id");

        request.Property(x => x.DecidedAt)
            .HasColumnName("decided_at")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd();

        request.HasOne<Job>()
            .WithMany()
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(
                "fk_submission_requests_jobs");

        request.HasIndex(x => x.JobId)
            .IsUnique()
            .HasFilter("job_id IS NOT NULL")
            .HasDatabaseName(
                "ux_submission_requests_job_id");
    }

    private static void ConfigureOutbox(
    ModelBuilder modelBuilder)
{
    var message = modelBuilder.Entity<OutboxMessage>();

    message.ToTable("outbox_messages", table =>
    {
        table.HasCheckConstraint(
            "ck_outbox_messages_message_type",
            "length(btrim(message_type)) > 0");

        table.HasCheckConstraint(
            "ck_outbox_messages_attempt_count",
            "attempt_count >= 0");

        table.HasCheckConstraint(
            "ck_outbox_messages_payload_object",
            "jsonb_typeof(payload_json) = 'object'");

        table.HasCheckConstraint(
            "ck_outbox_messages_lease_pair",
            """
            (
                lease_owner IS NULL
                AND lease_expires_at IS NULL
            )
            OR
            (
                published_at IS NULL
                AND lease_owner IS NOT NULL
                AND lease_expires_at IS NOT NULL
            )
            """);

        table.HasCheckConstraint(
            "ck_outbox_messages_lease_owner",
            """
            lease_owner IS NULL
            OR length(btrim(lease_owner)) > 0
            """);
    });

    message.HasKey(x => x.OutboxMessageId);

    message.Property(x => x.OutboxMessageId)
        .HasColumnName("outbox_message_id")
        .ValueGeneratedNever();

    message.Property(x => x.JobId)
        .HasColumnName("job_id")
        .IsRequired();

    message.Property(x => x.MessageType)
        .HasColumnName("message_type")
        .HasMaxLength(100)
        .IsRequired();

    message.Property(x => x.PayloadJson)
        .HasColumnName("payload_json")
        .HasColumnType("jsonb")
        .IsRequired();

    message.Property(x => x.CreatedAt)
        .HasColumnName("created_at")
        .HasDefaultValueSql("CURRENT_TIMESTAMP")
        .ValueGeneratedOnAdd();

    message.Property(x => x.PublishedAt)
        .HasColumnName("published_at");

    message.Property(x => x.AttemptCount)
        .HasColumnName("attempt_count")
        .HasDefaultValue(0)
        .ValueGeneratedOnAdd();

    message.Property(x => x.NextAttemptAt)
        .HasColumnName("next_attempt_at")
        .HasDefaultValueSql("CURRENT_TIMESTAMP")
        .ValueGeneratedOnAdd();

    message.Property(x => x.LeaseOwner)
        .HasColumnName("lease_owner")
        .HasMaxLength(200);

    message.Property(x => x.LeaseExpiresAt)
        .HasColumnName("lease_expires_at");

    message.Property(x => x.LastError)
        .HasColumnName("last_error")
        .HasMaxLength(2000);

    message.HasOne<Job>()
        .WithMany()
        .HasForeignKey(x => x.JobId)
        .OnDelete(DeleteBehavior.Restrict)
        .HasConstraintName("fk_outbox_messages_jobs");

    message.HasIndex(x => x.JobId)
        .HasDatabaseName("ix_outbox_messages_job_id");

    message.HasIndex(x => new
        {
            x.NextAttemptAt,
            x.CreatedAt
        })
        .HasFilter("published_at IS NULL")
        .HasDatabaseName("ix_outbox_messages_dispatch");
}

    private static void ConfigureJobAttempt(ModelBuilder modelBuilder)
    {
        var attempt = modelBuilder.Entity<JobAttempt>();

        attempt.ToTable("job_attempts", table =>
        {
            table.HasCheckConstraint(
                "ck_job_attempts_status",
                """
                status IN (
                    'Running',
                    'Succeeded',
                    'Failed',
                    'Cancelled',
                    'LeaseExpired'
                )
                """);

            table.HasCheckConstraint(
                "ck_job_attempts_attempt_number",
                "attempt_number > 0");

            table.HasCheckConstraint(
                "ck_job_attempts_worker_instance_id",
                "length(btrim(worker_instance_id)) > 0");

            table.HasCheckConstraint(
                "ck_job_attempts_fencing_token",
                "fencing_token > 0");

            table.HasCheckConstraint(
                "ck_job_attempts_lease_after_claim",
                "lease_expires_at > claimed_at");
        });

        attempt.HasKey(x => x.AttemptId)
            .HasName("pk_job_attempts");

        attempt.Property(x => x.AttemptId)
            .HasColumnName("attempt_id")
            .ValueGeneratedNever();

        attempt.Property(x => x.JobId)
            .HasColumnName("job_id")
            .IsRequired();

        attempt.Property(x => x.AttemptNumber)
            .HasColumnName("attempt_number")
            .IsRequired();

        attempt.Property(x => x.WorkerInstanceId)
            .HasColumnName("worker_instance_id")
            .HasMaxLength(200)
            .IsRequired();

        attempt.Property(x => x.FencingToken)
            .HasColumnName("fencing_token")
            .IsRequired();

        attempt.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(JobAttemptStatus.Running)
            .IsRequired();

        attempt.Property(x => x.ClaimedAt)
            .HasColumnName("claimed_at")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd();

        attempt.Property(x => x.LeaseExpiresAt)
            .HasColumnName("lease_expires_at")
            .IsRequired();

        attempt.Property(x => x.CompletedAt)
            .HasColumnName("completed_at");

        attempt.Property(x => x.FailureCode)
            .HasColumnName("failure_code")
            .HasMaxLength(100);

        attempt.Property(x => x.FailureMessage)
            .HasColumnName("failure_message")
            .HasMaxLength(2000);

        attempt.HasOne<Job>()
            .WithMany()
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_job_attempts_jobs");

        attempt.HasIndex(x => new { x.JobId, x.AttemptNumber })
            .IsUnique()
            .HasDatabaseName("ux_job_attempts_job_attempt_number");

        attempt.HasIndex(x => new { x.JobId, x.FencingToken })
            .IsUnique()
            .HasDatabaseName("ux_job_attempts_job_fencing_token");

        // A job can have at most one attempt in Running state.
        attempt.HasIndex(x => x.JobId)
            .IsUnique()
            .HasFilter("status = 'Running'")
            .HasDatabaseName("ux_job_attempts_one_running_per_job");

        attempt.HasIndex(x => new { x.Status, x.LeaseExpiresAt })
            .HasDatabaseName("ix_job_attempts_status_lease_expires_at");
    }

    private static void ConfigureClaimRequest(ModelBuilder modelBuilder)
    {
        var request = modelBuilder.Entity<ClaimRequest>();

        request.ToTable("claim_requests", table =>
        {
            table.HasCheckConstraint(
                "ck_claim_requests_worker_instance_id",
                "length(btrim(worker_instance_id)) > 0");

            table.HasCheckConstraint(
                "ck_claim_requests_fingerprint",
                "request_fingerprint ~ '^[0-9a-f]{64}$'");

            table.HasCheckConstraint(
                "ck_claim_requests_outcome",
                """
                outcome IN (
                    'Granted',
                    'AlreadyOwnedBySameRequest',
                    'AlreadyClaimed',
                    'NotEligible',
                    'NotFound',
                    'ExpiredOrLost'
                )
                """);

            table.HasCheckConstraint(
                "ck_claim_requests_fencing_token",
                "fencing_token IS NULL OR fencing_token > 0");
        });

        request.HasKey(x => x.ClaimRequestId)
            .HasName("pk_claim_requests");

        request.Property(x => x.ClaimRequestId)
            .HasColumnName("claim_request_id")
            .ValueGeneratedNever();

        // JobId deliberately isn't configured as a foreign key:
        // a NotFound outcome must also be retained for idempotent retries.
        request.Property(x => x.JobId)
            .HasColumnName("job_id")
            .IsRequired();

        request.Property(x => x.WorkerInstanceId)
            .HasColumnName("worker_instance_id")
            .HasMaxLength(200)
            .IsRequired();

        request.Property(x => x.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasMaxLength(64)
            .IsRequired();

        request.Property(x => x.Outcome)
            .HasColumnName("outcome")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        request.Property(x => x.AttemptId)
            .HasColumnName("attempt_id");

        request.Property(x => x.FencingToken)
            .HasColumnName("fencing_token");

        request.Property(x => x.LeaseExpiresAt)
            .HasColumnName("lease_expires_at");

        request.Property(x => x.DecidedAt)
            .HasColumnName("decided_at")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd();

        request.HasOne<JobAttempt>()
            .WithMany()
            .HasForeignKey(x => x.AttemptId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_claim_requests_job_attempts");

        request.HasIndex(x => x.AttemptId)
            .HasDatabaseName("ix_claim_requests_attempt_id");

        request.HasIndex(x => new { x.JobId, x.DecidedAt })
            .HasDatabaseName("ix_claim_requests_job_decided_at");
    }


    private static void ConfigureLeaseRenewalRequest(
        ModelBuilder modelBuilder)
    {
        var request = modelBuilder.Entity<LeaseRenewalRequest>();

        request.ToTable("lease_renewal_requests", table =>
        {
            table.HasCheckConstraint(
                "ck_lease_renewal_requests_worker",
                "length(btrim(worker_instance_id)) > 0");

            table.HasCheckConstraint(
                "ck_lease_renewal_requests_fencing_token",
                "fencing_token > 0");

            table.HasCheckConstraint(
                "ck_lease_renewal_requests_fingerprint",
                "request_fingerprint ~ '^[0-9a-f]{64}$'");

            table.HasCheckConstraint(
                "ck_lease_renewal_requests_outcome",
                """
                outcome IN (
                    'Renewed',
                    'LeaseExpiredOrLost',
                    'CancellationPending',
                    'NotOwnerOrSuperseded',
                    'NotFound'
                )
                """);

            table.HasCheckConstraint(
                "ck_lease_renewal_requests_expiry",
                """
                (
                    outcome = 'Renewed'
                    AND lease_expires_at IS NOT NULL
                )
                OR
                (
                    outcome <> 'Renewed'
                    AND lease_expires_at IS NULL
                )
                """);
        });

        request.HasKey(x => x.RenewRequestId)
            .HasName("pk_lease_renewal_requests");

        request.Property(x => x.RenewRequestId)
            .HasColumnName("renew_request_id")
            .ValueGeneratedNever();

        // Intentionally no FK on JobId or AttemptId: rejected NotFound
        // and stale-attempt outcomes must also be stored durably.
        request.Property(x => x.JobId)
            .HasColumnName("job_id")
            .IsRequired();

        request.Property(x => x.AttemptId)
            .HasColumnName("attempt_id")
            .IsRequired();

        request.Property(x => x.WorkerInstanceId)
            .HasColumnName("worker_instance_id")
            .HasMaxLength(200)
            .IsRequired();

        request.Property(x => x.FencingToken)
            .HasColumnName("fencing_token")
            .IsRequired();

        request.Property(x => x.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasMaxLength(64)
            .IsRequired();

        request.Property(x => x.Outcome)
            .HasColumnName("outcome")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        request.Property(x => x.LeaseExpiresAt)
            .HasColumnName("lease_expires_at");

        request.Property(x => x.DecidedAt)
            .HasColumnName("decided_at")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd();

        request.HasIndex(x => new { x.JobId, x.DecidedAt })
            .HasDatabaseName("ix_lease_renewal_requests_job_decided_at");

        request.HasIndex(x => new { x.AttemptId, x.DecidedAt })
            .HasDatabaseName("ix_lease_renewal_requests_attempt_decided_at");
    }


    private static void ConfigureCompletionRequest(
        ModelBuilder modelBuilder)
    {
        var request = modelBuilder.Entity<CompletionRequest>();

        request.ToTable("completion_requests", table =>
        {
            table.HasCheckConstraint(
                "ck_completion_requests_worker",
                "length(btrim(worker_instance_id)) > 0");

            table.HasCheckConstraint(
                "ck_completion_requests_fencing_token",
                "fencing_token > 0");

            table.HasCheckConstraint(
                "ck_completion_requests_fingerprint",
                "request_fingerprint ~ '^[0-9a-f]{64}$'");

            table.HasCheckConstraint(
                "ck_completion_requests_payload",
                "jsonb_typeof(request_payload_json) = 'object'");

            table.HasCheckConstraint(
                "ck_completion_requests_outcome",
                """
                outcome IN (
                    'AcceptedSucceeded',
                    'AcceptedRetryScheduled',
                    'AcceptedFailed',
                    'AcceptedCancelled',
                    'LeaseExpiredOrLost',
                    'NotOwnerOrSuperseded',
                    'CancellationPending',
                    'AttemptAlreadyFinalized',
                    'NotFound',
                    'IdempotencyKeyConflict',
                    'InvalidOutcomeForState'
                )
                """);
        });

        request.HasKey(x => x.CompletionRequestId)
            .HasName("pk_completion_requests");

        request.Property(x => x.CompletionRequestId)
            .HasColumnName("completion_request_id")
            .ValueGeneratedNever();

        // Deliberately no foreign keys: NotFound decisions must be durable.
        request.Property(x => x.JobId)
            .HasColumnName("job_id")
            .IsRequired();

        request.Property(x => x.AttemptId)
            .HasColumnName("attempt_id")
            .IsRequired();

        request.Property(x => x.WorkerInstanceId)
            .HasColumnName("worker_instance_id")
            .HasMaxLength(200)
            .IsRequired();

        request.Property(x => x.FencingToken)
            .HasColumnName("fencing_token")
            .IsRequired();

        request.Property(x => x.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasMaxLength(64)
            .IsRequired();

        request.Property(x => x.RequestPayloadJson)
            .HasColumnName("request_payload_json")
            .HasColumnType("jsonb")
            .IsRequired();

        request.Property(x => x.Outcome)
            .HasColumnName("outcome")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        request.Property(x => x.DecidedAt)
            .HasColumnName("decided_at")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd();

        request.HasIndex(x => new { x.JobId, x.DecidedAt })
            .HasDatabaseName("ix_completion_requests_job_decided_at");

        request.HasIndex(x => new { x.AttemptId, x.DecidedAt })
            .HasDatabaseName("ix_completion_requests_attempt_decided_at");
    }


    private static void ConfigureCompletionRequestConflict(
        ModelBuilder modelBuilder)
    {
        var conflict = modelBuilder.Entity<CompletionRequestConflict>();

        conflict.ToTable("completion_request_conflicts", table =>
        {
            table.HasCheckConstraint(
                "ck_completion_request_conflicts_fingerprint",
                "request_fingerprint ~ '^[0-9a-f]{64}$'");

            table.HasCheckConstraint(
                "ck_completion_request_conflicts_payload",
                "jsonb_typeof(request_payload_json) = 'object'");

            table.HasCheckConstraint(
                "ck_completion_request_conflicts_outcome",
                "outcome = 'IdempotencyKeyConflict'");
        });

        conflict.HasKey(x => new
            {
                x.CompletionRequestId,
                x.RequestFingerprint
            })
            .HasName("pk_completion_request_conflicts");

        conflict.Property(x => x.CompletionRequestId)
            .HasColumnName("completion_request_id")
            .ValueGeneratedNever();

        conflict.Property(x => x.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasMaxLength(64)
            .IsRequired();

        conflict.Property(x => x.RequestPayloadJson)
            .HasColumnName("request_payload_json")
            .HasColumnType("jsonb")
            .IsRequired();

        conflict.Property(x => x.Outcome)
            .HasColumnName("outcome")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        conflict.Property(x => x.DecidedAt)
            .HasColumnName("decided_at")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd();

        conflict.HasOne<CompletionRequest>()
            .WithMany()
            .HasForeignKey(x => x.CompletionRequestId)
            .OnDelete(DeleteBehavior.Restrict);
    }

}