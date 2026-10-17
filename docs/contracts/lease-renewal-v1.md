# Lease Renewal Contract — V1

Status: Proposed contract

## Purpose

Define how a worker renews its execution lease while processing a job, and how Forge prevents stale workers from retaining execution authority.

PostgreSQL is the authoritative source of job state and lease validity.

Receiving a successful renewal response does not grant ownership to a different attempt or change the identity of the current attempt.

## Renewal Request

Every renewal request must include:

- `jobId`
- `attemptId`
- `workerInstanceId`
- `fencingToken`
- `renewRequestId`

The control application determines the lease duration. A worker must not choose or extend the lease duration itself.

Each logical renewal operation must have a unique `renewRequestId`.

A retry of the same logical operation must preserve the original `renewRequestId` and all request parameters.

Reusing a `renewRequestId` with different parameters must be rejected as an idempotency conflict.

The idempotency record must be retained for the documented retry/replay window. Its retention policy must not allow a previously accepted request to be processed as new while retries of that request are still permitted.

## Preconditions for Renewal

A renewal may be accepted only when all the following conditions hold at the authoritative decision point:

- The job exists.
- The job state is `Running`.
- The `attemptId` identifies the current execution attempt.
- The `workerInstanceId` matches the current claim owner.
- The `fencingToken` matches the current claim.
- The current lease has not expired.
- Cancellation has not been accepted for the current attempt.

An expired lease must not be revived, even if the recovery mechanism has not yet processed the expired claim.

A worker must not renew a claim that has been superseded.

## Successful Renewal

A successful renewal must:

- Validate ownership and lease validity atomically.
- Use the authoritative database clock.
- Extend the lease according to the configured lease duration.
- Preserve the current `attemptId`.
- Preserve the current `fencingToken`.
- Record the renewal outcome durably for idempotent retries.

The lease update and the durable idempotency record must be committed atomically.

The new expiration time is determined by the control application, not by a timestamp supplied by the worker.

## Idempotency and Lost Responses

A renewal request may succeed even when its response is lost.

When the worker retries with the same `renewRequestId` and identical parameters:

- The control application must not apply the renewal a second time.
- The original definitive renewal outcome must be returned.
- The original `leaseExpiresAt` must not be silently extended again.
- A recorded success must not be treated as proof that the claim is still valid at the time of the retry.

The response must distinguish the original renewal outcome from the current claim status.

For an idempotent replay, the control application must return the original recorded renewal outcome and original `leaseExpiresAt`, and must separately report the current claim status evaluated at replay time. Checking current claim status must not extend the lease.

The current claim status must distinguish at least:

- `Valid`: the same attempt still owns a non-expired lease and cancellation has not been accepted.
- `Expired`: the lease has expired, even if recovery has not yet reconciled the job.
- `Superseded`: a different attempt or owner now holds the current claim.
- `CancellationPending`: the job is in `CancelRequested`.
- `NotFound`: the job or claim can no longer be found.

A replayed `Renewed` outcome alone does not authorize continued execution. If the current claim status is no longer valid, the worker must stop relying on the old claim. Even when the status is `Valid`, the replay does not grant extra time beyond the original `leaseExpiresAt`.

Reusing the same `renewRequestId` with different parameters must return an idempotency conflict.

A new `renewRequestId` is used for a new logical renewal operation. If the outcome of a prior request is unknown, the worker must resolve it by retrying that same request before issuing a new logical renewal.

Every definitive first-time renewal outcome, including
rejections, must be durably recorded against the
renewRequestId and original request parameters.

The outcome record must be committed atomically with any
state change that produces the outcome.

Replays must return the recorded outcome while reporting
current claim status separately.

Transient failures whose outcome was not committed may be
retried using the same renewRequestId.

## Renewal Outcomes

The control application must distinguish these renewal outcomes:

- `Renewed`
- `LeaseExpiredOrLost`
- `CancellationPending`
- `NotOwnerOrSuperseded`
- `NotFound`
- `IdempotencyKeyConflict`

An idempotent replay must also report current claim status separately, as specified above. A renewal must never be reported as a fresh successful grant if the lease has expired or the attempt has been superseded.

Transient failures that leave the outcome unknown must be handled using the same `renewRequestId` until the result can be resolved.

## Lease Expiration and Recovery

Lease expiration must be evaluated using the database clock.

The lease is valid only while its expiration time is strictly later than the authoritative decision time.

Renewal and recovery must use atomic conditional state transitions. Their conditions must be checked as part of the authoritative state change, not only in an earlier read.

If renewal wins while the lease is valid, recovery must observe the updated expiration time and must not revoke that still-valid claim using stale information.

If recovery revokes the claim first, the renewal must be rejected.

An expired lease must never be revived by a delayed request.

When a job is claimed again after recovery, the new attempt must receive a new `attemptId` and a higher `fencingToken`.

The old worker must not renew its lease or commit a result using its stale ownership information.

## Cancellation

A job in `CancelRequested` must not receive a new lease renewal.

The worker should stop cooperatively when cancellation is confirmed.

A worker must not interpret a renewal response as permission to ignore a cancellation request or commit a result after ownership has been revoked.

Cancellation does not guarantee that external side effects already performed by a handler have been undone.

## Worker Responsibilities

The worker must:

- Renew before the current lease expires.
- Preserve `renewRequestId` when retrying an uncertain request.
- Stop relying on its claim when renewal is definitively rejected or current claim status shows that the claim is no longer valid.
- Avoid treating an unknown renewal outcome as a successful renewal.
- Present the current attempt identity and `fencingToken` when submitting a completion result.
- Never assume that a local clock overrides the database's lease-validity decision.

The worker's renewal interval and safety margin are configuration and implementation concerns, not fixed by this contract.

## General Invariants

- PostgreSQL is the authoritative source of lease validity.
- At most one current execution claim may be valid for a job.
- Renewal does not change the current `attemptId` or `fencingToken`.
- A renewal retry must not extend the lease twice.
- An expired or superseded claim cannot regain authority.
- Lease renewal and recovery must be safe under concurrency.
- A broker message does not grant execution ownership.
- Completion must validate current ownership independently.

## Versioning

This is the proposed V1 lease renewal contract.

Implementation details must follow these rules or be changed through a reviewed design update.
