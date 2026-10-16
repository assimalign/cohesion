# CHAR review round 3: binding provenance (#1437)

Base: `03a3dc6c1bf6531332a1562f54b543c17556b2f6`. This round fixes the third review's
function-parameter/domain-VALUE IN classification defect. Earlier round evidence remains in
[CHAR_REVIEW_AUDIT.md](CHAR_REVIEW_AUDIT.md).

## Binding change

`SqlBindingContext` records Table, Function or Domain when the binder creates the expression
scope. Function declaration, function body preparation (including every invocation collation),
domain declaration and the synthetic domain-check function on reload all provide their context.
Ordering-scope copies preserve it. Table CHECK and ordinary statement scopes retain Table.

IN now asks `SqlExpressionEvaluator.ReferencesTableColumn`. There is no callable syntax-only
`SqlPlanner.ReferencesAnyColumn` left. The instance query uses the binding context before walking
table-scope expression operands, so function arguments and VALUE cannot be mistaken for table
references merely because they use `SqlColumnReferenceExpression` and `SqlCatalogColumn` slots.
The seek-bound caller uses the same scope query over its actual table. No source name, spelling,
qualifier or AST pattern is used to infer whether a scope is a function or a domain.

The slot representation remains useful for names, declared types, domain identities, lengths,
invocation collations and row-array ordinals. Its reads remain invocation-dependent and cannot
be constant-folded when preparing a cached function body. No public surface, project reference,
internal visibility grant, storage/index format, schema version or wire format changes.

## PostgreSQL expectation sources

All paths below are relative to `C:\Source\repos\postgres\postgres`, read-only checkout
`85f55534e80b633bdb7bfc91eaa5793021d6bf66`. Expectations are derived from that source, not from
the failing or fixed Cohesion implementation. Cohesion diagnostic wording and access-path labels
are local contracts; the citations establish the SQL result or constraint rejection.

| Key | Source and rule |
| --- | --- |
| A | `src/backend/executor/functions.c:491-505`: SQL arguments become typed Param nodes; invocation collation may override their type collation. `src/backend/commands/typecmds.c:3603-3618`: VALUE becomes CoerceToDomainValue of the base type. Neither is a table Var. |
| B | `src/backend/parser/parse_expr.c:1163-1190,1248-1288`: partition candidates by contained level-0 Vars; more than one non-Var candidate shares a type selected with the left operand first; Var candidates compare independently, combined with OR/AND. |
| C | `src/backend/parser/parse_coerce.c:1397-1456` and `src/include/catalog/pg_cast.dat:313-325`: skip unknown inputs and retain the first known CHAR/TEXT/VARCHAR type because their implicit casts work both ways; all-unknown chooses TEXT. Common coercion has typmod -1 (`parse_coerce.c:1583-1585`). |
| D | `src/backend/utils/adt/varchar.c:673-690,746-776,912-930`: CHAR comparison removes trailing spaces. `src/backend/utils/adt/varlena.c:1355-1421,1429-1480`: TEXT comparison includes full length and trailing spaces. `pg_cast.dat:318-325` uses text(bpchar); `src/include/catalog/pg_proc.dat:1176-1178` maps that conversion to rtrim1, `src/backend/utils/adt/oracle_compat.c:787-797`. |
| E | `src/backend/parser/parse_oper.c:272-286`: a binary operator's unknown input takes its known partner's type. `src/backend/parser/parse_expr.c:1325-1341`: BETWEEN becomes separately resolved comparisons. |
| F | `src/backend/executor/execExprInterp.c:4067-4074,4149-4185`: strict comparison and NULL candidates preserve UNKNOWN unless another candidate decides IN/NOT IN. |
| G | `src/backend/executor/execExprInterp.c:4498-4508`: domain CHECK rejects FALSE, accepts NULL. `src/backend/executor/execMain.c:1889-1904,2131-2136`: table CHECK follows the same NULL rule and rejects failing writes. Domain conversion is installed by `src/backend/parser/parse_coerce.c:699-723`. |
| H | `src/backend/rewrite/rewriteHandler.c:1277-1331`: obtain a column's DEFAULT expression and coerce it to its target type. `src/backend/executor/execExpr.c:1813-1879`: CASE chooses the matching WHEN result or ELSE. |
| I | `src/backend/utils/adt/varchar.c:130-190,219-227`: CHAR input pads to the declared width and output retains the stored padding. |
| J | `src/backend/parser/parse_expr.c:1740-1763,2244-2255`: CASE and COALESCE select/coerce their common result type; a CHAR value with unknown NULL retains CHAR. `src/backend/executor/execExpr.c:2188-2208` short-circuits COALESCE at its first non-NULL argument. `src/backend/nodes/nodeFuncs.c:191-195` preserves the type through COLLATE. `parse_expr.c:2777-2781` applies the explicit CAST target type; `src/backend/parser/parse_func.c:797` assigns a call's declared return type. |

