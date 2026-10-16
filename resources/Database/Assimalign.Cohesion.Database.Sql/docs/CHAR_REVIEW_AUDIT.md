# CHAR review round 1 (#1437)

This audit follows `a7ee6b499`. Paths and line anchors below refer to the round-1 fixes, not
the earlier feature commits. It covers production `src` and `shared` code in every SQL child
root, plus the Types codecs, Indexing and shared Database code reached by SQL. Searches included
`is string`, `is not string`, `as string`, `typeof(string)`, string pattern arms and casts,
`ReferenceEquals`, object equality, `GetType`, all switches and storage-type equality fast paths.
There is no `GetType() == typeof(string)` site. AST/token/command switches are not value-type
dispatch; the table below identifies the value and storage-type switches individually.

## Representation decision

`SqlCastConverter.ApplyModifiers` and `SqlRowCodec.ReadValue` create `SqlCharacter`. It travels
in the evaluator's `object?[]` rows, including expression results, group keys, native function
arguments and join probes. Binding also gives an untyped literal or parameter this identity when
it compares with CHAR. This is why paths originally written for CLR strings now receive a wrapper.
Public rows unwrap to the **padded** CLR string; subquery/group/INSERT SELECT boundaries reattach
identity using column metadata. The wire transports padded strings with Character column metadata.

Keeping only a padded CLR string plus column metadata would lose identity for intermediate CAST,
CASE, COALESCE, polymorphic function and subquery results. Making that representation sound needs
a typed value slot carried through every expression, temporary row and comparison (for example,
replacing the object-valued evaluator with `SqlValue` throughout). That could eliminate CLR-string
dispatch mistakes, but is a much larger evaluator change and still needs modifier-aware calls and
separate physical/SQL equality. It is not the smaller fix for this branch. Retain the private
wrapper, centralize parameter coercion, and keep explicit conversions at the physical/public
boundaries. No public member, interface, assembly friendship or project reference was added.

## Decisions and reference behavior

- BETWEEN binds its two comparisons independently, following the local PostgreSQL clone's
  `src/backend/parser/parse_expr.c:1325-1333`. IN selects the common character type over its entire
  list before converting the shared operand (`:1182-1190`). Typed TEXT/VARCHAR selects TEXT;
  untyped literals/parameters do not force a CHAR-only list to TEXT. The existing once-per-row
  evaluation of a shared BETWEEN operand is preserved.
- ALTER COLUMN remains validation-only. Every CHAR length change is validated, and any change
  to existing padding is rejected before catalog publication. Empty/all-null columns can change
  width. Code-first validation of populated tables uses the existing deferred provisioning worker.
  This respects #1447, which owns table rewrites using #1265's machinery; silently changing only
  metadata or introducing a second rewrite implementation would not respect that contract.
- Physical equality in ALTER compares the full padded strings with ordinal equality. SQL equality,
  which ignores trailing spaces, cannot establish that a rewrite is unnecessary.
- Formats stay at record-space 12, schema v6, result-header 2. They are unreleased, and no additional
  format change is needed for these fixes. Historical signature decoding now admits Character (19).

## Baseline proof

Before source changes, the existing Release SQL test output was copied to
`_out/char-review-r1/baseline-current/net10.0`; `baseline-current/SHA256.json` records the DLL hashes.
`baseline-run` is a separate copy with **only** `Assimalign.Cohesion.Database.Sql.Tests.dll`
replaced by the new test assembly. All 57 production DLL hashes were verified unchanged, as were
the saved originals. No fixed production assembly was used for the baseline run.

Run the SQL test csproj with Release, `DOTNET_TieredCompilation=0`, `--no-build --no-restore`,
`-p:OutputPath=C:\cw\1437\_out\char-review-r1\baseline-run` and filter
`FullyQualifiedName~SqlCharacterBoundaryTests|FullyQualifiedName~BuildAsync_CharacterWidening_ShouldDeferValidationAndRejectRewrite`.
Use the ordinary output path for the fixed run. TRX files are
`_out/char-review-r1/results/baseline-final.trx` and `fixed-regressions.trx`.

All regressions were run and observed failing before their corresponding production fix.
The final matrix uses the same final test DLL against both sets of production assemblies:

