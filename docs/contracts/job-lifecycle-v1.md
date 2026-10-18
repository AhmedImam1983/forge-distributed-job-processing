# Job Lifecycle Contract — V1

Status: Proposed contract

## Purpose

Define the lifecycle of a Forge job, its allowed state
transitions, retry behaviour, cancellation, and recovery.

PostgreSQL is the authoritative source of job state.

## Job States

- Queued: The job is waiting to be scheduled or claimed.
  It may be temporarily ineligible until its next eligible
  time, for example, during retry backoff.
- Running: The job has a current recorded execution attempt;
 its claim is usable only while the lease is valid.
- CancelRequested: Cancellation was requested while a claim
  was active, but cancellation has not yet been finalized.
- Succeeded: The result has been durably recorded and
  the current execution attempt was accepted.
- Failed: The job failed permanently or exhausted its
  allowed execution attempts.
- Cancelled: The job has been cancelled according to
  the cancellation and ownership rules.

## Initial Acceptance

A job is considered accepted only after its durable job
record and required outbox record have been committed.

The persisted job state starts as Queued.

Acceptance does not mean execution has started.

## Allowed State Transitions

### Queued -> Running

Allowed only when an atomic claim operation grants a valid
execution claim.

Running means an execution attempt is the job's current
recorded attempt.

The claim is usable only while its lease is valid and the
attempt remains current.

An expired lease may remain recorded as Running until the
recovery mechanism reconciles it. An expired claim must not
authorize lease renewal or completion.

### Queued -> Cancelled

Allowed when a cancellation request wins an atomic
conditional state transition before a valid claim is granted.

The job must not subsequently be claimed.

### Running -> Succeeded

Allowed only when:

- The current attempt still owns valid execution authority.
- The fencing token and attempt identity match.
- The result has been durably recorded.
- The completion transition succeeds atomically.

### Running -> Queued

Allowed after a retryable failure or recovered expired claim,
provided the retry policy permits another attempt.

The state change and required outbox notification must be
committed atomically.

### Running -> Failed

Allowed when the failure is non-retryable or the configured
attempt limit has been exhausted.

### Running -> CancelRequested

Allowed when a cancellation request wins the atomic state
transition before completion is accepted.

### CancelRequested -> Cancelled

Allowed when cancellation is confirmed, or when recovery
revokes the expired claim and makes the old attempt unable
to commit a result.

Cancellation does not guarantee that external side effects
already performed by a handler have been undone.

## Attempt Lifecycle

A job and an execution attempt are different concepts.

- A job has one lifecycle and one stable jobId.
- Each execution attempt has its own attemptId.
- Retrying a job creates a new attempt.
- A failed attempt does not necessarily make the job Failed.
- Only one current execution claim may be valid for a job.
- A superseded attempt must not overwrite the result of a
  newer attempt.

## Retry Rules

- Retry only failures classified as retryable.
- Respect the configured maximum attempt count.
- Apply a delay and backoff policy before retrying.
- Do not retry a job after cancellation has been accepted.
- When retrying, atomically update the job state and create
  the required outbox notification.
- A retry must not reuse the previous attemptId or restore
  an expired claim.

### V1 Lease Recovery Policy

- An expired execution lease is classified as retryable in V1.
- The default maximum is three total attempts, including the initial attempt.
- By default, retry delays start at five seconds and double after
  each expired attempt, capped at 300 seconds. These values are
  configurable through the `LeaseRecovery` settings.
- The retry delay determines both `NextEligibleAt` and the outbox
  message's `NextAttemptAt`.
- If the attempt limit is exhausted, the job becomes `Failed`.
- If cancellation is pending when the lease expires, recovery revokes
  the expired claim and finalizes the job as `Cancelled` without retrying.
- Every retry creates a new attempt and uses a higher fencing token.

## Cancellation and Completion Race

Cancellation and successful completion may race.

If completion is committed first, the job becomes Succeeded
and the cancellation request is rejected as too late.

If cancellation is recorded first, the job becomes
CancelRequested, and the old attempt must not subsequently
commit a successful result.

A worker is expected to cooperate with cancellation when
its handler supports it.

A cancellation request does not forcibly terminate arbitrary
code or reverse external side effects.

## Lease Expiration and Recovery

Lease expiration must be evaluated using the database clock.

Lease renewal, recovery, cancellation, and completion must
use atomic conditional state transitions.

An expired claim is not proof that the worker process stopped.

Recovery must revoke stale execution authority before
reassigning the job or finalizing cancellation.

A stale worker must not renew an expired claim or commit
a result after its ownership has been superseded.

## Terminal States

Succeeded, Failed, and Cancelled are terminal states in V1.

No automatic transition out of a terminal state is allowed.

Any future administrative requeue operation must be
explicitly designed and audited.

## General Invariants

- PostgreSQL is the authoritative source of job state.
- State transitions must be validated and atomic.
- At most one current execution claim may be valid.
- Terminal jobs cannot be claimed.
- Duplicate broker messages must not create duplicate jobs.
- A broker acknowledgement does not prove job success.
- Job state and execution-attempt history must remain
  distinguishable.
- State transitions and required outbox records must be
  committed in the same database transaction.

## Versioning

This is the proposed V1 lifecycle contract.

Implementation details and timing policies must remain
consistent with these rules or be updated through a
reviewed design change.
