# TRANSPORT REVIEW 03 READ HELPER SEMANTICS

## Summary

The transport pipe convenience helpers had two semantic problems:

1. `ReadAsync` did not consume the pipe input even though its name strongly implies consumption.
2. `PeekAsync` and `ReadAsync` both ignored the caller's cancellation token.

## Chosen Direction

Keep the existing public helper names, but make their behavior explicit and useful:

- `PeekAsync`
  - leaves the data available for the next read
  - forwards the cancellation token

- `ReadAsync`
  - consumes the current input buffer
  - returns a stable snapshot of the consumed bytes
  - forwards the cancellation token

## Why `ReadAsync` Returns A Snapshot

Once a `PipeReader` is advanced past consumed data, the original `ReadResult.Buffer` is no longer safe to hand back as-is.

To keep the API shape while making the semantics correct, `ReadAsync` now copies the readable buffer into a snapshot sequence before advancing the underlying pipe.

## Tradeoff

`ReadAsync` now allocates for the returned snapshot. That is acceptable for a convenience API, but callers on hot paths should continue using `pipe.Input.ReadAsync(...)` directly when they need full control over allocations and advancement.
