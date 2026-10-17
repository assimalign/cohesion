# Assimalign.Cohesion.Database.Execution — Design

The model-neutral request/result vocabulary (area architecture:
[resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md) §3.2).
This package references `Database.Types` only. It has no direct or transitive dependency on
`Database.Language`, and it owns no planner, executor, parser or transaction pipeline.

## Parameter normalization shared source (#1102, D15)

The default for open decision D15 is `shared/ParameterBindings.cs`: stateless internal
name grammar (numeric names use ASCII digits only), one-sigil removal and ordinal dictionary-copy rules. SQL, Documents and
their parsers link it through explicit `CohesionSharedSource` items. No public bind API
or visibility grant is added. Execution itself still references only Types and does
not compile or reference Language. Each engine supplies its own coded collision
exception. Graph binding remains #1138's hand-off. The canonical contract is in
[Language DESIGN](../../Assimalign.Cohesion.Database.Language/docs/DESIGN.md#parameter-contract-1102).


## Request and result families

`DatabaseRequest` is a memberless abstract root. A model defines the operations it accepts:
KeyValuePair requests carry command operands; `SqlQueryRequest` and `DocumentQueryRequest`
own their typed `Statement` and `Parameters`; `GraphRequest` owns those values for the row
and path request variants. There is no shared statement property or request-kind discriminator.

`DatabaseResult` reports status, affected count and diagnostics. `DatabaseResultSet` adds
columns and streaming `DatabaseRow` values. `DatabaseColumn` describes a column, including
optional `MaxLength`: the declared character count or binary byte limit, not an individual
value's size. SQL uses it with the shared `Character` identity for `CHAR(n)` metadata; its
execution and padding rules remain in the SQL model.

The owner approved all five result-family renames on 2026-10-16 (#1576, #1579).
The public names are `DatabaseResult`, `DatabaseResultSet`, `DatabaseResultStatus`,
`DatabaseRow` and `DatabaseColumn`; they all remain in this assembly. There are no aliases
or compatibility shims. Model-specific language results retain names such as `SqlQueryResult`,
`DocumentQueryResult` and `GraphPathsQueryResult`.

## DatabaseRow MISSING exposure (#1126)

MISSING is an optional capability (owner ruling, 2026-10-16). The protected constructor's
`supportsMissing = false` value backs non-virtual `SupportsMissing`. The non-virtual
`IsMissing(int)` throws NotSupportedException while unsupported, validates the ordinal,
then calls protected virtual `IsMissingCore(int)`. The default core throws too, following
database-area.md rules 4 and 6. This capability is the authorized shared boundary change;
no public sentinel or model-specific type enters Execution.

Only embedded and client Documents rows opt in. Their MISSING fields have IsMissing true,
IsNull false, and GetValue null. Explicit NULL has IsMissing false and IsNull true. Every
other model retains SupportsMissing false and IsMissing throws; it does not return false.
Existing row members' NVI conversion remains #1264's work. Deviates from interface-first
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

All eight fields are present: nullable values are NULL, never SQL++ MISSING. Both count
columns are null in every Tier 1 row: the planners have no statistics and EXPLAIN
never executes the described statement. A successful description changes zero records, reports
the ordinary row-result affected count of -1, and carries
parser/planner diagnostics. It uses the ordinary SQL and Graph result frames. Documents encodes
each row as a JSON document with these eight member names in its existing document stream.

This is a description shape, not a shared operator algebra: each model owns its vocabulary and
closed, reflection-free plan mapping. Test-only reflection enumerates each model's plan records
and requires a mapping. An item adding a plan record also adds its mapping and an EXPLAIN case;
whichever item lands second owns that hand-off, including collection DDL and SQL definition and
column-alteration plans. No public interface or shipped assembly visibility grant is added.

The sequencing default is T1 before #1264 (P8). P8 folds this result into its per-row result
shape; this item retains today's result bases and introduces no P8 row contract.

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
  `Database.Types`. Diagnostics may still be null; non-null diagnostics and further result
  NVI work remain with #1264.
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