| Finding / test method in SqlCharacterBoundaryTests (unless stated) | Cases | Baseline P/F/S | Fixed P/F/S | Baseline failure |
| --- | ---: | --- | --- | --- |
| 1: Join_AccentInsensitiveCharacterKeys_ShouldReturnSameRowsAfterIndexing | 1 | 0/1/0 | 1/0/0 | Seek loses the scan's matching row |
| 2: Between_MixedCharacterBounds_ShouldMatchSeparateComparisons | 4 | 0/4/0 | 4/0/0 | Shared CHAR coercion changes the TEXT comparison; literal and parameter, either bound |
| 2: In_MixedCharacterCandidates_ShouldKeepSignificantTextSpaces | 4 | 0/4/0 | 4/0/0 | Significant TEXT space disappears, either candidate order |
| 3: AlterColumn_CharacterWideningWithValues_ShouldRejectRewriteAndPreserveDefinition | 1 | 0/1/0 | 1/0/0 | Widening succeeds without repadding old values |
| 3: SqlDeferredProvisioningTests.BuildAsync_CharacterWidening_ShouldDeferValidationAndRejectRewrite | 1 | 0/1/0 | 1/0/0 | Widening bypasses deferred value validation |
| 4: BuildAsync_DroppedCharacterDomainScript_ShouldValidateHistoricalSignature | 2 | 0/2/0 | 2/0/0 | COHSQLS001 on cold Apply/Verify reopen after catalog generation changes |
| 5: Aggregate_OverlongCharacterArgument_ShouldRejectAssignment | 3 | 0/3/0 | 3/0/0 | CHAR(2) 'ab' reaches CHAR(1); arities 1, 2, 5 |
| 5: Aggregate_CharacterArgumentWithDifferentLength_ShouldApplyParameterPadding | 6 | 0/6/0 | 6/0/0 | CHAR(3) padding survives CHAR(1)/CHAR(5) parameters; arities 1, 2, 5 |
| 5: Aggregate_OverlongCharacterBesideNull_ShouldRejectBeforeStrictDispatch | 2 | 0/2/0 | 2/0/0 | STRICT skips the invalid CHAR argument; arities 2, 5 |
| 6: AlterColumn_CharacterNotNull_ShouldAcceptUnchangedPadding | 1 | 0/1/0 | 1/0/0 | Wrapper reference equality rejects valid NOT NULL alteration |
| Audit: CreateTable_CharacterFunctionCheck_ShouldAcceptCharacterComparisons | 3 | 0/3/0 | 3/0/0 | CHECK rejects CHAR-returning functions in =, BETWEEN, IN |
| Audit: DomainCheck_CharacterParameter_ShouldApplyDeclaredWidth | 3 | 0/3/0 | 3/0/0 | Native domain check misses CHAR width on INSERT, CAST and function argument |
| Audit: UnarySign_CharacterCaseResult_ShouldNameCharacterType | 2 | 0/2/0 | 2/0/0 | Runtime + / - errors expose SqlCharacter instead of Character |
| Control: PredicateControl_NonCharacterBounds_ShouldMatchSeparateComparisons | 3 | 3/0/0 | 3/0/0 | None; deliberately passing controls |
| **Total** | **36** | **3/33/0** | **36/0/0** | |

### Does finding 2 predate CHAR?

Not in the requested controls. Against the original assemblies the following expressions and
their explicit `>= AND <=` / `= OR =` equivalents all return FALSE:

```sql
CREATE DOMAIN short_text AS VARCHAR(1);
CREATE TABLE t (id INT); INSERT INTO t VALUES (1);
SELECT 'a ' BETWEEN CAST('a' AS VARCHAR(1)) AND CAST('a' AS TEXT),
       'a ' IN (CAST('b' AS VARCHAR(1)), CAST('a' AS TEXT)) FROM t;
SELECT 'a ' BETWEEN CAST('a' AS short_text) AND CAST('a' AS TEXT),
       'a ' IN (CAST('b' AS short_text), CAST('a' AS TEXT)) FROM t;
SELECT 1.24 BETWEEN CAST(1 AS DECIMAL(2,1)) AND CAST(1.2 AS DECIMAL(3,2)),
       1.24 IN (CAST(2 AS DECIMAL(2,1)), CAST(1.2 AS DECIMAL(3,2))) FROM t;
```

