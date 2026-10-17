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

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureJob(modelBuilder);
        ConfigureSubmissionRequest(modelBuilder);
        ConfigureOutbox(modelBuilder);
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

        job.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd();

        job.Property(x => x.NextEligibleAt)
            .HasColumnName("next_eligible_at")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd();

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
}