# Assimalign.Cohesion.Database.Execution — Design

The model-neutral request/result vocabulary (area architecture:
[docs/resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md) §3.2).
This package references `Database.Types` only. It has no direct or transitive dependency on
`Database.Language`, and it owns no planner, executor, parser or transaction pipeline.

## Parameter normalization shared source (#1102, D15)

The default for open decision D15 is `shared/Parameters/ParameterBindings.cs`: stateless internal
name grammar (numeric names use ASCII digits only), one-sigil removal and ordinal dictionary-copy rules. SQL, Documents, Graph and
their parsers link it through explicit `CohesionSharedSource` items. No public bind API
or visibility grant is added. Execution itself still references only Types and does
not compile or reference Language. Each engine supplies its own coded collision
exception. The canonical contract is in
[Language DESIGN](../../Assimalign.Cohesion.Database.Language/docs/DESIGN.md#parameter-contract-1102).


## Request and result families

`DatabaseRequest` is a memberless abstract root. A model defines the operations it accepts:
KeyValuePair requests carry command operands; `SqlQueryRequest` and `DocumentQueryRequest`
own their typed `Statement` and `Parameters`; `GraphRequest` owns those values for the row
and path request variants. There is no shared statement property or request-kind discriminator.

`DatabaseResult` reports status, affected count and non-null diagnostics. A result with nothing
to report returns an empty collection, including successful row, command and path results (#1264).
`DatabaseResultSet` adds columns and streaming `DatabaseRow` values. `DatabaseColumn` describes a column, including
optional `MaxLength`: the declared character count or binary byte limit, not an individual
value's size. SQL uses it with the shared `Character` identity for `CHAR(n)` metadata; its
execution and padding rules remain in the SQL model.

The owner approved all five result-family renames on 2026-10-16 (#1576, #1579).
The public names are `DatabaseResult`, `DatabaseResultSet`, `DatabaseResultStatus`,
`DatabaseRow` and `DatabaseColumn`; they all remain in this assembly. There are no aliases
or compatibility shims. Model-specific language results retain names such as `SqlQueryResult`,
`DocumentQueryResult` and `GraphPathsQueryResult`.

## Row access contract (#1264)

DatabaseRow is the NVI seam for six sealed leaves: SqlMaterializedRow,
KeyValueMaterializedRow, DocumentQueryRow, GraphQueryRow, the remote DocumentRow and
DatabasePlanDescriptionRow. FieldCount remains abstract because each leaf owns its shape.
Every public ordinal accessor validates 0 <= ordinal < FieldCount before calling its
protected core. Framework ArgumentOutOfRangeException guards own the ordinal failure,
including IsMissing on unsupported rows; valid unsupported MISSING access still throws
NotSupportedException. No diagnostic identity or new value kind is introduced.

The typed cores retain each leaf's conversions, NULL/MISSING behavior and conversion
failures; GetValue retains the original boxed value or model-specific output mapping.
Calls between cores reuse the already validated ordinal rather than repeat public checks.
Typed access does not allocate a new box. SQL's materialized wrapper keeps its instance
layout, and the per-row allocation guard compares construction with all typed accessors
and GetValue through a DatabaseRow reference. Existing Release allocation guards cover
statement and wire consumers. These checks make no wall-clock or dispatch-throughput claim.
DatabaseResultSet.GetRowsAsync keeps its existing shape: the paused benchmark suite
provides no per-row dispatch evidence to justify replacing it.

## DatabaseRow MISSING exposure (#1126)

MISSING is an optional capability (owner ruling, 2026-10-16). The protected constructor's
`supportsMissing = false` value backs non-virtual `SupportsMissing`. The non-virtual
`IsMissing(int)` validates the ordinal, throws NotSupportedException for a valid ordinal
while unsupported, then calls protected virtual `IsMissingCore(int)`. The default core throws too, following
database-area.md rules 4 and 6. This capability is the authorized shared boundary change;
no public sentinel or model-specific type enters Execution.

Only embedded and client Documents rows opt in. Their MISSING fields have IsMissing true,
IsNull false, and GetValue null. Explicit NULL has IsMissing false and IsNull true. Every
other model retains SupportsMissing false and IsMissing throws; it does not return false.
Every other ordinal member uses a protected abstract core. Deviates from interface-first
(database-area.md): the existing abstract row family remains the concrete-first seam.

## Plan descriptions (#1104)

`DatabasePlanDescription` is a sealed materialized `DatabaseResultSet`, created from sealed
`DatabasePlanNode` values. Every model emits depth-first pre-order, with zero-based sequential
node identifiers and a null parent for the root. The fixed column order is:

| Column | DatabaseType | Nullable |
| --- | --- | --- |
| `node_id` | Int32 | No |
| `parent_id` | Int32 | Yes |
| `operator` | String | No |
| `target` | String | Yes |
| `index` | String | Yes |
| `detail` | String | Yes |
| `estimated_rows` | Int64 | Yes |
| `actual_rows` | Int64 | Yes |

All eight fields are present: nullable values are NULL, never SQL++ MISSING. DatabasePlanNode.EstimatedRows and ActualRows supply the two optional count columns.
Models without measurements retain NULL. SQL bounded probes can populate estimated_rows; plain
EXPLAIN never executes the described statement and leaves actual_rows NULL. SELECT-only SQL
EXPLAIN ANALYZE populates measured candidate work before residuals (#1522), not final output
cardinality. Details identify exact probes, incomplete lower bounds and heuristic join estimates. A successful description changes zero records, reports
the ordinary row-result affected count of -1, and carries
parser/planner diagnostics. It uses the ordinary SQL and Graph result frames. Documents encodes
each row as a JSON document with these eight member names in its existing document stream.

This is a description shape, not a shared operator algebra: each model owns its vocabulary and
closed, reflection-free plan mapping. Test-only reflection enumerates each model's plan records
and requires a mapping. An item adding a plan record also adds its mapping and an EXPLAIN case;
whichever item lands second owns that hand-off, including collection DDL and SQL definition and
column-alteration plans. No public interface or shipped assembly visibility grant is added.

DatabasePlanDescriptionRow follows the same ordinal NVI contract as model and client rows;
its invariant conversions, UTF-8 bytes and InvalidOperationException for required NULL values
remain unchanged.

## Session capabilities

The capability bases live in the root `Database` assembly, which references this package:

| Base | Capability | Models |
|---|---|---|
| `DatabaseSession` | Lifecycle, state and explicit transactions | Every model, including Blob |
| `DatabaseExecutionSession<TRequest>` | Typed `ExecuteAsync(TRequest, CancellationToken)` | Sql, Documents, Graph and KeyValuePair |
| `DatabaseLanguageSession<TRequest>` | Typed execution plus `ExecuteAsync(string, parameters, CancellationToken)` | Sql, Documents and Graph |

Both generic bases constrain `TRequest` to `DatabaseRequest`. Public execution members are
non-virtual and perform validation and tracing before calling protected abstract cores.
The language base declares its typed forwarder before its text member and forwards through
the base's public NVI. Consequently a foreign request fails compilation with the model's
request type in the diagnostic. A plain root session or Blob session has no execute member.
KeyValuePair's command parser is an internal wire entry, not a public language capability.

## Decisions and remaining work

- `Diagnostic`, `DiagnosticSeverity` and `DiagnosticLocation` moved unchanged to
  `Database.Types`. Diagnostics are non-null; DatabaseRow owns ordinal validation.
- There is no shared execution pipeline. The unused pipeline, transaction-scope and executor
  abstractions were deleted under #1257. Models own their execution and transaction brackets.
- There is no exception root here. Model failures use the root assembly's `DatabaseException`
  family; this lower package cannot reference that root.
- Text execution retains its existing validation, refusal and tracing order. Parsing every
  language request before entering the traced execution member is deferred to separate work.
- Streaming remains the model's responsibility: `DatabaseResultSet.GetRowsAsync` does not
  introduce shared buffering or choose a model's memory budget.

## AOT posture

Abstract request/result contracts, a sealed column descriptor and ordinary generic session
bases require no reflection, expression trees, runtime code generation or generic virtual method.


## Debugger inspection (#1470)

Eligible concrete source types use private nested debugger views of reviewed public/protected
state; existing inherited display/proxy pairs are reused. See the shared
[debugger convention](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md#debugger-inspection).
Only stored fields and reviewed pure getters are projected; inspection does not execute queries, enumerate results, invoke clocks or mutate lifecycle state.
Runtime APIs, methods, allocation budgets, persistent formats and wire behavior are unchanged.
