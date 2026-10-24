# Database convention audit (#1470)

Public leaves remain sealed; the five Database interface contracts are listed in the area rule.
`LockManager()` and `StoragePageLatch()` are public parameterless constructors (decision 61).
Error identities use the [error-code registry](ERROR-CODES.md); structured carriers keep the code
outside message text. Standard framework argument guards retain their exception contracts.

## Reusable throws and local validation

The baseline static-throw audit found seventeen methods. The three unconditional catalog-conflict
throws moved from model database classes to internal static exception extension members:
`ThrowDocumentConflict()`, `ThrowBlobConflict()` and `ThrowGraphConflict()` on the foreign,
root-owned `DatabaseTransactionAbortedException`. Their messages and diagnostic translation are
unchanged; Graph retains its cached message and `CG04007` carrier.

The remaining fourteen methods retain local policy or private cold-path responsibilities:

| Owning type / methods | Reason for keeping the guard local |
|---|---|
| `DatabaseEngine.ThrowIfEmpty` | The root public member's name/disposal/cancellation validation order. |
| `DatabaseFileNames.ThrowIfInvalid` | Portable-name grammar, followed by its local resource-backed refusal reason. |
| `DatabaseMemoryFiles.ThrowIfOpen` | Whether the owned file set still has live streams. |
| `DatabaseEngineOptionChecks.ThrowIfNotPositive` (TimeSpan and int), `ThrowIfNegative`, `ThrowIfInvalidGroupCommitWindow` | Conditional engine-option policy and value/parameter contracts. |
| `SqlDatabase.ThrowIfFormatIsNotCurrent`, `KeyValueDatabase.ThrowIfFormatIsNotCurrent` | Model-owned catalog/record-format admission policy. |
| `SqlDatabaseSession.ThrowLocatedFailure` | Statement-span bounds and concrete exception/cause identity during location attachment. |
| `SqlValue.ThrowNullCompared` | Private non-inlined cold branch of comparison; successful comparisons retain their existing code and allocation shape. |
| `SqlExpressionRenderer.ThrowIfTooDeep` | Private renderer-depth validation. |
| `StoragePageManager.ThrowIfHeaderPage` | Private reserved-header-page mutation guard. |
| `Storage.ThrowIfLsnMoved` | Private page-identity/LSN validation before journaling. |

These guards evaluate state or policy rather than share an unconditional throw across unrelated
objects. Contextual factories in binding/provisioning select codes, locations and retained causes;
they are distinct from throw-only helpers. No shared public exception-helper type is introduced.

## Shared-source resources

Engine option requirements and portable-name refusal text bind to each model assembly's generated
internal accessor. Valid name checks avoid resource lookups; the description format used during
engine construction is cached. The SQL Types and Defaults modules similarly have a separate
internal binding in each actual consumer, including SDK Tasks. No shipped assembly accesses
another shipped assembly's internals and no new project reference is required.

The semantic message audit is reviewed by category: operation/type names, SQL keywords, value
placeholders and punctuation used to compose an existing structured diagnostic are data. Existing
protocol overflow notices are wire warning descriptions, rather than exception-message templates;
their invariant wording and transport limits remain unchanged. Exception prose and its explanatory
format fragments live in generated resources. No storage or wire version changes are required.

The post-main rules audit also checks private field/constant naming, explicit constructors, partial extension containers, internal helper placement and build item-group structure. Database source now also follows the debugger-view rule described below. Pure naming and constructor-syntax changes preserve message text, diagnostic identities, work accounting, ownership and all statement budgets. Deferred P8/D9 surfaces and the documented hosting ownership exception are retained.


## Debugger inspection

Concrete types in Database `src/` folders follow the debugger display and proxy convention
in `.claude/rules/general-rules.md`. A view is needed only for explicit private/internal
instance state that requires filtering, including inherited state. Existing effective base
displays/proxies are reused. Exceptions, EventSource subclasses, compiler-generated backing
storage, already hidden state, tests, shared source, fixtures and samples remain excluded.

A new view is private, nested, last in the same primary type file, and has a public constructor
with public read-only projections of reviewed public/protected state. Inspection never runs
queries, enumerates results, resolves services, takes locks, delegates storage/stream reads,
invokes callbacks, or lazily creates data. Stored references may be returned without accessing
their contents. Ref structs capture only safe scalar snapshots; borrowed spans, pointers and
ref-like values never escape into heap views. Types without a safe permitted projection use
an empty view and type-name display. Existing diagnostic summaries and redaction remain intact.

These additions affect debugger inspection only. Original constructors, methods, property
getters, field initializers and instance layout are unchanged, as are public runtime APIs,
error identities, formats, wire messages and normal allocation budgets.
