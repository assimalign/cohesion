# CHAR review rounds 1 and 2 (#1437)

Round 1 follows `a7ee6b499`; round 2 follows `4d45d8dd5`. The round-1 evidence is historical:
its IN expectations were partly incorrect, as corrected below. The dispatch inventory includes
the three safe sites added by the second review. It covers production `src` and `shared` code in every SQL child
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
  `src/backend/parser/parse_expr.c:1325-1341`. This was correct in round 1 and needs no change.
  IN separates column-reference candidates for independent comparisons (`:1163-1176,1253-1295`).
  Only more than one non-column candidate gets a common type with the left operand first
  (`:1182-1190`). The first non-unknown CHAR/TEXT/VARCHAR wins, not TEXT unconditionally:
  `parse_coerce.c:1397-1440` replaces the running candidate only for a one-way implicit cast,
  but `src/include/catalog/pg_cast.dat:313-325` makes all these pairs implicit both ways.
  All-unknown inputs select TEXT (`parse_coerce.c:1444-1456`). Common coercion applies typmod -1
  (`:1583-1585`), so no operand's declared width pads or truncates other comparands. VARCHAR and
  TEXT share String identity here; untyped string parameters follow the literal rule. The IN
  common group evaluates its left operand once; independent comparisons evaluate it separately.
  The existing once-per-row evaluation of a shared BETWEEN operand is preserved.
- ALTER COLUMN remains validation-only. Every CHAR length change is validated, and any change
  to existing padding is rejected before catalog publication. Empty/all-null columns can change
  width. Code-first validation of populated tables uses the existing deferred provisioning worker.
  This respects #1447, which owns table rewrites using #1265's machinery; silently changing only
  metadata or introducing a second rewrite implementation would not respect that contract.
  Both rejection paths now name the column and recommend an explicit table-copy migration,
  with #1447 identified as the in-place rewrite work. This remedy is a Cohesion policy, not a
  PostgreSQL diagnostic: PostgreSQL implements `ATRewriteTables` in `tablecmds.c:5897`.
- Physical equality in ALTER compares the full padded strings with ordinal equality. SQL equality,
  which ignores trailing spaces, cannot establish that a rewrite is unnecessary.
- Formats stay at record-space 12, schema v6, result-header 2. They are unreleased, and no additional
  format change is needed for these fixes. Historical signature decoding now admits Character (19).

## Round-2 expectation sources

All PostgreSQL paths in this section are relative to the read-only clone
`C:\Source\repos\postgres\postgres`. These sources establish the expectations independently
of the baseline/fixed results. The user-supplied rule is implemented without changing the
separate, documented CASE/COALESCE dialect behavior.

| Key | Source and established rule |
| --- | --- |
| A | `src/backend/parser/parse_expr.c:1163-1176,1182-1190,1248-1295`: exclude expressions containing level-0 column references; only more than one remaining candidate shares a common type, with the left operand first; bind other candidates with separate operators and OR/AND. |
| B | `src/backend/parser/parse_coerce.c:1397-1440` and `src/include/catalog/pg_cast.dat:313-325`: ignore unknowns, and retain the first known string type because replacement requires a one-way implicit cast and each CHAR/TEXT/VARCHAR pair permits both directions. `parse_coerce.c:1444-1456` selects TEXT for all-unknown inputs. |
| C | `src/backend/utils/adt/varchar.c:673-690,746-776` strips trailing U+0020 for CHAR equality; `src/backend/utils/adt/varlena.c:1429-1480` compares full TEXT lengths/bytes under deterministic collation. `pg_cast.dat:318-325` converts CHAR to TEXT/VARCHAR through trimming text conversion. |
| D | `src/backend/parser/parse_oper.c:272-286`: an unknown operand takes its known comparison partner's type. Cohesion's untyped string parameters follow this literal rule, as before this round. |
| E | `src/backend/parser/parse_coerce.c:1583-1585`: common coercion supplies typmod -1, so no candidate's declared width truncates or pads another value. |
| F | `src/backend/executor/execExprInterp.c:4054-4074,4165-4185`: strict NULL input and nonmatching NULL candidates preserve UNKNOWN; a deciding match overrides UNKNOWN. `parse_expr.c:1282-1295` combines independent terms with OR for IN and AND for NOT IN. |
| G | `src/backend/parser/parse_expr.c:1325-1341`: BETWEEN and NOT BETWEEN expand to separately bound comparisons. Round 1 already matches this; no code or expectation changes. |

