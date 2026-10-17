# Job Submission Contract — V1

Status: Proposed contract

## Purpose

Define how a client submits a job to Forge and when the
platform considers that job durably accepted.

PostgreSQL is the authoritative source of job state.

A successful submission response confirms durable acceptance.
It does not mean that execution has started or completed.

## Submission Request

Every submission request must include:

- submissionRequestId
- jobType
- input

The submissionRequestId identifies one logical submission
operation and must remain unchanged when that operation
is retried.

The jobType identifies a supported handler type.

The input must use one supported representation:

- A bounded inline payload for small inputs.
- A reference to a durably accessible input artifact.

A request must not provide both input representations
unless a future explicitly versioned contract permits it.

The exact payload schemas, supported job types, and size
limits are implementation-design concerns.

Large input files must be referenced rather than embedded
in broker messages.
An input artifact reference must identify stable content
for the lifetime of the accepted job.

A reference to mutable content must include an immutable
version identifier or a content digest that can be verified.

Retries and re-execution of the same accepted job must not
silently process different input content.

## Request Identity and Idempotency

The submissionRequestId must be unique within the
authenticated client's submission scope.

The control application must derive the client identity
from the authenticated request context, not trust a
client-supplied identity field in the request body.

The idempotency record must be associated with:

- The authenticated client scope.
- The submissionRequestId.
- A canonical fingerprint of the request parameters.
- The definitive submission outcome.

A retry using the same submissionRequestId and equivalent
request parameters must return the original submission
outcome and the same jobId if the submission was accepted.

It must not create another job or another logical
job-available notification.

Reusing the same submissionRequestId with different
request parameters must return IdempotencyKeyConflict.

The request fingerprint must cover all submission fields
that affect the accepted job, including its job type,
input descriptor, and requested options.

Canonicalization must be deterministic so that equivalent
requests can be compared consistently.

## Validation

Before accepting a job, the control application must
validate at least:

- The caller is authorized to submit the requested job.
- The jobType is supported.
- The input representation is valid.
- The input meets applicable size and schema limits.
- Any requested options are permitted by server policy.
- The submissionRequestId is valid.

The client must not choose arbitrary retry limits,
retry delays, or other server-controlled execution policies.

Invalid requests must not create an accepted job or
a job-available outbox event.

Deterministic rejection outcomes must be handled
consistently for retries using the same idempotency key.

## Durable Acceptance

A job is accepted only after the database transaction
creating its durable submission records has committed.

For an accepted submission, one PostgreSQL transaction
must atomically create:

- The job record with its stable jobId.
- The initial job state as Queued.
- The submission idempotency record and accepted outcome.
- The required outbox record for job availability.

The job record, idempotency outcome, and outbox record
must either all commit or all roll back.

A submission must not be reported as accepted before
this transaction commits.

The accepted jobId must remain stable for the lifetime
of the job.

Acceptance does not guarantee immediate scheduling,
claiming, or execution.

## Concurrent Duplicate Submissions

Concurrent requests using the same authenticated client
scope and submissionRequestId must not create duplicate jobs.

The control application must enforce idempotency using
durable database constraints and transactional logic,
not an in-memory check alone.

If equivalent concurrent requests race:

- At most one logical submission may be accepted.
- All equivalent retries resolve to the same outcome.
- An accepted submission resolves to the same jobId.

If the same key is used with conflicting request
parameters, the conflicting request must be rejected.

The implementation must handle uniqueness conflicts
and transaction retries without losing the accepted job
or creating duplicate outbox events.

## Submission Response

An accepted submission must return a durable receipt
containing at least:

- submissionRequestId
- jobId
- acceptance outcome
- acceptedAt

The acceptance outcome indicates that the job was
durably accepted by Forge.

The acceptedAt timestamp must be determined by the
control application using its authoritative database
decision, not a client-supplied timestamp.

A retry of an accepted submission must resolve to the
same original acceptance receipt and jobId.

The receipt does not promise that the job is still Queued.
Clients must query job status separately to obtain the
current lifecycle state.

If the original response is lost, the client must retry
using the same submissionRequestId and identical
request parameters.

## Outbox and Broker Publication

The job-available outbox record must be committed in the
same transaction as the accepted job.

The outbox publisher delivers the notification to
RabbitMQ independently of the submission request.

Publication may be retried and may result in duplicate
message delivery.

Every logical job-available notification must preserve
its stable messageId across redelivery.

The broker message is a notification that a job may be
available; it is not proof of durable job acceptance,
execution ownership, or job completion.

A worker must claim the job through the control
application before executing it.

A failure to publish immediately must not invalidate
a job that has already been durably accepted.

## Failure Handling

If validation fails before durable acceptance, the
control application must return a definitive rejection
and must not create an accepted job.

If a transient failure occurs before a definitive
outcome is committed, the client may retry with the same
submissionRequestId and identical request parameters.

If the database commit succeeds but the response is
lost, a retry must resolve to the previously accepted
submission rather than create another job.

A durable idempotency record must be retained for the
documented retry/replay window.

The retention policy must not allow a permitted retry
to be interpreted as a new submission.

The implementation must define how deterministic
rejection outcomes and accepted outcomes are retained
and replayed.

Every definitive first-time submission outcome, including
deterministic rejections, must be durably recorded against
the authenticated client scope, submissionRequestId, and
original request fingerprint.

Recording a rejected outcome must not create a job or
a job-available outbox record.

A retry using the same key and equivalent request parameters
must return the recorded outcome. A retry using the same
key with different parameters must return an idempotency
conflict.

Transient failures whose outcome was not committed may
be retried using the same submissionRequestId.

## General Invariants

- PostgreSQL is the authoritative source of job state.
- A job is accepted only after durable transaction commit.
- Each accepted logical submission has one stable jobId.
- Retrying a submission must not create duplicate jobs.
- Submission idempotency is scoped to authenticated identity.
- Job creation and its required outbox record are atomic.
- Broker publication is independent of submission acceptance.
- A broker message does not grant execution ownership.
- Server-controlled execution policies cannot be overridden
  by untrusted submission parameters.

## Versioning

This is the proposed V1 job submission contract.

Changes to the submission request, response, idempotency
semantics, or acceptance guarantees must be reviewed
and documented before implementation.
