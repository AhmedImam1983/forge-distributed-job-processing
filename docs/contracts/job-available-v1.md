# Job Available Message — V1

Status: Proposed contract

## Purpose

Notify a C++ worker that a job may be available for execution.

Receiving this message does not grant permission to execute the job.

## Message Fields

| Field | Meaning |
|---|---|
| messageType | `forge.job-available` |
| schemaVersion | `1` |
| messageId | Unique identifier for this logical notification. Redelivery of the same event must preserve this value. |
| jobId | Identifier of the job in Forge |

## Ownership Rules

- PostgreSQL is the authoritative source for job state.
- The .NET control application owns job lifecycle transitions.
- RabbitMQ delivers messages and may redeliver them.
- A worker must claim the job before executing it.
- The claim operation must reject cancelled, completed, or
  otherwise ineligible jobs.
- A message redelivery must not be treated as a new job.
- The input payload, priority, and current job state are not
  authoritative when carried in a broker message.

## Failure Handling

A worker must not acknowledge a message blindly if it could
cause accepted work to be lost.

Retry, acknowledgement, and dead-letter policies will be
specified in the messaging implementation design.

## Versioning

Changes to this message contract must preserve compatibility
or introduce an explicitly supported schema version.
Unknown versions must be handled explicitly.