`SqlCharacterBoundaryTests` cites these sources beside the expectations. For the compact matrix
below, `C(s)` means `CAST(s AS CHAR(3))`, `T(s)` means `CAST(s AS TEXT)`, and `V(s)` means
`CAST(s AS VARCHAR(3))`; quotes and trailing spaces are significant. Every non-null IN expectation
also asserts its opposite for NOT IN. The mixed-candidate test repeats the literal operand with
the existing untyped string parameter rule (D).

| Test / left operand | Candidates | Expected IN | Source keys |
| --- | --- | --- | --- |
| Mixed / `'a '` | `C('b'), T('a')` | **TRUE, corrected from FALSE** | A, B, C, D |
| Mixed / `'a '` | `T('a'), C('b')` | FALSE, retained | A, B, C, D |
| Mixed / `'a '` | `C('a'), T('b')` | **TRUE, corrected from FALSE** | A, B, C, D |
| Mixed / `'a '` | `T('b'), C('a')` | FALSE, retained | A, B, C, D |
| Mixed / `'a '` | `C('a'), V('b')` | TRUE | A, B, C, D |
| Mixed / `'a '` | `V('b'), C('a')` | FALSE | A, B, C, D |
| Mixed / `'a '` | `V('a'), C('b')` | FALSE | A, B, C, D |
| Mixed / `'a '` | `C('b'), T('a ')` | TRUE | A, B, C, D |
| Mixed / `'a '` | `'b', C('a'), T('d')` | TRUE; initial unknown is skipped | A, B, C, D |
| Typed / `C('a')` | `'a ', T('b')` | TRUE | A, B, C |
| Typed / `C('a')` | `'a ', V('b')` | TRUE | A, B, C |
| Typed / `T('a ')` | `C('a'), 'b'` | FALSE | A, B, C |
| Typed / `V('a ')` | `C('a'), 'b'` | FALSE | A, B, C |
| Typed / `C('a')` | `T('a '), T('b')` | TRUE; typed TEXT also acquires CHAR comparison identity | A, B, C, E |
| Typed / `C('abc')` | `T('abc '), 'other'` | TRUE | A, B, C, E |
| Typed / `C('abc')` | `T('abcd'), 'other'` | FALSE; no inherited CHAR(3) truncation | A, B, C, E |
| Separate groups / `'a '` | `c, T('b'), T('d')` | TRUE; `c = C('a')` stays independent | A, C, D |
| Separate groups / `'a '` | `CAST(c AS TEXT), C('a'), T('b')` | TRUE; the column expression cannot select the non-column group's type | A, B, C |
| Singleton / `C('a')` | `T('a ')` | FALSE; only one non-column candidate | A, C |
| One non-column / `C('b')` | `c, T('b ')` | FALSE; `c = C('a')`, both comparisons independent | A, C |

The five column-candidate cases use `c`, `CAST(c AS CHAR(3))`, `char_echo(c)`, `c COLLATE binary`
and `CASE WHEN TRUE THEN c ELSE C('b') END`, where `c` contains `'a'`. Each expects
`[TRUE, TRUE, TRUE, FALSE, TRUE]` for IN, the explicit OR expansion, reversed candidate order,
NOT IN and parameter IN. A, C and D establish each result; a nested reference is still a Var
candidate and the TEXT candidate never changes that comparison's operator.

The all-unknown/NULL test expects, in query order, `[FALSE, TRUE, NULL, NULL, NULL, TRUE, FALSE,
NULL, NULL]`: `'a ' IN ('a','b')`, `'a ' IN ('a ','b')`, IN and NOT IN against `('a',NULL)`,
`NULL IN ('a','b')`, IN and NOT IN for `'a '` against `(c,'b',NULL)`, and IN and NOT IN for
`'b'` against `(c,'d',NULL)`. B and C establish text-space significance, A and D establish the
independent CHAR match, and F establishes every NULL/negation result.

