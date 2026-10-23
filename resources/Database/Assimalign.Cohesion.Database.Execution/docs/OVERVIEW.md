# Assimalign.Cohesion.Database.Execution — Overview

The model-neutral request/result vocabulary of the Cohesion Data Platform:
`DatabaseRequest`, `DatabaseResult`, `DatabaseResultSet`, `DatabaseResultStatus`,
`DatabaseRow` and `DatabaseColumn`.

## Scope

- **Requests:** `DatabaseRequest` has no members. Model requests own their inputs. Language
  requests own a typed `Statement` and `Parameters`; key-value requests carry command operands.
- **Results:** `DatabaseResult` reports status, affected count and non-null diagnostics (empty when none).
  `DatabaseResultSet` streams `DatabaseRow` values described by `DatabaseColumn` metadata.
  Each model supplies the result implementations it needs.

Planning, parsing, execution and transaction brackets belong to model engines. Blob sessions
operate through their own storage API and have no execute member.

## Dependencies

Only `Database.Types`, which owns the shared diagnostic types as well as value types.
There is no query-language dependency, direct or transitive. The root `Database` assembly
references this package and owns `DatabaseExecutionSession<TRequest>` and
`DatabaseLanguageSession<TRequest>`; this package does not reference the root.

## Usage

```csharp
// session is a SqlDatabaseSession; only SqlQueryRequest or SQL text can be executed here.
DatabaseResult result = await session.ExecuteAsync(SqlQueryRequest.FromSql("SELECT 1"));
if (result is DatabaseResultSet rows)
{
    await using var resultLifetime = rows;
    await foreach (DatabaseRow row in rows.GetRowsAsync())
    {
        // Consume the model's row values.
    }
}
```

Sql, Documents and Graph offer typed and text execution. KeyValuePair offers typed execution
only; its server uses an internal command-text parser. A variable typed as `DatabaseSession`
offers lifecycle, state and transactions. Use the concrete model session to execute its requests.

See [DESIGN.md](DESIGN.md) for the decisions and deferred result-contract work.

Plan descriptions keep eight columns. Optional DatabasePlanNode.EstimatedRows/ActualRows carry
model-defined candidate counts; unavailable measurements remain NULL. SQL SELECT-only EXPLAIN
ANALYZE can populate actual work while plain EXPLAIN remains nonexecuting (#1522).


## Debugger inspection (#1470)

Eligible concrete source types use private nested debugger views of reviewed public/protected
state; existing inherited display/proxy pairs are reused. See the shared
[debugger convention](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md#debugger-inspection).
Only stored fields and reviewed pure getters are projected; inspection does not execute queries, enumerate results, invoke clocks or mutate lifecycle state.
Runtime APIs, methods, allocation budgets, persistent formats and wire behavior are unchanged.
