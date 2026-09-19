# SQL mapping API

All runtime APIs belong to `Assimalign.Cohesion.Database.Sql.Mapping`. Public types
are sealed concrete value/building surfaces, a composition factory, or explicit
implementations of the core interfaces. There is no reflection registration API.

| Type | Surface and purpose |
| --- | --- |
| `ISqlEntityMapping<TEntity,TKey,TSnapshot>` | Extends the core mapper, reader and writer with retained SQL metadata and detached snapshot serialization. Implemented by generated mappers. |
| `SqlMapping` | `Register` binds a retained mapping to a SQL unit of work; `Query` creates its immutable typed SELECT builder. |
| `SqlMappingStore` | Constructor accepts a caller-owned `ISqlClient`; `BeginTransactionAsync` implements the core save boundary; `QueryAsync` materializes detached entities. |
| `SqlMappingTransaction` | `CommitAsync` executes ordered buffered DML and COMMIT; `DisposeAsync` rolls back or discards the session. Constructed only by the store. |
| `SqlColumn<TEntity,TValue>` | Generated typed identifier with six comparison methods and two null tests. Its constructor validates the dialect's identifier boundary. |
| `SqlPredicate<TEntity>` | Immutable generated-column predicate; `And`, `Or` and `Not` compose SQL three-valued Boolean logic. No raw SQL constructor. |
| `SqlQuery<TEntity>` | `Where`, `OrderBy`, `ThenBy`, `Distinct`, `Take`, `Skip` create immutable query variants; `ToCommand` returns an independent parameterized client command. |

Mapping metadata consists of `TableName`, ordered `ColumnNames`/`ColumnTypes`, `KeyColumnName`,
and `ReferencedTables`. `WriteSnapshot(snapshot,target)` writes the snapshot in
that exact column order. The core's reader consumes `IReadOnlyList<object?>` and
writer consumes `IList<object?>`; binary snapshots and materialized values detach
their mutable storage.

Column tokens expose `TableName` and `ColumnName`. `Equal` and `NotEqual` lower null
to `IS NULL` and `IS NOT NULL`; `GreaterThan`, `GreaterThanOrEqual`, `LessThan`, and
`LessThanOrEqual` reject null values. `IsNull` and `IsNotNull` need no parameter.
Unknown mapping columns fail when compiling a predicate or adding an ordering.
Mismatched comparison value types fail against the retained storage-type metadata.
Non-null binary/floating-point predicates throw `NotSupportedException` before I/O;
their null tests, ordering and DISTINCT remain supported. Binary, floating-point,
DateTime and DateTimeOffset primary keys are rejected at SQL generation and runtime
metadata validation because predicates cannot address their encoded identities reliably.
`ThenBy` requires a preceding `OrderBy`; `Take` and `Skip` require nonnegative
counts. Commands returned by `ToCommand` may be inspected/executed through the SQL
client without modifying the original builder.

The mapping core supplies `MappingCommitOutcomeUnknownException`. It permanently
faults the save scope and owning SQL store; creating a new scope over that same
store does not permit replay. SQL and shared client connections expose `AbortAsync`
to discard uncertain sessions. See the [design](../../DESIGN.md) for the exact
commit/reconciliation contract and the [overview](../../OVERVIEW.md) for usage.
