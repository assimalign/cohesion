# TRANSPORT REVIEW 04 PIPELINE CANCELLATION

## Summary

The transport pipeline already accepted a `CancellationToken`, but the middleware abstraction dropped it before any middleware could observe it.

## Chosen Direction

Make cancellation part of the middleware contract itself:

- `TransportMiddleware` now accepts a `CancellationToken`
- typed `Use(...)` registrations now receive the current token
- middleware continuations (`next`) now require the same token to be passed through

## Why This Shape

This makes cancellation explicit at the same abstraction level as `connection` and `context`.

Middleware can now:

- forward cancellation into async I/O
- honor shutdown and timeout boundaries directly
- cooperate with higher-level connection open and accept flows without hidden behavior

## Tradeoff

This is a breaking API change for middleware registrations that call `next(...)`, because they must now pass the active `CancellationToken` into the continuation.

That tradeoff is intentional. It avoids silent token loss and keeps the pipeline contract honest.
