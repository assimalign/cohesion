# Assimalign.Cohesion.Database.Execution — Design

The shared request/result vocabulary (area architecture:
[resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md) §3.2). Planners,
operators and model semantics deliberately live outside it — this layer owns the *shape* of
what a session receives and returns, nothing about what a query means or how it runs.

## Design intent

Every engine answers the same session call: a parsed request in, a result out. One result
vocabulary lets the wire protocol and the hosting layer handle any model's results without
knowing the model, while each model engine supplies its own request and result leaves.

## Why-this-not-that decisions

- **Abstract request and result families, model leaves.** `QueryRequest`, `QueryResult`,
  `QueryResultSet` and `QueryRow` are abstract; each model subclasses them
  (`SqlQueryRequest`, `DocumentQueryRequest`, `GraphQueryRequest`, the KeyValuePair
  requests, and their result leaves). They predate the concrete-first rule and keep no
  interface twin; phase 8 of the concrete-types program (#1264) tightens them (NVI, typed
  accessors, never-null diagnostics, `docs/programs/DATABASE_CONCRETE_TYPES_PLAN.md` §5.3).
- **No shared execution pipeline (owner decision of 2026-10-04, D6 in the plan).** The
  project used to carry a middleware-shaped pipeline (`IQueryPipeline`,
  `IQueryPipelineStage`, `QueryPipelineBuilder`, `QueryExecutionContext`), an
  `IQueryTransactionScope` boundary seam, `IQueryExecutor`, `QueryStatementResult` and a
  `QueryExecutionException` root. No engine ever ran a statement through them: each model
  engine owns its planner, its executor and its transaction brackets over
  `Database.Transactions`. They were deleted with #1257 rather than kept "for later"; the
  Web-style tap-in pipeline is the composition the owner ruled out for this area.
- **No exception root here.** With the pipeline gone this project throws nothing of its
  own. Engines report execution failures through the area root's `DatabaseException`
  family, which sits above this project in the dependency graph.

## Non-goals

- No operator or plan algebra (model planners own it).
- No streaming-result buffering: `QueryResultSet.GetRowsAsync` streams; anything that must
  buffer does so in a model layer that knows the memory budget.

## AOT posture

Abstract contracts and a sealed column descriptor — no reflection, no expression trees, no
runtime code generation.
