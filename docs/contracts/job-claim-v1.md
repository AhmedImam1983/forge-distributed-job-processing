# Job Claim Contract — V1

Status: Proposed contract

## Purpose

Define how a worker requests permission to execute a job.

Receiving a broker message does not grant execution ownership.

## Claim Request
The claim operation must be idempotent.

If the same claimRequestId is retried with the same jobId
and workerInstanceId, the control application must return
the original claim outcome rather than creating a new attempt.

Reusing a claimRequestId with different request parameters
must be rejected.

- jobId
- workerInstanceId
- claimRequestId

Retries of the same claim request must preserve claimRequestId.
A repeated claimRequestId must never create a new attempt
or renew an expired lease implicitly.

If the original claim is no longer current, the control
application must report that the claim has expired or
been lost. It must not restore the previous ownership.

A new claim request may be created only when the job
has become eligible for claiming again.

## Successful Claim

A claim request must produce an explicit outcome:

- Granted
- AlreadyOwnedBySameRequest
- AlreadyClaimed
- NotEligible
- NotFound
- ExpiredOrLost

the control application must resolve the retry to the original
claim record without creating a new attempt. It must report
the claim's current validity; an expired or superseded claim
must never be reported as a currently valid grant.

A successful claim returns:

- attemptId
- fencingToken
- leaseExpiresAt
- authoritative execution descriptor

## Ownership Rules

- PostgreSQL is the authoritative source of job state.
- Claim validation and ownership assignment must be atomic.
- At most one current claim may be valid for a job.
- A cancelled or terminal job cannot be claimed.
- Duplicate messages must not create duplicate jobs.
- A worker must present valid ownership when renewing a lease
  or submitting a completion result.

## Lease Recovery
Lease expiration must be evaluated using the database's
authoritative clock.

Lease renewal, recovery, and completion must use atomic
conditional state changes so that an expired or stale claim
cannot overwrite a newer valid claim.

Expired claims must be detected and recovered through a
durable control-process mechanism.

Lease recovery must not race with a valid lease renewal or
accept completion from a stale attempt.

## Broker Acknowledgement

A worker acknowledges a message after a durable claim has
been confirmed.

If the claim outcome is unknown because of a transient
failure, the worker must not acknowledge blindly.

Stale or duplicate notifications may be acknowledged after
the control service confirms that no new execution is needed.

## Failure Handling

Claim, renewal, and completion operations must tolerate
network retries without creating conflicting ownership.

The exact retry and lease timing policies will be documented
in the implementation design.