The two ALTER diagnostics test the definition-rewrite path (`CHAR(4) NOT NULL DEFAULT 'b'`)
and changed-padding path (`CHAR(4)`) on populated `c CHAR(3)`. Both must name `ALTER COLUMN 'c'`,
recommend an `explicit table-copy migration`, and identify `#1447`. These three assertions come
from finding 3 and the owner's rewrite scope, not PostgreSQL: PostgreSQL performs table rewrites
in `src/backend/commands/tablecmds.c:5897` (`ATRewriteTables`). Existing data-preservation and
nullability tests remain unchanged.

### Round-2 baseline matrix

The original round-1 Release output was rebuilt at `4d45d8dd5` before editing source and saved
under `_out/char-review-r2/baseline-original/net10.0`; `baseline-original/SHA256.json` records
every DLL hash. `baseline-run/net10.0` contains a copy with only the SQL test DLL replaced by
the expanded test assembly. Production DLL hashes are checked against that saved inventory.
Logs and TRX files are under `_out/char-review-r2`, with the same 35-case filter for both runs.
Both runs use Release and `DOTNET_TieredCompilation=0`; baseline additionally passes
`--no-build --no-restore -p:OutputPath=C:\cw\1437\_out\char-review-r2\baseline-run\net10.0`.

| Test method in SqlCharacterBoundaryTests | Cases | Baseline P/F/S | Fixed P/F/S |
| --- | ---: | --- | --- |
| In_MixedCharacterCandidates_ShouldUseFirstKnownType | 9 | 5/4/0 | 9/0/0 |
| In_TypedCharacterOperand_ShouldUseFirstKnownType | 7 | 3/4/0 | 7/0/0 |
| In_ColumnReferenceCandidate_ShouldMatchSeparateComparisons | 5 | 0/5/0 | 5/0/0 |
| In_SeparatedCandidateGroups_ShouldResolveComparisonsIndependently | 4 | 2/2/0 | 4/0/0 |
| In_AllUnknownCandidates_ShouldUseTextAndPreserveNulls | 1 | 1/0/0 | 1/0/0 |
| AlterColumn_CharacterRewrite_ShouldNameColumnAndMigration | 2 | 0/2/0 | 2/0/0 |
| Between_MixedCharacterBounds_ShouldMatchSeparateComparisons (unchanged) | 4 | 4/0/0 | 4/0/0 |
| PredicateControl_NonCharacterBounds_ShouldMatchSeparateComparisons (unchanged) | 3 | 3/0/0 | 3/0/0 |
| **Total** | **35** | **18/17/0** | **35/0/0** |

The passing baseline cases deliberately retain correct behavior. In particular, passing the
old CHAR-first tests could not establish conformance: their expectations were wrong. The two
corrected rows above change IN and parameter IN from FALSE to TRUE, and NOT IN from TRUE to FALSE,
because CHAR appears before TEXT among the non-unknown inputs. DESIGN and DIALECT now say this too.

## Round-1 baseline evidence (superseded IN expectations)

A failing pre-fix baseline demonstrates test sensitivity, not correctness of its expectations.
The two CHAR-first cases in the old `In_MixedCharacterCandidates_ShouldKeepSignificantTextSpaces`
test incorrectly expected `[FALSE, TRUE, FALSE]`; they now expect `[TRUE, FALSE, TRUE]`.
The TEXT-first cases still expect `[FALSE, TRUE, FALSE]`. The historical IN row and totals below
record what was run, not a valid PostgreSQL-conformance result. The round-2 matrix replaces that
claim with source-cited expectations.

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

`2351BFC25E999B10CD3375B7860B49F33E80F30CAB8B636E652E03E9127F587B` (SHA-256).