Those comparisons do not assignment-coerce the shared operand to a VARCHAR bound, domain or
DECIMAL scale. The offending mutation was specifically `BindCharacterComparand`; no separate
non-CHAR predicate defect was reproduced. The controls are not counted as regression failures.

## Value, string and storage dispatch sites

Paths in this table are relative to `Assimalign.Cohesion.Database.Sql/src`. Multiple anchors in
one row are individual sites with the same disposition. “Safe” means safe for Character, not a
claim about every unrelated behavior in the method.

| Site | Disposition and reason |
| --- | --- |
| Internal/SqlCharacter.cs:25 | Safe: Input wraps CLR strings only when the accompanying type is Character; existing wrappers pass through. Output unwraps padded Value; ToString deliberately returns trimmed Text. |
| Internal/SqlPlanExecutor.Joins.cs:114 | **Fixed**: retain both string and SqlCharacter as complete probe comparands. Assignment overflow must not suppress collation-equivalent matches. |
| Internal/SqlPlanExecutor.AlterColumn.cs:56 | **Fixed**: object Equals receives unwrapped padded values, not wrapper references. Length-change detection at :39 and deferred classification in Internal/SqlSchemaProvisioner.Deferred.cs:90 include widening. |
| Internal/SqlCastConverter.cs:52,54,110,115,136,138,194 | Safe: numeric parsing accepts string or wrapper and uses trimmed text; ApplyModifiers unwraps before its string cast/pattern, then applies Character first; StorageTypeOf has a Character arm. |
| Internal/SqlPlanExecutor.cs:1476,1480,1489,1492,1497,1500,1504,1507,1510,1513,1516,1519,1522,1525,1528,1531 | Safe: CoerceForColumn unwraps first, preserving Value for CHAR assignment and Text for other targets. Its storage-type switch, numeric string test, `as string` and temporal/UUID string arms therefore receive CLR strings. |
| Internal/SqlValueComparer.cs:25,73,77,123 | Safe: comparison unwraps CHAR to Text before string/GetType/IComparable dispatch; hashing has an explicit SqlCharacter arm using the same collation. |
| ValueObjects/SqlValue.cs:254,258,271,274,278,291,295 | Safe: Character equality/hash use trimmed text and its own type tag; formatting uses padded Value. String casts apply only to String references. Object Equals delegates to the typed value comparison. |
| ValueObjects/SqlValue.cs:343,392,417,467 | Safe: numeric Compare dispatch falls back to SqlValueComparer for Character; the string fast path falls through to an explicit SqlCharacter arm; Box handles only non-reference values (ToObject already returns the Character reference). |
| Internal/Functions/SqlValueConverter.cs:61,111,148 | Safe: typed CLR string shorthands read AsString (which supports Character), produce String, and declare TEXT. Public CLR type mapping does not receive the internal wrapper as T. |
| Internal/SqlMaterializedRow.cs:27,35,38,40 | Safe: byte accessor rejects non-binary values; string accessor has an explicit padded Character arm. GetValue and numeric accessors unwrap via Output. |
| Internal/SqlRowCodec.cs:238,240,255,278,289,318 | Safe: CHAR index normalization trims wrapper Text or raw string; row write/read switches explicitly encode padded Character as a physical string and restore its wrapper. Temporal key-identity default preserves other values. |
| Internal/SqlScriptDataReceipt.cs:45 | Safe: casts read the private receipt table's fixed TEXT/TEXT/INT schema (:15-18), never a user-defined column. |
| Internal/SqlExpressionEvaluator.Binding.cs:172,182,197,444,586 | **Fixed** BETWEEN/IN and shared argument binding. String constant/parameter pattern arms at :172 deliberately create full CHAR comparands without assignment bounds. CharacterToText converts typed CHAR branches; CASE/COALESCE do not mutate a shared operand per alternative. |
| Internal/SqlExpressionEvaluator.cs:642,652,666,668,692,809,810,1032,1085,1121 | **Fixed** OperandTypeName's string-only type naming at :692. Other sites safe: comparisons use SqlValueComparer; concatenation uses Character.ToString's trimmed text; LIKE unwraps the padded input and converts its pattern to text; unary/arithmetic dispatch rejects character operands. |
| Functions/SqlScalarFunction.cs:156; Functions/SqlAggregateAccumulator.cs:110; Internal/SqlExpressionEvaluator.cs:991; Internal/SqlPlanExecutor.Grouping.cs:217 | **Fixed**: same storage type no longer bypasses a declared CHAR width, on single, inline and pooled argument paths. Public Invoke/Add require already-declared argument types; engine resolved paths do the coercion. |
| Internal/Functions/SqlFunctionResolver.cs:263,283,292,204,338 | **Fixed** Coerce applies Character modifiers before the equal-storage early return. Integer conversion switches are numeric-only; result-type rules preserve nonnumeric type identity; TryWiden already applies CHAR return modifiers. |
| Functions/SqlFunction.cs:219,240,265 | Safe: declared Character results deliberately bypass the fast result check and apply their return width. JSON/JSONB representation shortcuts cannot absorb Character. ANYELEMENT preserves the supplied type without inventing a length. |
| Internal/Functions/SqlAbsFunction.cs:31; SqlSumFunction.cs:54; SqlLengthFunction.cs:27 | Safe: ABS/SUM require numeric signatures; LENGTH explicitly branches on Character and counts trimmed Unicode scalars before its String/fallback paths. |
| Internal/SqlPlanExecutor.Grouping.cs:251,293,310 | Safe: group-key Equals calls SqlValueComparer, never wrapper equality. Aggregate normalization changes numeric results only, preserving Character on the default path. |
| Internal/SqlPlanner.Grouping.cs:260,274,362,376 | Safe: literal/unary/numeric-rank switches are metadata, with character common-type handling before ranking. GroupValueType is used only for public parameter CLR values; expression results get their declared Character type. |
| Internal/SqlExpressionEvaluator.Functions.cs:157,187 | Safe: literals are initially String; columns/casts/functions/subqueries get Character from metadata. Unary numeric promotion preserves other type tags for validation. |
| Internal/SqlPlanner.cs:552,1097,1207 | Safe: Character is range-sargable; LIMIT/OFFSET accept integers only; cast validation remaps only JSON/JSONB and otherwise preserves the declared type. |
| Internal/SqlPlanner.InsertSelect.cs:77 | Safe: assignment compatibility explicitly includes Character in the character family, followed by ordinary column modifier enforcement. |
| Internal/SqlPlanExecutor.Constraints.cs:1204,1205 | **Fixed**: CHECK application-function comparisons recognize Character alongside String/JSON. Previously equality, BETWEEN and IN were rejected before evaluation. |
| Internal/SqlDeclaredDatabase.Scripts.cs:219 | **Fixed**: historical signature storage range includes Character=19 instead of ending at JsonBinary=18. |
| Internal/SqlPlanExecutor.SystemViews.cs:310,334,346,354,361 | Safe: type-name switch has CHARACTER; numeric/temporal metadata switches return null for Character. Character maximum length is explicitly reported at :97. |
| Internal/SqlSchemaStatementRenderer.cs:217 | Safe: Character has its own CHAR(length) rendering arm. |

