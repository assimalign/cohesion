# Assimalign.Cohesion.Database.Execution — Design

The shared execution substrate (area architecture:
[resources/Database/DESIGN.md](../../DESIGN.md) §3.2). Planners and model semantics
deliberately live outside it — this layer owns the *shape* of an execution (context,
stages, results) and the *guarantees* around its transaction boundary, nothing about
what a query means.

## Design intent

Every engine runs the same flow: a session hands a parsed request to an execution
that runs inside a transaction scope and yields a result. Centralizing that flow
buys uniform boundary semantics (one place where auto-commit is correct), a shared
extension seam (stages for tracing/retry/timeout that work for every model), and a
single result vocabulary the wire protocol can serialize without knowing models.

## Why-this-not-that decisions

- **Middleware-shaped pipeline, not an operator tree.** The repo is middleware-first
  (the Web area's 2026-07 composition direction), and the concerns this layer owns —
  cancellation, boundaries, tracing, retry — are cross-cutting wrappers, not data
  operators. Model planners build operator trees *inside* the terminal delegate;
  imposing a shared operator algebra here would smuggle model semantics into the
  kernel (the exact thing the lane guardrail forbids).
- **`IQueryTransactionScope` instead of referencing `Database.Transactions`.** The
  dependency direction makes the obvious choice impossible: the area contract root
  references this project (sessions are typed in `QueryRequest`/`QueryResult`), and
  `Database.Transactions` references the root — so `Execution → Transactions` would
  be a cycle. The seam is also *better* than a hard reference: the pipeline needs
  exactly three things (implicit?, commit, rollback), and engines can satisfy them
  from `ITransactionContext`+manager, from a raw storage transaction, or from a test
  double.
- **Boundary rules enforced at the pipeline edge, not inside stages.** Stages always
  observe an active scope; exactly one place decides commit/rollback. The rules
  (#164): implicit + `Success` result → commit; implicit + failed result → rollback
  (result still returned, not thrown); any exception or cancellation → rollback,
  then the *original* error propagates — a rollback failure faults the scope but
  never masks the root cause. Explicit scopes are untouched: the session owns them.
- **Rollback uses `CancellationToken.None`.** An aborted request must still release
  its transaction; honoring the abort token during cleanup would leak scopes.
- **`QueryExecutionException` as a local error root.** This project sits below the
  area root (`DatabaseException` is unavailable by dependency direction), and the
  exception-root rule scopes roots to the owning library.
- **Known asymmetry:** `IQueryExecutor.ExecuteAsync` returns `Task<QueryResult>`
  while the session surface uses `ValueTask` — kept for now; the executor seam
  predates the pipeline and unifying it is surface churn for zero behavior.

## Lifecycle pattern

A `QueryExecutionContext` lives for exactly one execution. The pipeline links the
context's `RequestAborted` with the per-call token; cancellation between stages is
the pipeline's duty, cancellation inside long-running work is the operator's. The
item bag is engine-private state between stages — stages must not require entries
earlier stages did not put there.

## Non-goals

- No operator/plan algebra (model planners own it; see the lane guardrail).
- No retry policy in the box — retry is a stage an engine registers, because only
  the engine knows which of its errors are retryable.
- No streaming-result buffering: `QueryResultSet.GetRowsAsync` streams; anything
  that must buffer does so in a model layer that knows the memory budget.

## AOT posture

Contracts, sealed data carriers, and delegate composition — no reflection, no
expression trees, no runtime codegen.