## New regression expectations and baseline matrix

`SqlCharacterBindingTests` contains the following cases. P/F/S means Passed/Failed/Skipped.
The two original reviewer examples are the direct `p`, TEXT-first function case and
`In_DomainValue_ShouldRejectReviewWrite` (the review's SQL verbatim).

| Test method | Cases and source-derived expectation | Sources | Baseline P/F/S | Fixed P/F/S |
| --- | --- | --- | --- | --- |
| In_FunctionParameter_ShouldUseCommonType | 12: `p`, `CAST(p AS CHAR(3))`, `char_echo(p)`, `p COLLATE binary`, `CASE WHEN TRUE THEN p ELSE NULL END`, `COALESCE(p,NULL)`, each before/after TEXT `'b'`. TEXT-first IN over `'a'` is FALSE; CHAR-first TRUE. `'b'` is FALSE; NULL is UNKNOWN; NOT IN is the inverse/UNKNOWN. Constant calls and calls over three different rows agree. | A–F, H, J | 6/6/0 | 12/0/0 |
| In_DomainValue_ShouldRejectReviewWrite | 1: `'a ' IN (text_b(),VALUE)` rejects inserting `'a'` into the CHAR domain; no row is stored. | A–D, G | 0/1/0 | 1/0/0 |
| In_DomainValueCallers_ShouldRejectInvalidValue | 4: the same domain rejects UPDATE, omitted function-backed DEFAULT, function argument and CAST. A NULL control row remains unchanged. | A–D, F–H | 0/4/0 | 4/0/0 |
| In_TableCheck_ShouldUseEachBindingScope | 3: CHECK calling the TEXT-first parameter function rejects `'a'`; direct `c` and `char_echo(c)` candidates in the table's CHECK accept it via independent CHAR comparison. Stored output is `'a  '`. | A–E, G, I | 2/1/0 | 3/0/0 |
| In_FunctionDefault_ShouldStoreFalse | 1: direct and nested SQL function DEFAULTs store FALSE for both inserted rows; nested SELECT also returns FALSE. | A–D, H | 0/1/0 | 1/0/0 |
| Between_DomainValue_ShouldBindComparisonsIndependently | 4: VALUE/`char_echo(VALUE)` as the lower bound and TEXT `'a'` as upper reject `'a'`; reversing the bounds accepts `'a'`, returned padded. | A, D, E, G, I | 4/0/0 | 4/0/0 |
| In_TableFunctionAndDomainCandidates_ShouldKeepBindingOrigins | 1: IN candidates are TEXT `'b'`, a SQL CHAR-parameter result, a checked CHAR-domain cast, and table `c`. Non-table group is FALSE; table `'a'` decides TRUE and table `'b'` FALSE. NOT IN reverses that. A real table column named VALUE behaves like `c`. | A–G, H, J | 0/1/0 | 1/0/0 |
| In_FunctionSeekBound_ShouldMatchScan | 1: the false function result makes CASE return 2; both scan and seek return id 2. | A–D, H | 0/1/0 | 1/0/0 |
| Open_FunctionAndDomainChecks_ShouldRetainBindingContexts | 1: fresh and file-reopened definitions return/store FALSE and reject the same domain/table CHECK writes. | A–D, G, H | 0/1/0 | 1/0/0 |
| **Total** | **28** | | **12/16/0** | **28/0/0** |

The mixed-list regression uses composition: a real table column, a function result whose body
references its CHAR parameter in IN, and a domain cast whose CHECK references VALUE in IN.
The supported SQL expression-body RETURN has no table source
(`Sql.Language/src/SqlQueryParser.Definitions.cs:140`, `SqlDefinitionBinder.cs:80`); domain CHECK
has only VALUE and refuses parameters/subqueries (`SqlDefinitionBinder.cs:144`,
`SqlPlanExecutor.Constraints.cs:1070`). Thus all three cannot be direct identifiers in one lexical scope.
The test exercises the supported composition without expanding that grammar or adding synthetic
test-only mixed scopes.

Before any production edits, the original Release output was rebuilt at the base (existing CHAR
boundary tests **59/0/0**) and saved to `_out/char-review-r3/baseline-original/net10.0` with
`SHA256.json`. The first 27 new regressions, before the reopen case, yielded **12/15/0** on the
unchanged production build and **27/0/0** after the fix. Final baseline evidence uses the final
test DLL over the saved original production DLLs in `baseline-run/net10.0`: **12/16/0**. The same
28 cases pass in the fixed full Release suite. All 78 non-test DLLs in the replay match the saved
original SHA-256 inventory (79 DLLs total, with only the test assembly replaced). The test DLL
SHA-256 is `C44963A85F521E6CADDDC44E1569088D5CA21E7FC032F63D8BB11F886CDAADE9`.
The baseline's failures are the expected reproduced defects, not gate failures. Evidence is in
`baseline-evidence.json`, `results/baseline-final.trx`, `baseline-final-cases.json` and
`fixed-final-cases.json` under `_out/char-review-r3`.

## Parameters-as-columns consumer audit

Paths in this table are under `resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/`.
The audit searched every `SqlColumnReferenceExpression`, `SqlBoundColumn`, Column/Parameter
bound-kind test and evaluator construction in the SQL engine, then followed the definition,
planner, CHECK/DEFAULT and DROP call paths. Function bodies are opaque to statement walkers:
`SqlPlanner.Children` visits call arguments, not a called function's definition (`:1398`).

| Consumer and site | Finding / action |
| --- | --- |
| `SqlDefinitionBinder.cs:80,144,249,265`; `SqlPlanExecutor.Constraints.cs:896` | These are the synthetic parameter/VALUE producers and domain admission helper. **Fixed:** supply Function or Domain context at declaration and cached/reopened body binding. |
| `SqlExpressionEvaluator.Binding.cs:408,452`; `SqlExpressionEvaluator.cs:271` | **Fixed:** IN requires table-reference identity, not any named slot. Classification is now on the binding scope; no syntax-only API remains. |
| `SqlExpressionEvaluator.cs:140,155` | Ordering scope copies now preserve context. Currently only statements use them, but copying cannot silently change a future definition scope into Table. |
| `SqlExpressionEvaluator.Binding.cs:134,334`; `SqlExpressionEvaluator.cs:213,280,327` | Correct: bind/evaluate an ordinal and resolve the named slot/type. Parameter and VALUE frames intentionally use those operations; neither operation asserts table provenance. |
| `Functions/SqlExpressionFunction.cs:36,65,73,78,80`; `SqlExpressionEvaluator.cs:120`; `SqlPlanExecutor.cs:1462` | Correct: invocation constructs a fresh argument frame and executes the already-bound body. The execution-only evaluator does not classify syntax or bind another scope. Return-value column metadata is used only for assignment coercion; return-domain validation follows separately. |
| `SqlExpressionEvaluator.Functions.cs:116,146,166`; `SqlExpressionEvaluator.Binding.cs:256,265`; `SqlPlanner.cs:1241,1256` | Correct: maximum length, static type, source-domain identity and operator/cast validation use declared slot metadata. Function parameter domain names are retained; VALUE carries the base type. No change to coercion width or domain-specific casts. |
| `SqlExpressionEvaluator.cs:347,380,464`; `SqlExpressionEvaluator.Binding.cs:695,705`; `SqlDefinitionBinder.cs:247` | Correct: collation reads slot metadata. Function slots explicitly receive invocation collation; domain VALUE uses the same invocation path. A scope category does not change collation priority. |
| `SqlExpressionEvaluator.Binding.cs:196,556,607,630` | Correct: BETWEEN binds two comparisons. Folding admits only Constant and supplied statement Parameter bound kinds; SQL arguments/VALUE remain Column ordinal reads, so a cached body never folds against a first invocation's value. |
| `SqlPlanner.cs:386,456,466,1343` | Correct: seek extraction is reached for actual table predicates only. Its column guard now uses the table scope. Volatility comes from the resolved function contract; row-dependent arguments reject seeks, and function bodies retain their own context. The new scan/seek regression pins computed bounds. |
| `SqlPlanner.Joins.cs:107,123,134` | Correct: join-key recognition resolves table-bound ordinals and compares their storage/collation suitability; definition frames never reach join planning. |
| `SqlPlanner.cs:126`; `SqlPlanner.Grouping.cs:40,157,196,254`; `SqlPlanner.Ordering.cs:17,75`; `SqlPlanner.InsertSelect.cs:57`; `SqlPlanner.Subqueries.cs:193` | Correct: projection, grouping equivalence/eligibility, aliases and source metadata operate on planned statement relations. They visit a call's arguments, never its parameter-frame body. |
| `SqlPlanner.cs:660,702,1109,1129` | Correct: scopeless VALUES/count rejection is applied to statements, not SQL function bodies; ordinary expression validation resolves names through its supplied scope. A SQL parameter is a valid named slot, not a supplied `@parameter`. |
| `SqlPlanExecutor.Constraints.cs:1067,1137,1225,1261` | Correct: CHECK admission types named slots and inspects resolved calls for scalar/Immutable eligibility. Domain VALUE legitimately has its base type; synthetic metadata does not weaken volatility checks. |
| `SqlPersistedExpression.cs:199,208,368` | Correct: persisted-expression binding resolves named slots; canonicalization/equality preserve identifier syntax within the same scope. They do not attach table dependencies or classify IN candidates. |
| `SqlBoundTableCache.cs:337,359,392,414,535` | Correct: domain call arguments read a real table column, then invoke a separately bound Domain frame. `ColumnOrdinals` collects real table-CHECK dependencies for updates/DROP COLUMN; it never walks the SQL function body or its VALUE frame. |
| `SqlDefinitionBinder.cs:87,276,295,314,322,359`; `SqlPlanExecutor.Definitions.cs:111` | Correct: function/domain dependency recording uses resolved function identities and declared/cast domain identities, not lexical column names. Parameter/result domain dependencies are added explicitly. DROP refusal reads those recorded identities plus actual column domain/default/CHECK references. Volatility is checked on declaration and reopen against resolved functions. |
| `SqlPlanExecutor.Defaults.cs:156,174`; `SqlBoundTableCache.cs:237,448,510` | Correct: DEFAULT binds with an empty outer frame; invoked SQL bodies have their own Function context. Cache diagnostics and signature recovery use types from the correct table/definition scope. |

No additional misclassification defect was found in these consumers. Existing full-suite coverage
includes `SqlDefinedFunctionTests.ExecuteAsync_SqlDefinitions_ShouldBindAndEnforceDependencies`,
`ExecuteAsync_NullAndVolatility_ShouldFollowDeclaredContract`,
`ExecuteAsync_RepeatedInvocationCollation_ShouldReuseBoundBody`, and the domain binding/reopen
and weakened-volatility tests. The new varying-row parameter tests also guard against accidental
constant folding of invocation slots.

## Final validation

All required gates pass. P/F/S means Passed/Failed/Skipped; these are full suites, including the
28 new SQL regressions. Builds use `MSBUILDDISABLENODEREUSE=1`,
`DOTNET_CLI_USE_MSBUILD_SERVER=0` and `-p:UseSharedCompilation=false`. Release SQL uses
`DOTNET_TieredCompilation=0`. Each `dotnet test` call names the test csproj, with `-c Release` for
Release; no tests are skipped or weakened. Build tasks were built in both configurations first.

| Suite | Debug P/F/S | Release P/F/S |
| --- | --- | --- |
| Database.Sql | 2,835/0/0 | 2,835/0/0 |
| Database.Sql.Language | 1,722/0/0 | 1,722/0/0 |
| Database.Sql.Schema | 362/0/0 | 362/0/0 |
| Database.Sql.Catalog | 120/0/0 | 120/0/0 |
| Database.Sql.Storage | 14/0/0 | 14/0/0 |
| Database.Indexing | 90/0/0 | 90/0/0 |
| Database.Sql.Client | 1,120/0/0 | 1,120/0/0 |
| Database.Client | 69/0/0 | 69/0/0 |
| Database.Hosting | 130/0/0 | 130/0/0 |
| Database (root) | 186/0/0 | 186/0/0 |
| Database.Types | 93/0/0 | 93/0/0 |
| Sdk.Database | 342/0/0 | 342/0/0 |
| **Total** | **7,083/0/0** | **7,083/0/0** |

The SQL NativeAOT harness publishes in Release for `win-arm64`, with **zero IL2xxx/IL3xxx
warnings or errors**. Its in-process/wire smoke passes **23/0/0**. No project files or build
settings were changed. Logs, TRX, `gates.json`, `aot-result.json` and hash inventories remain
under `_out/char-review-r3`; the report records the durable results because that directory is ignored.

No installer, Database.Testing tests, benchmarks, GitHub writes or branch/worktree changes.
