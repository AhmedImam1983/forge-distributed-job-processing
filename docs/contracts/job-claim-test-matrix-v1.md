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
