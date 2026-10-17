# Job Completion Contract — V1

Status: Proposed contract

## Purpose

Define how a worker reports the outcome of a job execution
and how Forge accepts results without allowing stale workers
to overwrite newer outcomes.

PostgreSQL is the authoritative source of job state,
execution ownership, and completion decisions.

## Completion Request

Every completion request must include:

- jobId
- attemptId
- workerInstanceId
- fencingToken
- completionRequestId
- outcome

The outcome must be one of:

- Succeeded
- Failed
- Cancelled

A successful outcome must include a result descriptor.
A failed outcome must include bounded failure information,
such as failureCode and failureMessage.

A cancelled outcome may include cancellation details.

The exact result schema and payload size limits are
implementation-design concerns.

The worker must not choose the job's retry policy,
maximum attempt count, or retry delay.

## Ownership and Validity

A completion may be accepted only when the control
application confirms that:

- The job exists.
- The attemptId identifies the current execution attempt.
- The workerInstanceId matches the current claim owner.
- The fencingToken matches the current claim.
- The job is in a state that permits the reported outcome.
- The lease is valid at the authoritative decision point.

A completion request must not renew or revive a lease.

An expired or superseded claim must not authorize a new
completion.

The control application must validate ownership and apply
the completion decision atomically.

Lease validity must be evaluated using the authoritative
database clock, not a worker-supplied timestamp.

## Successful Completion

A Succeeded outcome may be accepted only while the job
is Running and the current claim is valid.

Before the job becomes Succeeded, the result data or its
durable artifact reference must be available and validated
according to the implementation's storage rules.

The accepted completion must atomically record:

- The job state as Succeeded.
- The accepted result descriptor.
- The final status of the execution attempt.
- The durable completion idempotency record.
- Any required outbox event.

These database changes must be committed in one transaction.

If the result is stored outside PostgreSQL, it must be
durably written before the database completion is committed.
An implementation must account for artifacts left behind
when the database transaction does not commit.

A terminal job must not accept a different result later.

## Failed Completion

A Failed outcome reports that the worker could not complete
the execution successfully.

The control application decides whether the failure is
retryable according to the configured retry policy.

If another attempt is permitted:

- The current attempt is recorded as failed.
- The job returns to Queued.
- The next eligible time follows the retry policy.
- The state change and any required outbox notification
  are committed atomically.

If the failure is non-retryable or the attempt limit has
been exhausted, the job becomes Failed.

A worker must not unilaterally override the retry policy.

Failure reporting must not allow a stale attempt to alter
the state of a newer attempt.

## Cancelled Completion

A Cancelled outcome may finalize cancellation only when
the job is in CancelRequested and the completion request
still belongs to the current valid claim.

The worker is expected to stop cooperatively when
cancellation is requested.

If the lease expires before cancellation is confirmed,
the recovery mechanism must revoke the stale claim before
finalizing cancellation.

A successful completion must not be accepted after
cancellation has won the state transition.

Cancellation does not undo external side effects that
already occurred.

## Completion and Cancellation Race

Successful completion and cancellation may race.

If successful completion commits first, the job becomes
Succeeded and a later cancellation request is too late.

If cancellation is recorded first, the job becomes
CancelRequested and the old attempt must not subsequently
commit a successful result.

A worker receiving CancellationPending must cooperate
with cancellation rather than treating the request as
a successful completion.

If it later confirms cancellation, that confirmation is
a separate logical completion operation.

The winning transition must be determined atomically
by the control application.

## Idempotency and Lost Responses

Every logical completion operation must have a unique
completionRequestId.

A retry of the same operation must preserve the original
completionRequestId and all request parameters.

Reusing the same completionRequestId with different
parameters must be rejected as an idempotency conflict.

Every definitive first-time completion outcome, including
rejections, must be durably recorded with the original
request parameters.

The completion outcome record must be committed atomically
with any state change produced by that outcome.

If a response is lost, the worker retries with the same
completionRequestId.

A replay must:

- Return the original recorded completion outcome.
- Avoid applying the completion a second time.
- Avoid duplicating result acceptance or outbox events.
- Report current job and attempt status separately.

An accepted historical completion does not imply that
the worker still owns a valid execution claim.

Transient failures whose outcome was not committed may
be retried using the same completionRequestId.

The idempotency record must be retained for the documented
retry/replay window so that permitted retries cannot be
mistaken for new completion requests.

## Completion Outcomes

The control application must distinguish at least:

- AcceptedSucceeded
- AcceptedRetryScheduled
- AcceptedFailed
- AcceptedCancelled
- LeaseExpiredOrLost
- NotOwnerOrSuperseded
- CancellationPending
- AttemptAlreadyFinalized
- NotFound
- IdempotencyKeyConflict
- InvalidOutcomeForState

An idempotent replay must also report the current job
and attempt status separately from the original outcome.

A different completionRequestId must not cause a finalized
attempt to accept a second, conflicting completion.

## Lease Expiration and Recovery

Completion and lease recovery must use atomic conditional
state transitions.

If completion commits while the claim is valid, recovery
must observe the resulting state and must not revoke a
completed job using stale information.

If recovery revokes the claim first, a later completion
from that claim must be rejected.

A new execution attempt must receive a new attemptId
and a higher fencingToken.

A stale worker must never overwrite a newer attempt's
accepted result.

## General Invariants

- PostgreSQL is the authoritative source of job state.
- Only the control application accepts job completion.
- A broker message does not grant execution ownership.
- A valid current claim is required for new completion.
- Result acceptance and job state transition are atomic.
- A completion retry must not accept a result twice.
- Terminal job states cannot be changed by stale attempts.
- Cancellation and completion races have one atomic winner.
- Any required outbox records commit with the state change.

## Versioning

This is the proposed V1 job completion contract.

Implementation details must follow these rules or be
changed through a reviewed design update.