Related non-value switches in SQL: evaluator/binder/planner, persisted-expression and grouping
expression switches dispatch AST kinds/operators; executor switches dispatch plans; definition,
script and schema switches dispatch DDL kinds; server switches dispatch protocol frames and
exceptions. None classify a wrapper by CLR string identity. String comparisons of SQL names,
signatures, canonical expression text, hashes and identifiers have statically typed string inputs.
`GetType` in cast errors and unsupported-node errors is diagnostic only; value conversion is done
before those errors (the unary-sign diagnostic inconsistency was fixed above).

## Every reference-identity site

All sites below are safe for Character: they compare the specified non-value objects. The only
object-equality site comparing physical row values was ALTER COLUMN, fixed above. SqlValue's
object override and group-key comparer are also covered above.

| File under SQL/src | Lines | Objects compared |
| --- | --- | --- |
| SqlDatabaseSession.cs | 224, 271, 312 | Translated exception and metrics snapshots |
| SqlDatabaseTransaction.cs | 126, 151, 181 | Translated exceptions |
| SqlDatabaseEngine.cs | 398, 1003 | Declaration array CAS and tracked database |
| Functions/SqlFunction.cs | 83, 87, 89, 91, 105, 256 | Pseudo-type singleton descriptors |
| Functions/SqlType.cs | 358, 385 | Type descriptor identity shortcuts; Equals(object) at :368 delegates to descriptor equality |
| Functions/SqlTypeCollection.cs | 87, 113 | Registered domain descriptors |
| Internal/Functions/SqlFunctionResolver.cs | 108, 112, 190, 393, 402, 436 | Pseudo-type descriptors |
| Internal/Functions/SqlExpressionFunction.cs | 53 | Bound-body collation descriptor |
| Internal/SqlBoundTable.cs | 76 | Constraint descriptor |
| Internal/SqlBoundTableCache.cs | 76, 157, 190 | Pending migration, constraint and column descriptors |
| Internal/SqlPlanner.Grouping.cs | 194, 195 | Subquery/EXISTS AST nodes (structural comparer at :238 uses AST equality) |
| Internal/SqlPlanExecutor.Constraints.cs | 265, 303, 1137 | Constraint, catalog definition and Boolean type descriptor |
| Internal/SqlSchemaProvisioner.cs | 58, 59 | Already-validated state document instances |
| Internal/SqlSchemaProvisioner.Tracing.cs | 52 | Script step descriptor |
| Internal/SqlExpressionEvaluator.Functions.cs | 369 | Evaluator instance |
| Internal/SqlExpressionEvaluator.cs | 712 | Bound BETWEEN operand nodes, to evaluate a shared node once |