| Finding / test method in SqlCharacterBoundaryTests (unless stated) | Cases | Baseline P/F/S | Fixed P/F/S | Baseline failure |
| --- | ---: | --- | --- | --- |
| 1: Join_AccentInsensitiveCharacterKeys_ShouldReturnSameRowsAfterIndexing | 1 | 0/1/0 | 1/0/0 | Seek loses the scan's matching row |
| 2: Between_MixedCharacterBounds_ShouldMatchSeparateComparisons | 4 | 0/4/0 | 4/0/0 | Shared CHAR coercion changes the TEXT comparison; literal and parameter, either bound |
| 2: In_MixedCharacterCandidates_ShouldKeepSignificantTextSpaces | 4 | 0/4/0 | 4/0/0 | Historical assertions selected TEXT in either order; the two CHAR-first expectations were wrong and are superseded above |
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
| Internal/Expressions/SqlBoundExpression.In.cs:53 | Safe: common CHAR groups wrap constant/parameter strings once when bound; this does not apply a length modifier or change the operand used by independent comparisons. Computed strings acquire the same identity in EvaluateIn. |
| Internal/SqlPlanExecutor.Joins.cs:114 | **Fixed**: retain both string and SqlCharacter as complete probe comparands. Assignment overflow must not suppress collation-equivalent matches. |
| Internal/SqlPlanExecutor.AlterColumn.cs:56 | **Fixed**: object Equals receives unwrapped padded values, not wrapper references. Length-change detection at :39 and deferred classification in Internal/SqlSchemaProvisioner.Deferred.cs:90 include widening. |
| Internal/SqlCastConverter.cs:52,54,110,115,136,138,194 | Safe: numeric parsing accepts string or wrapper and uses trimmed text; ApplyModifiers unwraps before its string cast/pattern, then applies Character first; StorageTypeOf has a Character arm. |
| Internal/SqlPlanExecutor.cs:1476,1480,1489,1492,1497,1500,1504,1507,1510,1513,1516,1519,1522,1525,1528,1531 | Safe: CoerceForColumn unwraps first, preserving Value for CHAR assignment and Text for other targets. Its storage-type switch, numeric string test, `as string` and temporal/UUID string arms therefore receive CLR strings. |
| Internal/SqlPlanExecutor.cs:254 | Safe: ORDER BY's tuple-pattern switch handles null placement only; every non-null pair, including Character, delegates to SqlValueComparer with the resolved collation. Added in round 2. |
| Internal/SqlPlanExecutor.Defaults.cs:251 | Safe: only binary and decimal defaults take special parsing paths; Character defaults pass through CoerceForColumn, which applies padding and length. Added in round 2. |
| Internal/SqlValueComparer.cs:25,73,77,123 | Safe: comparison unwraps CHAR to Text before string/GetType/IComparable dispatch; hashing has an explicit SqlCharacter arm using the same collation. |
| ValueObjects/SqlValue.cs:254,258,271,274,278,291,295 | Safe: Character equality/hash use trimmed text and its own type tag; formatting uses padded Value. String casts apply only to String references. Object Equals delegates to the typed value comparison. |
| ValueObjects/SqlValue.cs:343,392,417,467 | Safe: numeric Compare dispatch falls back to SqlValueComparer for Character; the string fast path falls through to an explicit SqlCharacter arm; Box handles only non-reference values (ToObject already returns the Character reference). |
| Internal/Functions/SqlValueConverter.cs:61,111,148 | Safe: typed CLR string shorthands read AsString (which supports Character), produce String, and declare TEXT. Public CLR type mapping does not receive the internal wrapper as T. |
| Internal/SqlMaterializedRow.cs:27,35,38,40 | Safe: byte accessor rejects non-binary values; string accessor has an explicit padded Character arm. GetValue and numeric accessors unwrap via Output. |
| Internal/SqlRowCodec.cs:238,240,255,278,289,318 | Safe: CHAR index normalization trims wrapper Text or raw string; row write/read switches explicitly encode padded Character as a physical string and restore its wrapper. Temporal key-identity default preserves other values. |
| Internal/SqlScriptDataReceipt.cs:45 | Safe: casts read the private receipt table's fixed TEXT/TEXT/INT schema (:15-18), never a user-defined column. |
| Internal/SqlExpressionEvaluator.Binding.cs:173,183,198,408,616 | **Fixed** BETWEEN/IN and shared argument binding. String constant/parameter pattern arms at :173 deliberately create full CHAR comparands without assignment bounds. Round 2 separates IN candidates and resolves the non-column group's identity before evaluation. CharacterToText converts typed CHAR branches; CASE/COALESCE do not mutate a shared operand per alternative. |
| Internal/SqlExpressionEvaluator.cs:642,652,666,668,692,739,749,813,814,1034,1087,1123 | **Fixed** OperandTypeName's string-only type naming at :692. Common IN groups at :739 and :749 apply their selected CHAR identity to every string comparand through SqlCharacter.Input. Other sites safe: comparisons use SqlValueComparer; concatenation uses Character.ToString's trimmed text; LIKE unwraps the padded input and converts its pattern to text; unary/arithmetic dispatch rejects character operands. |
| Functions/SqlScalarFunction.cs:156; Functions/SqlAggregateAccumulator.cs:111; Internal/SqlExpressionEvaluator.cs:993; Internal/SqlPlanExecutor.Grouping.cs:217 | **Fixed**: same storage type no longer bypasses a declared CHAR width, on single, inline and pooled argument paths. Public Invoke/Add require already-declared argument types; engine resolved paths do the coercion. |
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
| Sql.Client/src/SqlRow.cs:219 | Field<T> consumes the public wire values: Character arrives as a padded CLR string plus column metadata, so its generic `is T` and numeric-conversion paths never receive SqlCharacter. Added in round 2. |
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

