# Database T3 plan: v1 (2026-11-02)

> **Status:** v1, generated 2026-11-02 after T2 merged (#1775, #1783). On 2026-11-02 the owner accepted
> the session's recommendations for all open decisions. This plan applies every recommendation below;
> each row can still be overridden by its number. Supersedes nothing: T3 had no plan.
>
> **Filed 2026-11-02:**
> - 24 new issues, #1787 to #1810, each with its runs as a task list, their acceptance criteria, evidence
>   and dependencies;
> - a T3 run-mapping comment on each of the 29 existing issues the plan uses.
>
> The issues are the working record. This file is the index and the decision log.

**Built from:**
- **The live board:** a roadmap rebuild on 2026-11-02.
- **Three static research passes** against `main` `e86d5dcc5`: SQL, OQL and GQL, and indexes and engine.
  - They covered every T3 family and every carried item, with `file:line` evidence and the reference
    engines' sources.
  - Each issue body carries the evidence for its runs, and claims the passes could not verify statically
    are marked there with the probe that settles them.
- **The T2 precedent:** plan v3, 2026-10-25.

## 0. Bottom line

1. **T3 is about 90 runs, larger than T2 (73).** At five runs per batch that is about 18 batches. Another
   13 runs are gated behind decisions or other programs, and 7 belong to a later programmability program.
2. **It runs as three tracks, in priority order:**
   - **A, availability and correctness (23 runs):** first in every batch.
   - **B, language surface (39 runs).**
   - **C, search and vector indexes (28 runs).**
3. **Rollback is broken today for any additive schema change, not only lossy ones.** [Certain]
   - The reverse plan of an added column is `DropColumn`, and of an added table `DropTable`. Both are
     destructive (`Sql.Schema/src/SqlSchemaMigrationPlanner.cs:194-233`).
   - Without the opt-in, the older application's Build fails with `CS03105`, and the database does not
     start.
   - With the opt-in, that Build deletes the newer version's columns and data.
   - This violates the availability principle (2026-10-31). Track A's migration chain (SQ-A4–A7) fixes
     it, together with #1779.
4. **Process memory is not bounded.** [Certain]
   - Plain SELECT keeps five unbudgeted buffers: buffered nested-loop inners (#1776), the joined
     `matches` list, the plain GROUP BY dictionary, every full sort, and the materialized result.
   - One large `ORDER BY` can exhaust process memory.
   - Track A charges all five (SQ-L1). It then lifts T2's decision D3 ("no spill anywhere") for sort,
     hash and result spooling, so the refused queries run again (EN-X1, SQ-L2–L4).
5. **Under sustained concurrent writes the journal can fill the volume (#1283, not delivered).** [Certain]
   A checkpoint still needs zero active transactions, and the area design says a busy checkpoint never
   takes a database offline "however long its journal grows". EN-C1 traces the growth and EN-C2 adds a
   bounded admission pause.
6. **A refused open still writes (#1744).** T3 bumps about a dozen formats. EN-G1 generalizes SQL's
   copy-on-write probe overlay to Graph, Documents and Key-Value, and merges before the first T3 format
   bump.
7. **Since #1759, a production Database application serves no health endpoint.** [Certain]
   - Nothing outside tests constructs `DatabaseAdminEndpointService`.
   - `DatabaseCommandClient` posted to a route the admin listener now refuses with 403. #1785 (#1811)
     deleted it, with `ResourceCommand` and `ResourceCommandObservation`.

   EN-A1–A3 fix the health endpoint on the new application model (#1785). Two gaps belong to the
   ApplicationModel area (§7).
8. **Cuts (recommended):**
   - defer the SQL clustered index #1634, which rewrites SQL row addressing and is about 5 runs;
   - materialized views, triggers and procedures go to a later program;
   - GRANT and REVOKE wait for a cross-model security program;
   - COPY FROM and LOAD CSV become a bulk-load program;
   - GQL FOREACH and named procedures stay refused.

## 1. Where T3 starts (board reconciliation, 2026-11-02)

**Delivered:**
- T1: #1168.
- Pre-T2: waves 1–11.
- T2: 19 batches, #1742 to #1783. Every T2 family is closed.

**Board corrections made today:**

| Item | Was | Now | Evidence |
|---|---|---|---|
| #1270, #1343, #1353, #1470 | In progress | Done (closed) | Delivered; their leftovers moved to #1283, EN-D1, the SQL lane and #1710 |
| #1527, #1533, #1536, #1540, #1541 | In progress | Backlog, under Hardening gate #1511 | Code delivered with test citations. Only benchmark criteria remain, and the benchmark suite has been paused since 2026-10-16 |
| #1523, #1264 | In review (no PR) | Backlog, T3 | #1523 slices 3–4 go to SQ-L1–L4 and its query-form COPY to SQ-C2. #1264 becomes EN-P1 |
| #1255 | In progress | Backlog | Epic; its last child #1264 is EN-P1 |
| #1756, #1762–#1764, #1767, #1782 | T2 / Prerequisite | T3 | T2 is closed |
| #1776–#1780, #1283, #1744 | Untiered | T3 | Decided or carried |
| #1666–#1668, #1762–#1764, #1767 | No Category or Area | Set | Hygiene |
| `tiers.json` `sql-grants` | T2 | T3 | T2 plan v3 decision 2 |
| Roadmap script | Ignored a family line beside a Tier field | Fixed | 26 delivered T2 families no longer show as unfiled slots |

**Main was red** after #1783 merged: a semantic conflict with #1759 (`Hosting.Health`). Hotfix #1786 fixes
it.

## 2. Owner decisions applied

★ marks a decision that changes scope or an earlier ruling. Every recommendation is applied; override any
row by its number.

| # | Decision | Applied |
|---|---|---|
| T3-1 ★ | T3 structure | Three tracks (A first in every batch, then B, C). Same branch, PR and merge model as T2: one branch and PR per batch, merge commits, cohesion-docs in step |
| T3-2 ★ | Lift T2 decision D3 ("no spill anywhere") | Lifted for sort, hash and result spooling (Q-SP2, O12). Temp files go under the database directory. The engine quota is min(10 GiB, 25% of free space); the statement quota is 4× the budget (Q-SP3). In-memory engines spill only with an explicit directory |
| T3-3 ★ | Charge every retained buffer | Yes, in SQ-L1 (Q-SP1). The refusals are temporary until SQ-L2–L4. A process-wide grant pool is a later Hardening item |
| T3-4 ★ | A never-seen schema with destructive steps and no opt-in (Q-RB1) | Open at the stored schema, run nothing, and report Degraded with the `CS03105` detail. Verify mode (`CS03103`) stays the strict path for pipelines |
| T3-5 | Rollback mechanics (#1779) | An applied-schema history: a new SQL catalog kind, hash-only, never evicted. A rollback ignores the destructive opt-in. `ApplySchemaAsync` keeps throwing (Q-RB2). Schema lineage comes later (Q-RB3) |
| T3-6 | Default-only changes | Published jointly instead of refused (Q-MG1) |
| T3-7 ★ | #1634 clustered index | Deferred out of T3 (O6). #1739 INCLUDE and index-only reads is the read path |
| T3-8 ★ | GQL CALL and FOREACH versus D3 | D3 amended: inline `CALL { }` subqueries (read and write) yes; named procedures no (indexes use `SEARCH`); FOREACH stays refused |
| T3-9 | SQL programmability (Q-PG1–PG3) | In T3: generated columns (STORED and VIRTUAL, bare means VIRTUAL, code-first) and user and table functions. Later program: materialized views, triggers and procedures, as application-registered C# with no interpreter |
| T3-10 | Grants #177 (Q-GR1) | Gated on a cross-model security program; no SQL-only principal store |
| T3-11 | Bulk load (Q-CP2) | COPY FROM STDIN, #1656, #1545 and GQL LOAD CSV form a separate bulk-load program, with one shared CSV reader. COPY's query form and credit window stay in T3 (Q-CP1) |
| T3-12 | Arrays (Q-AR1, Q-AR2) | One dimension with lower bound 1; code-first `T[]` in SQ-Y4 |
| T3-13 | Enums (Q-EM1) | Code-first CLR enums map to SQL ENUM only by per-property opt-in |
| T3-14 | DEFERRABLE (Q-DF1) | Foreign keys only in T3. Deferrable UNIQUE and PRIMARY KEY (SQ-X2) are gated |
| T3-15 | Regex (Q-RX1–RX3) | .NET NonBacktracking with PostgreSQL-syntax translation, refusing backreferences and lookaround. Refused under nondeterministic collations. `^` follows POWER's type rule |
| T3-16 | JSONB (Q-JS1, Q-JS2) | Canonical text plus a structural comparator (one codec). Add SQ-N8 for JSON constructors and aggregates |
| T3-17 | Plan cache #1780 (Q-PC1) | One shared cache per database |
| T3-18 | Full-text postings (O1) | A B-tree dictionary plus compressed posting pages (about 5–10× GIN rather than 20–40×) |
| T3-19 | BM25 statistics (O2) | Physical counters, documented as approximate like Lucene's deleted documents. SQL ranks with `ts_rank` and `ts_rank_cd` |
| T3-20 | New child roots (O3) | `Database.Text` and `Database.Spatial` as new projects. Each run gets #1785-style wiring scope |
| T3-21 | Vector (O4) | HNSW first, 2,000 float32 dimensions; synthetic data in tests, SIFT1M and GloVe in cohesion-benchmarks |
| T3-22 | Geography (O5, O9) | Each model's reference model in one library. Documents refuses points outside the extents; Graph clamps them |
| T3-23 | SQL full-text surface (O7) | PostgreSQL's `@@` family with `CREATE INDEX … USING FULLTEXT … WITH (config)`; expression-level values |
| T3-24 | OQL spellings (O8) | Couchbase where it has the feature (`SEARCH()`, `APPROX_VECTOR_DISTANCE`); Cosmos DB `ST_*` for GeoJSON. Verify spellings first |
| T3-25 | Graph vectors (O10) | Overflow chains (Graph record format 6), with no dimension cap tied to the other properties |
| T3-26 | GQL full-text and vector surface (O11) | The Cypher 25 `SEARCH` clause |
| T3-27 | #1283 (O13) | The bounded admission pause now; a fuzzy checkpoint only if the pause measurably costs throughput |
| T3-28 | Managed HTTP health (O17) | The health listener binds the platform's `http` endpoint when managed |
| T3-29 | OQL MERGE ANSI `ON` | Key equality only |
| T3-30 | OQL set-operation header | RAW/VALUE gives a `value` column; identical projections give typed columns; otherwise whole-document mode |
| T3-31 | OQL windows | Port, not extract, from SQL. Include FROM LAST and `RATIO_TO_REPORT` |
| T3-32 | SQL additions from the deferral audit | In T3: subqueries in UPDATE SET, VALUES, CHECK, DEFAULT and LIMIT (SQ-Q2), `SELECT … FOR UPDATE / FOR SHARE` (SQ-FU1), and CASE and COALESCE common-type resolution (SQ-CT1, Track A). Later: outer-join reordering, and online table rewrite |
| T3-33 | #1538 parse and plan caches across models | Stays in the Hardening backlog, after T3's grammar churn |

## 3. Track A: availability and correctness (23 runs)

| Run | Issue | Scope | Depends on | Format |
|---|---|---|---|---|
| EN-G1 | #1744 | A refused open writes nothing: SQL's probe overlay generalized to Graph, Documents and Key-Value | — | none |
| SQ-T4 | #1764, #1782 | Reclamation snapshot pins break context-count assertions; one root cause, CI stability | — | none |
| SQ-A4 | #1777 | Keep table owners for index-only changes in a joint migration | — | SQL bump (35) |
| SQ-A5, SQ-A6 | #1778 | Publish 49a's deferred migrations jointly, with default-only changes (T3-6) | A4 | SQL bump |
| SQ-A7 | #1779 | Applied-schema history and hold-and-degrade rollback (T3-4, T3-5). Covers any change, not only lossy ones | A5, A6 | SQL bump, new code |
| SQ-A8 | #1756 | Sdk.Database migration writer for TYPE changes (`sdks/` edits authorized) | K4, A6 | SDK output |
| SQ-K4 | #1787 | Index DDL defaults to ASC NULLS FIRST (SQ-K3 option A, decided) | EN-G1 | SQL bump, compiled v12, SDK |
| SQ-H3 | #1763 | Plain GROUP BY must not share volatile aggregates | — | none |
| SQ-CT1 | #1788 | General CASE and COALESCE common-type resolution outside JSON positions | — | none |
| OQ-K1 | #1767 | An unaliased collection name is the implicit alias everywhere. Fixes DML reading `g` but writing `items.g`. Lifts OQ-J1's explicit-alias rule and retires `CD00016` | — | none |
| SQ-L1 | #1776, #1762 | Charge all five retained buffers, and CTAS OFFSET rows | — | none |
| EN-X1 | #1789 | Spill substrate: temp space under quotas, external sort, free-space floor | — | none (temp layout private) |
| SQ-L2, SQ-L3, SQ-L4 | #1789 | Spillable sort; hash aggregation and DISTINCT; result spooling | EN-X1, L1 | none |
| EN-C1, EN-C2 | #1283 | Trace the growth to the transactions that block checkpoints, and route raw brackets through the apply gate; bounded admission pause near the journal limit | — | none |
| EN-P1 | #1264 | `DatabaseRow` NVI with typed accessors; parser and analyzer audit | — | public API |
| EN-A1 | #1790 | Each model server binds its named platform endpoint when its options set none. The health listener binds `http` when managed. (#1785 already deleted `DatabaseCommandClient` and `ResourceCommandObservation`) | #1785 | public API |
| EN-A2 | #1790 | Database health as `IApplicationInstanceHealthContributor`s, plus a readiness contributor for accepting servers | #1785 | none |
| EN-A3 | #1790 | Read-only `databases` and `health` actions; no write actions (Hosting D3) | #1785 | none |
| EN-D1 | #1791 | Fold the event-source, extensibility and concrete-types program documents into the project designs, and delete them | — | docs |

## 4. Track B: language surface (39 runs)

**SQL (27):**

| Family | Runs | Issue | Notes |
|---|---|---|---|
| sql-joins T3 | SQ-J3 (NATURAL, qualified `t.*`), SQ-J4 (LATERAL) | #1792 | J4 shares `SqlPlanExecutor.Joins.cs` with L1, L3 and L4, so they never pair |
| sql-distinct-on | SQ-D1 (DISTINCT ON, SELECT ALL) | #1793 | After L1 |
| sql-constraints-defaults T3 | SQ-X1 (DEFERRABLE foreign keys) | #1794 | SQL bump; compiled bump for typed FKs |
| sql-type-names T3: enums | SQ-V1, SQ-V2 | #1795 | SQL bump; code-first by opt-in |
| sql-type-names T3: arrays | SQ-Y1–Y4 | #1796 | SQL bump; **a new wire cell kind**; a member in the shared type enum |
| sql-operators T3 | SQ-E1 (regex), SQ-E2 (`^`, `regexp_*`) | #1797 | Untrusted patterns run on a linear-time engine |
| sql-scalar-functions T3: JSON | SQ-N3 (JSONB normalization, UNIQUE KEYS) to SQ-N7 (JSON_TABLE), SQ-N8 (constructors and aggregates) | #1798 | N3 changes stored JSONB bytes: SQL bump |
| sql-copy T3 | SQ-C2 (query form), SQ-C3 (credit window, acknowledgment deadline) | #1799 | C2 after L1 and L4 |
| sql-programmability T3 | SQ-U1, SQ-U2 (user and table functions, `VALUES` in FROM), SQ-B1, SQ-B2 (generated columns) | #1800 | U2 feeds Y3 and N7 |
| sql-subqueries T3 | SQ-Q2 (subqueries in UPDATE SET, VALUES, CHECK, DEFAULT, LIMIT) | #1801 | — |
| sql-locking | SQ-FU1 (`FOR UPDATE / FOR SHARE`) | #1802 | Lock-manager row locks |
| Plan cache | SQ-Z3 | #1780 | After J4 and U2 (cache keys) |

**OQL (7):**
- set operations SQ-style: OQ-S1, OQ-S2 (#1803);
- MERGE: OQ-M1, OQ-M2 (#1804);
- windows, ported from SQL: OQ-W1–W3 (#1805).

All of them follow OQ-K1. At most one OQL run per batch, except MERGE paired with windows.

**GQL (5):**
- closed graph types (gql-schema-ddl T3): GQ-T1–T3 (#1806), with Graph catalog v3. Closing a type refuses
  writes from older application versions, so a closed type is opt-in per type, under the availability
  principle;
- inline `CALL { }` subqueries (gql-call-foreach): GQ-N1, GQ-N2 (#1807). Writing subqueries must see the
  writes of earlier invocations.

## 5. Track C: search and vector indexes (28 runs)

| Group | Runs | Issues |
|---|---|---|
| Substrate | IX-S1 (per-index page ownership, DROP INDEX frees pages, bounded builds), IX-B1 (B-tree leaf payloads, page format 3) | #1808, #1809 |
| Full-text core (`Database.Text`) | IX-T1, IX-T2, IX-F1, IX-F2, IX-F3 | #1626 |
| Vector core | IX-V1, IX-V2, IX-V3 (HNSW with repair after deletes) | #1627 |
| Geography and spatial (`Database.Spatial`) | IX-G1, IX-G2, IX-G3 | #1628 |
| SQL | IX-I1, IX-I2 (INCLUDE, index-only); IX-Q1, IX-Q2 (full-text); IX-P1, IX-P2 (spatial) | #1739, #1635, #1640 |
| Documents | IX-D1 (full-text), IX-E1 (vector), IX-H1 (2d), IX-J1 (2dsphere) | #1636, #1638, #1641, #1642 |
| Graph | IX-W1 (vector and point values, overflow records, record format 6), IX-R1 (text index), IX-K1 (full-text, `SEARCH`), IX-N1 (vector), IX-O1 (point) | #1810, #1644, #1637, #1639, #1643 |

The largest risk in the tier is IX-V3, HNSW repair under MVCC. Every index kind bumps a store format.

## 6. Batches

**Rules:**
- About five runs per batch: at most two SQL, one OQL, one GQL, and two IX or EN.
- At most one run per SQL collision group: STO, MIG, SDK, DDL, EXPR, SEL, MEM and WIRE (from the SQL research).
- At most one format bump per store per batch, with numbers assigned at merge in merge order.
- Track A first; a batch never waits on B or C.
- EN-G1 merges before any T3 format bump.
- Same delivery as T2: a branch and PR per batch, merge commits, the cohesion-docs pass in step, Codex
  runs, reviews and fix passes.

| Batch | Runs | Bumps |
|---|---|---|
| B1 | EN-G1, SQ-T4, SQ-A4, OQ-K1, EN-C1 | SQL 35 (A4), after EN-G1 |
| B2 | SQ-K4, SQ-L1, OQ-S1, GQ-T1, EN-A1 | SQL 36 (K4), Graph catalog (T1) |
| B3 | SQ-A5, EN-X1, SQ-H3, OQ-S2, GQ-N1, EN-P1 | SQL (A5) |
| B4 | SQ-A6, SQ-L2, EN-C2, IX-B1, SQ-J3, IX-R1 (#1644) | B-tree format (B1), Graph catalog (R1) |
| B5 | SQ-A7, SQ-L3, IX-S1 after A7, SQ-CT1, OQ-M1 with OQ-W1 | SQL (A7, then S1 in merge order) |
| B6 | SQ-A8, SQ-L4, EN-A2, IX-T1, GQ-T2 | Graph catalog (T2) |

After B6, Track A's EN-A3 and EN-D1 fill open slots. B and C follow their lane queues:
- **SQL:** J4 → D1 → X1 → V1 → E1 → N3 → U1 → V2 → E2 → N4 → C2 → U2 → Y1 → N5 → Z3 → Y2 → C3 → B1 → N7 →
  N6 → N8 → Q2 → FU1 → B2 → Y3 → Y4.
- **OQL:** M2 with W2 → W3.
- **GQL:** N2 → T3.
- **IX** follows the research grid: b2–b8 with T2, V1, F1, I1, V2, G1, F2, I2, V3, G2, F3, Q1, D1, W1, G3, Q2,
  K1, E1, P1, P2, H1, N1, J1, O1.

Batch composition is fixed at launch, as in T2.

## 7. Escalations to the ApplicationModel area (not Database runs)

- **E-AM1, named endpoints with schemes.** A resource declares named endpoints, each with a scheme,
  including non-HTTP ones such as the Database wire. Platforms assign each (`COHESION_ENDPOINT_<EP>_*`) and
  report one output per endpoint. Until then, the in-process platform assigns one `http` endpoint
  (`InProcessInfrastructure.cs:59,327-329`), and dependents receive a database's address as a literal
  setting.
- **E-AM2, readiness separate from publication.**
  - The instance reports that it serves, through a renewal flag or a second publication after
    `StartAsync`, and Gateway G18 waits for it.
  - The gateway consumes health contributors (Hosting open questions 2 and 7).
  - Today a database counts ready before its servers listen.
- **The startup window, documented in #1785.** Publication waits for Build's open, recovery and
  migration, so the gateway's startup timeout must cover them.

## 8. Gated and later

| Item | Runs | Gate |
|---|---|---|
| Bulk-load program: #1656 (bulk contract, frames 15–20), #1545, SQL COPY FROM STDIN, the online half of #1547, GQL LOAD CSV | EN-B1–B3, SQ-C4–C6, GQ-L1 | Its own program (T3-11) |
| Grants #177 | SQ-G1–G3 | Cross-model security program (T3-10) |
| Deferrable UNIQUE and PRIMARY KEY | SQ-X2 | T3-14 |
| Later programmability program | MV1–MV2, TG1–TG3, PR1–PR2 | T3-9 |
| SQL clustered index #1634 | 5 | T3-7 |
| Parse and plan caches across models #1538 | 3 | T3-33 |
| Outer-join reordering; online table rewrite | — | T3-32 |
| Hardening benchmark proofs: #1527, #1533, #1536, #1540, #1541 | 0 runs | The benchmark suite resuming |

## 9. Risks

1. **Migrations and rollback.** Until SQ-A4–A7 land, a rolled-back Build either takes the database down or
   deletes newer data. Track A schedules them first; the chain spans B1–B5.
2. **Memory refusals before spill.** SQ-L1 makes some queries that run today refuse with `CS05067`, until
   SQ-L2–L4 land two to three batches later. A spill to the database's own volume must respect the engine
   reserve's free-space floor (T2 SQ-A3), or it can take the database offline.
3. **Format churn.** About seven SQL bumps, up to five compiled-schema bumps, B-tree format 3, Graph record
   format 6, Graph catalog v3 and later, and Documents index records. EN-G1 first.
4. **HNSW under MVCC (IX-V3)** and full-text index size (IX-F1–F3).
5. **#1767's DML mismatch** (`items.g` read as `g` but written as a field). [Likely, statically] OQ-K1's
   first step is the probe in the OQL report's appendix.
6. **Regex and JSON** take untrusted patterns and documents. They must run on linear-time engines and within
   the budgets.
