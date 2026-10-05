# Assimalign.Cohesion.Database.Execution — Overview

The model-agnostic request/result vocabulary of the Cohesion Data Platform: the
families every engine speaks (`QueryRequest`, `QueryResult`, `QueryResultSet`,
`QueryRow`, `QueryColumn`) and the outcome status (`QueryResultStatus`).

## Scope

- **Request contracts** — `QueryRequest` carries a parsed `QueryStatement` and
  optional parameters; `QueryRequest<TStatement>` gives a model its typed statement
  view. Each model engine subclasses them (`SqlQueryRequest`, the Documents, Graph and
  KeyValuePair requests).
- **Result contracts** — `QueryResult` reports a status, an affected count and
  diagnostics; `QueryResultSet` streams `QueryRow`s described by `QueryColumn`s. Each
  model engine supplies its own leaves.

Execution itself — planning, operators, transaction brackets — lives in each model
engine. The shared execution pipeline this project used to hold (`IQueryPipeline`,
`QueryPipelineBuilder`, `QueryExecutionContext`, `IQueryTransactionScope`) had no
consumer and was deleted under the concrete-first program (#1255, #1257).

## Dependencies

`Database.Language` (statements, diagnostics) and `Database.Types`. Deliberately
**below** the area contract root — the root's session surface
(`IDatabaseSession.ExecuteAsync`) is typed in this project's terms, so nothing here
may reference root types (transaction identity, engine contracts).

## Usage

```csharp
// A model engine's request: the typed statement view over the shared base.
public sealed class SqlQueryRequest : QueryRequest<SqlQueryStatement>
{
    // ...
}

QueryResult result = await session.ExecuteAsync(SqlQueryRequest.FromSql("SELECT 1"));
if (result is QueryResultSet rows)
{
    await foreach (QueryRow row in rows.GetRowsAsync())
    {
        // ...
    }
}
```

See [DESIGN.md](DESIGN.md) for the decisions behind the shape.