## Adjacent SQL roots and physical codecs

Paths below are relative to `resources/Database`; no edits were necessary at these sites.

| Site | Why safe for Character |
| --- | --- |
| Sql.Schema/shared/SqlDefaultLiteralRenderer.cs:25,71 | Runtime default value/type mapping takes public CLR values; rendering explicitly accepts String or Character. No internal wrapper crosses this assembly boundary. |
| Sql.Schema/src/SqlSchemaMigrationPlanner.cs:302; SqlTableBuilder.cs:265 | Reference identity is compiled constraints / selector expression nodes, not row values. |
| Sql.Schema/src/Internal/SqlCompiledSchemaValidator.cs:471,517 | Enum.IsDefined accepts Character; remaining enum checks validate constraint, action and permission kinds. |
| Sql.Catalog/src/SqlCatalog.cs:1291,2605,2617 | Catalog object-kind switch and identifier tuple comparers; no runtime SQL values. |
| Sql.Language/shared/SqlTypeModifiers.cs:26 | Type-name metadata resolution explicitly includes CHAR/Character and shares the maximum-length rule. Language source switches handle syntax, token kinds and literal rendering. |
| Sql.Client/src/Internal/SqlExecuteExchange.cs:46; Exceptions/SqlClientException.cs:71 | Frame and error-code dispatch. Payloads are public CLR values plus metadata, not SqlCharacter. |
| Sql.Storage/src | No matching string/object-identity/value-type dispatch. Model values are encoded by SqlRowCodec before storage. |
| Types/src/DatabaseValueCodec.cs:34,66 | Physical tuple writer/reader intentionally use CLR strings and physical String tags. SQL's public row boundary unwraps CHAR; its column header carries Character. |
| Types/src/DatabaseKeyReader.cs:50 | Physical-tag switch: CHAR uses String key encoding after SQL normalizes its text, so a separate Character physical tag is neither emitted nor needed. |
| Types/src/Collation.cs:60,181 | Collation-id switch and environment string configuration, not SQL values. |
| Indexing/src/ValueObjects/IndexKey.cs:117,120 | Key equality compares encoded byte content. |
| Indexing/src/BTreeIndex.cs:767,1348; Internal/BTreeEntryOrder.cs:211; Internal/BTreeNode.cs:258,403 | Tiebreaker/search-kind switches over normalized keys, not SQL storage types. |
| Database/shared/DatabaseDeclarations.cs:63; DatabaseMemoryFiles.cs:162; DatabaseEngineBuilderState.cs:398,406 | Reference identity of declaration arrays, file handles, workers and servers. |
| Database/shared/DatabaseCheckpointWorker.cs:293 | Checkpoint failure-kind switch, not a row value. |

The prefixes `Sql`, `Sql.Schema`, `Types`, etc. abbreviate `Assimalign.Cohesion.Database.<suffix>`;
`Database/shared` is the `Assimalign.Cohesion.Database/shared` directory.
