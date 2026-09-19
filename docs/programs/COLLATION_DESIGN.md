# Collation — design and scope

**Status:** implemented in Phase 17 (#1025), verification recorded in `_out/phase17-VERIFICATION.md` · **Created:** 2026-09-18 · **Owner:** Chase Crawford
**Related:** #854 (shared type system and collation foundation, closed) · `Database.Types`,
`Database.Indexing`, `Database.Sql`, `Database.Sql.Catalog`

---

## 1. Baseline before Phase 17

At the approved baseline `0d8049f9`, `Assimalign.Cohesion.Database.Types` had a `Collation` type, and almost nothing used it. The following table records that defect; §8 records the implementation.

| Component | State |
|---|---|
| `Collation` | Exactly two: `Binary` (code-point) and `Invariant` (`CompareInfo` with `CompareOptions.None`). `FromId` throws on anything else — it is a closed set of two. |
| `SqlCatalogColumn` | `Name`, `Type`, `IsNullable`, `DefaultLiteral`. **No collation field.** |
| `SqlExpressionEvaluator` | Hardcodes `Collation.Binary.Compare(...)` for every string comparison. |
| `IndexKey` | References collation **only in comments** — *"strings under an explicit collation"* describes an intent, not an implementation. |
| SQL language | No `COLLATE` clause anywhere in the grammar. |

**The practical consequence:** every string comparison in the engine is ordinal, permanently.
`WHERE Name = 'alice'` cannot match `'Alice'`. `ORDER BY Name` is code-point order, not linguistic
order. A `UNIQUE` constraint on a string column is case-sensitive and cannot be anything else. There
is no configuration, DDL, or API that changes any of this.

For a relational engine, case-insensitive comparison is table stakes. This is a real gap rather than
a refinement.

## 2. The constraint that shapes the whole design

**Index keys must be order-preserving under raw byte comparison.** That is the repo's standing
storage rule, and it is what makes a B+Tree seek work at all: the tree compares encoded key bytes,
never strings.

A full linguistic collation generally **cannot** be expressed as an order-preserving byte transform.
Correct locale ordering (Turkish dotless *ı*, Swedish *å* sorting after *z*, contractions like
Hungarian *cs*) requires ICU collation elements — in .NET, `CompareInfo.GetSortKey`. Persisting
those bytes into an index makes the on-disk index format depend on the **ICU version**, so a
platform ICU upgrade can silently invalidate every persisted index.

**Owner decision (2026-09-18): byte-expressible collations only in this pass.** Full linguistic
collation is deferred to the backlog rather than abandoned — see §6.

## 3. The collation set

Collations supported in this pass are exactly those expressible as a deterministic,
order-preserving byte transform of the input:

| Collation | Comparison | Byte transform |
|---|---|---|
| `Binary` | Code-point ordinal | The UTF-8 bytes as-is. Exists today. |
| `CaseInsensitive` | Ordinal, case-folded | Invariant simple case fold, then UTF-8. |
| `CaseAccentInsensitive` | Ordinal, case- and accent-folded | Unicode normalization with combining marks stripped, case folded, then UTF-8. |

`Invariant` stays for compatibility but is documented as **not index-backed**, because
`CompareInfo` ordering is not a byte transform. A predicate under `Invariant` on an indexed column
falls back to a scan, and that must be stated in the design doc rather than discovered.

**Every collation here is culture-independent by construction.** That is deliberate: it means the
engine's behavior does not depend on machine locale, on `CultureInfo.CurrentCulture`, or on the ICU
version present at runtime.

### The AOT trap this avoids

`InvariantGlobalization` is not set anywhere in the repo today. If it ever were, `CompareInfo`
silently degrades to ordinal — and every culture-aware collation would quietly become binary, with
no error. **That is structurally the same defect as #1018**: a promise that degrades silently
instead of failing.

Byte-expressible collations remove the trap rather than documenting around it. They do not consult
`CompareInfo` at all, so there is no globalization mode in which they silently mean something else.
Any future linguistic collation (§6) **must fail loudly under `InvariantGlobalization`**, never
degrade.

## 4. Where collation is resolved

**Owner decision: a database-level default, overridable per column, overridable per expression.**

```
database default collation
  └─ column collation          (CREATE TABLE … name TEXT COLLATE case_insensitive)
       └─ expression collation (WHERE name = 'alice' COLLATE binary)
```

Resolution is innermost-wins. An unqualified comparison uses the column's collation; a column
without an explicit collation uses the database default; a database without one uses `Binary`.

**Where it must be honoured**, all of which are currently ordinal-only:

- `WHERE` string comparison and `LIKE`
- `ORDER BY`
- `GROUP BY` grouping equality
- `UNIQUE` constraint equality — *a case-insensitive unique column must reject `Alice` when `alice`
  exists*
- Index key encoding and therefore planner seek eligibility
- `DISTINCT`

**The rule that matters most:** a `UNIQUE` constraint and the index backing it must agree on
collation, or the constraint silently stops being enforced for the cases that differ. That is the
one place where getting this wrong is a correctness bug rather than a usability one.

## 5. What changes

| Component | Change |
|---|---|
| `Database.Types` | New collations with their byte transforms; `Collation` stops being a closed set of two; each collation declares whether it is index-backable. |
| `Database.Indexing` | `IndexKey` encodes strings through the collation's byte transform, making the comment true. |
| `Database.Sql.Catalog` | `SqlCatalogColumn` gains a collation, persisted and surviving restart. The database carries a default. |
| `Database.Sql.Language` | `COLLATE` in `CREATE TABLE` column definitions and in comparison expressions; added to `SqlClauses` and the profile **only once the executor honours it**. |
| `Database.Sql` | Planner resolves the effective collation and selects an index only when its collation matches; executor stops hardcoding `Collation.Binary`. |
| Other four models | Documents, Graph and Key-Value compare strings too. Decide per model whether collation applies, and say so — a model that stays ordinal-only should say that explicitly rather than leaving it ambiguous. |

## 6. Deferred to the backlog — full linguistic collation

Not abandoned. Filed separately so the boundary is explicit:

- **Culture-aware collations via ICU sort keys**, with a versioning story for the persisted index
  format — the index must record the ICU version it was built under and refuse or rebuild on
  mismatch, rather than returning wrong results.
- **`InvariantGlobalization` must fail loudly**, never silently degrade a linguistic collation to
  ordinal.
- **Tailoring** — per-locale rules such as Turkish *ı*, Hungarian *cs*, Swedish *å*.
- **Collation in the other models**, once the relational shape is proven.

## 7. Non-goals

- No user-defined collations.
- No per-session or per-connection collation override.
- No collation-aware full-text search.
- No runtime locale dependence anywhere in this pass — if behavior varies by machine, it is wrong.


## 8. Phase 17 implementation (#1025)

The three byte-expressible collations are implemented with stable identifiers:
Binary = 0, legacy Invariant = 1, CaseInsensitive = 2, CaseAccentInsensitive = 3.
The folding/decomposition/mark tables are pinned to Unicode 17.0 and checked into
Database.Types, with source checksums and a regeneration script. Neither runtime
case conversion nor runtime normalization determines persisted keys. Case folding
uses the Unicode default simple C/S mappings, without Turkic tailoring or multi-scalar
full-fold expansions. Accent folding recursively decomposes canonically (including
algorithmic Hangul), removes mark categories Mn/Mc/Me, then simple-folds. Thus composed
and decomposed accented text agrees, while Binary preserves the original code points.
Malformed UTF-16 is rejected instead of allowing replacement bytes to collapse keys.

Index keys contain the collation identifier and escaped transformed UTF-8 only.
There is no original-spelling tie breaker: equal strings must produce identical
keys and unique-key lock hashes. Stored row values remain binary encoded to retain
the original spelling. The key reader returns canonical text for folding collations;
legacy invariant key decoding remains supported, but new invariant keys are rejected.
String equality and range seeks require the expression's effective collation to
match the indexed column. Mismatches, including an explicit invariant comparison
on a binary or folded index, scan and evaluate the full predicate.

Database defaults are persisted in a catalog record; column overrides are nullable
metadata in a versioned table extension. Older catalogs read as Binary with inherited
column defaults. Create a database using the concrete SQL engine overload
`CreateDatabaseAsync(name, Collation.CaseInsensitive, cancellationToken)`;
the existing engine interface continues to default to Binary. Catalog defaults may
be configured only before tables exist (or set idempotently to the same value), so
changing the default cannot invalidate existing index keys. Restart reuses the
persisted setting. No session setting is introduced.

`CREATE TABLE t (name TEXT COLLATE case_insensitive)` and scalar postfix
`COLLATE` execute. Resolution propagates through scalar expressions, preserves the
innermost explicit override, and otherwise uses the column then database default.
Where peer operands have equal precedence, the left operand supplies the collation.
WHERE (including IN/BETWEEN), LIKE, ORDER BY, GROUP BY/HAVING, DISTINCT, MIN/MAX,
and indexed UNIQUE enforcement all use the same rule. Group and DISTINCT hashes
use exactly the collation used for equality. Binary grouping tests remain unchanged;
new cases test folded equality and hashing alongside them.

Indexes, PRIMARY KEY and UNIQUE require index-backed collations and reject legacy
Invariant columns even when empty. Foreign-key string columns must have matching
effective collations, preventing indexed parent checks and reverse-reference checks
from disagreeing. Comparison under legacy Invariant remains compatible and scan-only;
it fails loudly under invariant globalization. The wording conflict between preserving
this legacy comparer and the checklist's blanket CompareInfo prohibition is recorded
in `_out/phase17-ESCALATIONS.md`: only the legacy comparer/hash retains CompareInfo,
never the three supported byte transforms or any index-key encoding.

The measured SQL profile is **32 of 49 declared clauses**. COLLATE has a live
conformance case and wire execution coverage. Unknown/culture-aware/custom names,
session overrides and collation-aware full-text syntax are rejected. Full linguistic
collation and its index-versioning story remain #1026. Documents, Graph, Key-Value
and Blob retain their existing ordinal comparison rules; their model design documents
explicitly state that SQL collation defaults and overrides do not apply to them.
