# Job Claim Test Matrix — V1

Status: Proposed test specification

## Test 1: Concurrent Claim for the Same Job

### Given
- A job exists in the Queued state.
- No valid execution claim exists.
- Worker A and Worker B are registered.

### When
- Both workers request a claim for the same job concurrently.
- Each request has a distinct claimRequestId.
- The test harness starts both requests concurrently using a synchronization barrier, so their claim attempts overlap.

### Expected Results
- Exactly one claim is granted.
- The other claim is rejected as AlreadyClaimed.
- Exactly one current execution attempt is valid.
- Only the granted worker may begin execution.
- The granted claim has a valid fencingToken.
- No conflicting active ownership is recorded.

## Test 2: Worker Crashes After Claim Before Execution

### Given

- A job exists in the Queued state.
- Worker A successfully claims the job.
- The claim has a valid attemptId, fencingToken, and lease.
- The broker message is acknowledged after the claim is confirmed.

### When

- Worker A crashes before invoking the job handler.
- The lease expires.
- The recovery mechanism processes the expired claim.
- Worker B attempts to claim the recovered job.

### Expected Results

- The abandoned attempt is not marked as successful.
- The expired claim is no longer valid.
- Recovery makes the job eligible for execution again.
- The recovery state change and required outbox notification
  are committed atomically.
- Worker B receives a new attemptId and a higher fencingToken.
- Worker A cannot renew the expired lease or commit a result
  using its stale claim.
- At most one current claim is valid for the job.

## Test 3: Claim Succeeds but Response Is Lost

### Given

- A job exists in the Queued state.
- Worker A submits a claim request with claimRequestId A.
- The control application successfully records the claim.

### When

- The claim response is lost due to a network failure.
- Worker A retries the request using the same claimRequestId.

### Expected Results

- No new execution attempt is created.
- If the original claim is still valid, the same attemptId
  and fencingToken are returned.
- If the original claim has expired or been superseded,
  the worker is informed that ownership is no longer valid.
- An expired claim is never restored implicitly.
- At most one current claim is valid for the job.