## Round-1 gates and historical checkpoint race

The second-review handoff confirms the main session already fixed this checkpoint race on the
integration branch. It is not a round-2 follow-up; the evidence below records the original run.

Build tasks succeeded in Debug and Release. All builds used `MSBUILDDISABLENODEREUSE=1`;
the Release SQL runs used `DOTNET_TieredCompilation=0`. Results below are Passed/Failed/Skipped.
Each test invocation names its test csproj. Logs and TRX files are under `_out/char-review-r1`.

| Suite | Debug | Release |
| --- | --- | --- |
| Database.Sql | 2783/0/0 | 2783/0/0 |
| Database.Sql.Language | 1722/0/0 | 1722/0/0 |
| Database.Sql.Schema | 362/0/0 | 362/0/0 |
| Database.Sql.Catalog | 120/0/0 | 120/0/0 |
| Database.Sql.Storage | 14/0/0 | 14/0/0 |
| Database.Indexing | 90/0/0 | 90/0/0 |
| Database.Sql.Client | 1120/0/0 | 1120/0/0 |
| Database.Client | 69/0/0 | 69/0/0 |
| Database.Hosting | 130/0/0 | 130/0/0 |
| Database (root) | 186/0/0 | 186/0/0 |
| Database.Types | 93/0/0 | 93/0/0 |
| Sdk.Database | 342/0/0 | 342/0/0 |

The final full SQL runs pass in both configurations. The initial SQL Debug run, before adding
the two diagnostic regressions, was 2780/1/0; the initial Release run was 2782/1/0.
The only failing original test in either initial full SQL run was
`SqlTransactionRollbackTests.RollbackAsync_UndoDeferred_ShouldHoldLocksUntilThePurgePassAndKeepCheckpointsRunning`.
Its final checkpoint raises `StorageTransactionException: Checkpoint requires no active transactions`.
No test was removed, skipped, weakened or altered to conceal that failure.

The untouched original Release assemblies **and original test DLL** pass 2747/0/0. With the
expanded test DLL and the same original production assemblies, the full suite is **2749/34/0**:
the 33 proven CHAR regression failures plus this same rollback/checkpoint failure. The rollback
test also passed three isolated baseline runs (1/0/0 each), confirming that the failing schedule
is not deterministic. The initial expanded full-suite baseline and fixed Release runs used
identical test DLLs with SHA-256
`B0BF40C1D3F593C6A1ABA108603EA555BE1175AFF151A910F0EC998F8BBEEEEE`.

The test's `QuietOptions` (`tests/SqlTransactionRollbackTests.cs:212`) sets maintenance and
checkpoint intervals to one hour, but `src/Internal/SqlVersionPurgeWorker.cs:115` caps its wait
at 25 ms when table reclamation has work. Reclamation opens storage transactions outside the
coordinator apply gate (`src/Internal/SqlTableReclamation.cs:90-97`). After DROP completes, the
test's checkpoint at `tests/SqlTransactionRollbackTests.cs:170` can overlap that work;
`Database.Storage/src/Storage.cs:1570` explicitly refuses an active storage transaction.
This unchanged INT-only path is outside the CHAR fixes. Follow-up recommendation: coordinate
or quiesce table reclamation in this rollback test before asserting an idle-storage checkpoint;
do not weaken the storage checkpoint invariant.

The SQL NativeAOT harness published for Release/win-arm64 and passed **23/0/0** smoke checks,
in process and over the wire, with **zero IL2xxx/IL3xxx diagnostics**. No installer,
Database.Testing suite or benchmarks were run.

## Round-2 final validation

The round-2 behavior, regression tests and source-cited documentation are committed as `295e3a8ec`,
following `4d45d8dd5`. Validation completed on 2026-10-16. Build tasks succeeded in Debug and
Release. Builds used `MSBUILDDISABLENODEREUSE=1`; all Release SQL tests used
`DOTNET_TieredCompilation=0`. Every test invocation named its test csproj.

The final baseline and fixed regression runs used the **same** expanded Release test DLL:

`A702C2F4F797C06F1EE75D9335920ACF62BC8D5A07006A3141D7D0DD16D3961E` (SHA-256).

All 79 original DLLs still match `baseline-original/SHA256.json`; all 78 non-test DLLs in
`baseline-run/net10.0`, including 57 Cohesion production DLLs, also match that inventory. Only
the test DLL was replaced. The final TRX files are `results/baseline-final.trx` (**18/17/0**) and
`results/fixed-final.trx` (**35/0/0**) under `_out/char-review-r2`. Their method-level counts match
the round-2 baseline matrix above. The filter is
`FullyQualifiedName~SqlCharacterBoundaryTests.In_|FullyQualifiedName~SqlCharacterBoundaryTests.Between_|FullyQualifiedName~SqlCharacterBoundaryTests.PredicateControl_|FullyQualifiedName~SqlCharacterBoundaryTests.AlterColumn_CharacterRewrite_`.
`final-baseline-evidence.json` records the hash and verification counts; the corresponding
`baseline-final-cases.json` and `fixed-final-cases.json` retain individual results.

Full gates, Passed/Failed/Skipped:

| Suite | Debug | Release |
| --- | --- | --- |
| Database.Sql | 2807/0/0 | 2807/0/0 |
| Database.Sql.Language | 1722/0/0 | 1722/0/0 |
| Database.Sql.Schema | 362/0/0 | 362/0/0 |
| Database.Sql.Catalog | 120/0/0 | 120/0/0 |
| Database.Sql.Storage | 14/0/0 | 14/0/0 |
| Database.Indexing | 90/0/0 | 90/0/0 |
| Database.Sql.Client | 1120/0/0 | 1120/0/0 |
| Database.Client | 69/0/0 | 69/0/0 |
| Database.Hosting | 130/0/0 | 130/0/0 |
| Database (root) | 186/0/0 | 186/0/0 |
| Database.Types | 93/0/0 | 93/0/0 |
| Sdk.Database | 342/0/0 | 342/0/0 |
| **Total** | **7055/0/0** | **7055/0/0** |

The final source adjustment prepares constant/parameter CHAR comparands once during binding.
After it, Database.Sql was rerun in both configurations, plus Database.Sql.Client, Database.Client,
Database.Hosting, Database and Sdk.Database in Debug; all counts were unchanged. The Release
consumer gates already used the final SQL implementation: each of their SQL DLL hashes matches
the final Release SQL test output (`release-consumer-hashes.json`). `verified-gates.json` merges
the full run (`gates.json`) and those final rechecks (`final-gates.json`); all logs and TRX files
remain under `_out/char-review-r2`.

The final SQL NativeAOT harness published for Release/win-arm64 and passed **23/0/0** smoke
checks, in process and over the wire, with **zero IL2xxx/IL3xxx diagnostics**. See
`aot-publish.log`, `aot-smoke.log` and `aot-result.json`. No installer, Database.Testing suite or
benchmarks were run. All test, publish and smoke processes exited; no worktree test/build process
was left running. The already-resolved integration checkpoint race was not changed.
