# Database error codes

A Database diagnostic identity is two uppercase letters followed by five decimal digits.
The prefix identifies the component that owns the failure. The suffix is allocated within
one of the categories below; it is an identity, not an ordinal that callers should interpret.
The suffix has the form `CCNNN`: two category digits and three condition digits.
Use the complete code for branching. Allocation is append-only within a category; never
reuse a published identity for another condition. Allocate after the highest assigned number
in the chosen category or documented subcategory; leave retired identities reserved. `00000` is unassigned.

| Prefix | Owner |
|---|---|
| CK | Shared Database kernel and infrastructure |
| CS | SQL |
| CD | Documents |
| CB | Blob |
| CG | Graph |
| CV | KeyValuePair |
| CC | Cache |

| Suffix range | Category |
|---|---|
| 00001–00100 | Language and syntax |
| 00101–00999 | Reserved |
| 01000–01999 | Binding and name resolution |
| 02000–02999 | Values, types and encoding |
| 03000–03999 | Schema, catalog and constraints |
| 04000–04999 | Transactions, locking and transaction state |
| 05000–05999 | Execution, capabilities and budgets |
| 06000–06999 | Storage, durability and format |
| 07000–07999 | Protocol, transport, authentication and sessions |
| 08000–08999 | Configuration and argument validation |
| 09000–09999 | Hosting, pooling and lifecycle |
| 10000–99999 | Reserved |

The code migration changes diagnostic strings and corresponding legacy message prefixes.
It does not change SQLSTATE values, numeric wire error kinds, frame layouts, protocol versions,
or disk formats. A diagnostic carries its code separately from its unprefixed message; legacy
message-only failures carry one prefix. Framework argument guards keep .NET exception contracts
and do not gain synthetic database identities. Compiler/analyzer diagnostic identifiers such as
`COHDBSDK` and `COHRES` are independent and remain unchanged.

This registry covers the Database edit boundary. CK does not reserve codes for unrelated
Cohesion libraries or resource areas. Cache has its prefix reserved even when an operation
reports through the existing cache result vocabulary rather than an exception diagnostic.

Process-crash test reopening (#1713) changes no error identity or engine behavior. An underlying
sharing/lock violation (Windows HRESULT 0x80070020/0x80070021, or EWOULDBLOCK/EAGAIN errno
11 on Linux and 35 on macOS) still fails open with Blob CB06005,
Documents CD06104 or Graph CG06006, retaining the I/O cause. Only the test harness retries those
causes; unrelated errors propagate immediately. Its 30-second timeout names the file and last
error and has no production diagnostic code.

## Kernel migration

| Previous code | Code | Condition family |
|---|---|---|
| COHDBL001 | CK00001 | Unsupported language clause |
| COHDBS001 | CK06001 | Storage format refusal |
| COHDBS002 | CK06002 | Storage offline |
| COHDBS003 | CK06003 | Record or page corruption |
| COHDBI001 | CK06101 | Index format refusal |
| COHDBI002 | CK06102 | Index corruption |
| COHDBP001 | CK07001 | Diagnostic truncation notice |
| COHDBP002 | CK07002 | Transaction status refusal |
| COHDBTCP001 | CK07101 | TCP listener host/address refusal |
| COHDBO001 | CK08001 | Listener option refusal |
| COHDBO002 | CK08002 | Session count refusal |
| COHDBO003 | CK08003 | Server timeout refusal |

## Sql migration

SQL syntax uses `CS00001`–`CS00099`. `CS00009` is the informational note for an omitted
statement semicolon; it does not refuse parsing or execution. `CS00100` is retired and reserved:
the migration described it as a profile refusal, but the parser used it for this note.
Recognized unsupported clauses use `CK00001`.
SQL UPDATE ... FROM and DELETE ... USING execute (#1714). Multiple source associations for
one UPDATE target report CS05068 before any mutation; DELETE deduplicates physical targets.
Quantified comparisons (#1718) reuse CS01015 for name binding, CS02055 for incompatible
compared non-NULL values, and CS05013 for the existing single-column scalar/IN/ANY/SOME/ALL
subquery shape condition. No new diagnostic identity is allocated.

Within category 03, condition bands 000–099 cover catalog/DDL, 100–199 provisioning,
200–299 declared scripts, 300–399 registered definitions, and 400–499 generic constraint failures.
These bands make allocation predictable while callers continue to match the complete identity.
The declared-script `CS03201`–`CS03205` refusals retain their existing message-only contract;
structured-carrier messages remain unprefixed.

| Previous code | Code | Category |
|---|---|---|
| SQL0001 | CS00001 | Language/syntax |
| SQL0002 | CS00002 | Language/syntax |
| SQL0003 | CS00003 | Language/syntax |
| SQL0004 | CS00004 | Language/syntax |
| SQL0005 | CS00005 | Language/syntax |
| SQL0006 | CS00006 | Language/syntax |
| SQL0007 | CS00007 | Language/syntax |
| SQL0008 | CS00008 | Language/syntax |
| SQL0100 / CS00100 (retired) | CS00009 | Language/syntax — omitted semicolon (Information) |
| COHSQLE005 | CS01005 | Binding/names |
| COHSQLE006 | CS01006 | Binding/names |
| COHSQLE008 | CS01008 | Binding/names |
| COHSQLE009 | CS01009 | Binding/names |
| COHSQLE014 | CS01014 | Binding/names |
| COHSQLE015 | CS01015 | Binding/names |
| COHSQLE016 | CS01016 | Binding/names |
| COHSQLE019 | CS01019 | Binding/names |
| COHSQLE020 | CS01020 | Binding/names |
| COHSQLE023 | CS01023 | Binding/names |
| COHSQLE026 | CS01026 | Binding/names |
| COHSQLE028 | CS01028 | Binding/names |
| COHSQLE051 | CS01051 | Binding/names |
| COHSQLE052 | CS01052 | Binding/names |
| New (#1715, SQ-M1) | CS01053 | Binding/names — merge_action() outside MERGE RETURNING |
| New (#1719, SQ-R1) | CS01054 | Binding/names — Recursive definition lacks a top-level anchor UNION [ALL] recursive term (42P19) |
| New (#1719, SQ-R1) | CS01055 | Binding/names — Recursive reference in the anchor (42P19) |
| New (#1719, SQ-R1) | CS01056 | Binding/names — Recursive reference inside a scalar, IN, quantified or EXISTS subquery (42P19) |
| New (#1719, SQ-R1) | CS01057 | Binding/names — Recursive reference on the nullable side of an outer join (42P19) |
| New (#1719, SQ-R1) | CS01058 | Binding/names — Recursive reference under INTERSECT ALL (42P19) |
| New (#1719, SQ-R1) | CS01059 | Binding/names — Recursive reference in a prohibited EXCEPT operand (42P19) |
| New (#1719, SQ-R1) | CS01060 | Binding/names — Recursive term has more than one self-reference (42P19) |
| New (#1719, SQ-R1) | CS01061 | Binding/names — Aggregate at the query level containing a recursive reference (42P19) |
| COHSQLE001 | CS02001 | Values/types |
| COHSQLE002 | CS02002 | Values/types — identity or standalone sequence range exhaustion (#1717) |
| COHSQLE003 | CS02003 | Values/types |
| COHSQLE010 | CS02010 | Values/types |
| COHSQLE011 | CS02011 | Values/types |
| COHSQLE012 | CS02012 | Values/types |
| COHSQLE018 | CS02018 | Values/types |
| COHSQLE022 | CS02022 | Values/types |
| COHSQLE034 | CS02034 | Values/types |
| COHSQLE046 | CS02046 | Values/types |
| COHSQLE047 | CS02047 | Values/types |
| COHSQLE048 | CS02048 | Values/types |
| COHSQLE055 | CS02055 | Values/types |
| COHSQLE062 | CS02062 | Values/types |
| COHSQLE063 | CS02063 | Values/types |
| New (#1447) | CS02064 | Values/types — ALTER COLUMN TYPE old value, USING result or dependent DEFAULT has no assignment cast (42804) |
| New (#1719, SQ-R1) | CS02065 | Values/types — Recursive anchor, recursive output or declared column-list width mismatch (42601) |
| New (#1719, SQ-R1) | CS02066 | Values/types — Recursive UNION changes the anchor type, domain or type modifiers; cast hint (42804) |
| New (#1719, SQ-R1) | CS02067 | Values/types — Recursive UNION changes or conflicts with the anchor collation; COLLATE hint (42P21) |
| COHSQLE024 | CS03024 | Schema/constraints |
| COHSQLE025 | CS03025 | Schema/constraints |
| COHSQLE027 | CS03027 | Schema/constraints |
| COHSQLE029 | CS03029 | Schema/constraints |
| COHSQLE030 | CS03030 | Schema/constraints |
| COHSQLE031 | CS03031 | Schema/constraints |
| COHSQLE033 | CS03033 | Schema/constraints |
| COHSQLE035 | CS03035 | Schema/constraints |
| COHSQLE037 | CS03037 | Schema/constraints |
| COHSQLE038 | CS03038 | Schema/constraints |
| COHSQLE039 | CS03039 | Schema/constraints |
| COHSQLE049 | CS03049 | Schema/constraints |
| COHSQLE050 | CS03050 | Schema/constraints |
| COHSQLE053 | CS03053 | Schema/constraints |
| COHSQLE054 | CS03054 | Schema/constraints |
| COHSQLE056 | CS03056 | Schema/constraints |
| COHSQLE059 | CS03059 | Schema/constraints |
| COHSQLE061 | CS03061 | Schema/constraints |
| COHSQLE065 | CS03065 | Schema/constraints |
| COHSQLE066 | CS03066 | Schema/constraints |
| New (#1716) | CS03067 | Schema/constraints — TRUNCATE omits a required referencing table (0A000) |
| New (#1716) | CS03068 | Schema/catalog — CTAS output names are missing, blank, duplicate or have the wrong width (42601) |
| New (#1717) | CS03069 | Schema/catalog — invalid resolved sequence options (22023) |
| New (#1717) | CS03070 | Schema/catalog — occupied sequence/table/view relation name (42P07) |
| New (#1717) | CS03071 | Schema/catalog — sequence namespace is absent (3F000) |
| New (#1717) | CS03072 | Schema/catalog — sequence definition exceeds the record limit (54000) |
| New (#1717, SQ-S2) | CS03073 | Schema/catalog — sequence name or object identity is absent (42P01) |
| New (#1717, SQ-S2) | CS03074 | Schema/catalog — sequence operation in a persisted expression or declared provisioning script is unsupported (0A000) |
| New (#1717, SQ-S2) | CS03075 | Schema/catalog — setval value is outside the sequence bounds (22003) |
| New (#1717, SQ-S2) | CS03076 | Schema/expression — sequence value in an unsupported evaluation context, including NEXT VALUE FOR in RETURNING, or detached from execution (0A000) |
| New (#1717, SQ-S2) | CS03077 | Schema/expression — malformed sequence reference or runtime sequence-call argument (22023) |
| COHSQLP001 | CS03101 | Schema/constraints |
| COHSQLP002 | CS03102 | Schema/constraints |
| COHSQLP003 | CS03103 | Schema/constraints |
| COHSQLP004 | CS03104 | Schema/constraints |
| COHSQLP005 | CS03105 | Schema/constraints |
| COHSQLS001 | CS03201 | Schema/constraints |
| COHSQLS002 | CS03202 | Schema/constraints |
| COHSQLS003 | CS03203 | Schema/constraints |
| COHSQLS004 | CS03204 | Schema/constraints |
| COHSQLS005 | CS03205 | Schema/constraints |
| COHSQLD001 | CS03301 | Schema/constraints |
| COHSQLD002 | CS03302 | Schema/constraints |
| COHSQLD003 | CS03303 | Schema/constraints |
| COHSQLC001 | CS03401 | Schema/constraints |
| COHSQLT001 | CS04001 | Transactions/locking |
| COHSQLT002 | CS04002 | Transactions/locking |
| COHSQLT003 | CS04003 | Transactions/locking |
| COHSQLT004 | CS04004 | Transactions/locking |
| COHSQLT005 | CS04005 | Transactions/locking |
| COHSQLT006 | CS04006 | Transactions/locking |
| COHSQLE021 | CS04021 | Transactions/locking |
| COHSQLE044 | CS04044 | Transactions/locking |
| COHSQLE064 | CS04064 | Transactions/locking — serialization failure, including a concurrently changed MERGE target under Snapshot (#1715) |
| New (w11 review) | CS04065 | Transactions/locking — hard deadlock (`40P01`) |
| New (#1716) | CS04066 | Transactions/locking — TRUNCATE inside an explicit transaction (25001) |
| New (#1717) | CS04067 | Transactions/locking — dropped or replaced sequence incarnation (40001) |
| New (#1717, SQ-S2) | CS04068 | Transactions/session state — currval is undefined for this sequence in this session (55000) |
| New (#1717, SQ-S2) | CS04069 | Transactions/session state — lastval is undefined or its sequence was dropped (55000) |
| New (#1717, SQ-S2 fix) | CS04070 | Transactions/session state — DISCARD ALL inside an explicit transaction (25001) |
| COHSQLE004 | CS05004 | Execution/budgets |
| COHSQLE007 | CS05007 | Execution/budgets |
| COHSQLE013 | CS05013 | Execution/budgets |
| COHSQLE017 | CS05017 | Execution/budgets |
| COHSQLE032 | CS05032 | Execution/budgets |
| COHSQLE036 | CS05036 | Repeated target modification by ON CONFLICT or MERGE |
| COHSQLE040 | CS05040 | Execution/budgets |
| COHSQLE042 | CS05042 | Execution/budgets |
| COHSQLE043 | CS05043 | Execution/budgets |
| COHSQLE045 | CS05045 | Execution/budgets |
| COHSQLE057 | CS05057 | Execution/budgets |
| COHSQLE060 | CS05060 | Execution/budgets |
| COHSQLE067 | CS05067 | Execution/budgets — retained statement memory, including filtered deferred sequence inputs and sort keys |
| New (#1714) | CS05068 | Execution/budgets — UPDATE target matched by multiple source associations (before writes) |
| New (#1525, SQ-J1) | CS05069 | Execution/budgets — direct RIGHT, FULL or parenthesized joined DML source would require a different target-outer execution tree |
| New (#1525, SQ-J2) | CS05070 | Execution/capabilities — FULL JOIN lacks a safe mandatory hash equality or has an unproven ON evaluation |
| New (#1715, SQ-M1) | CS05071 | Action planning — MERGE WHEN follows an unconditional clause of the same match kind |
| New (#1720, SQ-H1) | CS05072 | Execution/placement — FILTER on a non-aggregate call (42809) |
| New (#1720, SQ-H1) | CS05073 | Execution/placement — same-scope aggregate in FILTER (42803) |
| COHSQLE041 | CS06041 | Storage/durability |
| COHSQLE058 | CS06058 | Storage/durability |

SQLSTATE meanings remain those in [the SQL dialect](../../Assimalign.Cohesion.Database.Sql.Language/docs/DIALECT.md); the Cohesion identities changed, not ISO/PostgreSQL SQLSTATE values.

## Documents migration

OQL null placement (#1730) adds CD00013 for a malformed NULLS suffix and CD01032 for an
invalid directly constructed placement. Existing CD05018 still bounds sorting; #1632 ordering
proof probes fall back on exhaustion and add no execution failure or format change.

OQL aggregate quantifiers and native aggregates (#1729, OQ-G1) add CD00011 for a scalar/star
quantifier and CD01029 for an invalid directly constructed call quantifier. Existing CD02007
covers a statistical result outside the decimal domain, CD02008 preserves SUM/AVG nonnumber refusal, CD05001 covers
aggregate placement and CD05018 covers the same statement retained-memory exhaustion condition
for DISTINCT sets and ARRAY_AGG as for RETURNING. No disk or wire identity changes.

OQL ARRAY/FIRST comprehensions (#1728) reuse CD00002/CD00005 for malformed/deep syntax,
CD01005 for missing parameters, CD02006/CD02007/CD02008 for scalar failures and CD05001 for
invalid grouping, aggregate or metadata contexts. CD01027 identifies an empty variable in a
direct AST. Existing constructor depth and surrounding retained-result budgets also apply;
no disk or wire format changes.

OQL RETURNING (#1727) preserves existing syntax, binding and value identities for malformed
projections, duplicate names, missing parameters and scalar failures. Unsupported subqueries
and ELEMENT calls retain CK00001. CD01026 rejects empty output names in direct ASTs, CD05018
identifies retained-result budget exhaustion, and CD05019 identifies serialized result depth
including the named wrapper. Existing CD05016 covers frame-size preflight. A failure
rolls back statement writes before results become visible. The existing two-count Complete
payload accepts rows plus a matching affected count without capability negotiation.

| Previous code | Code | Category |
|---|---|---|
| OQL0001 | CD00001 | Language/syntax |
| OQL0002 | CD00002 | Language/syntax |
| OQL0003 | CD00003 | Language/syntax |
| OQL0004 | CD00004 | Language/syntax |
| OQL0005 | CD00005 | Language/syntax |
| OQL0006 | CD00006 | Language/syntax |
| OQL0007 | CD00007 | Language/syntax |
| OQL0008 | CD00008 | Language/syntax |
| OQL0009 | CD00009 | Language/syntax |
| OQL0010 | CD00010 | Language/syntax |
| New (#1729, OQ-G1) | CD00011 | Language/syntax — ALL/DISTINCT on a scalar or star call |
| New (#1729, OQ-G2) | CD00012 | Language/syntax — GROUP AS without preceding GROUP BY terms |
| New (#1730) | CD00013 | Language/syntax — NULLS without FIRST or LAST |
| COHDOC002 | CD01002 | Binding/names |
| COHDOC003 | CD01003 | Binding/names |
| COHDOC005 | CD01005 | Binding/names |
| COHDOC009 | CD01009 | Binding/names |
| COHDOC014 | CD01014 | Binding/names |
| COHDOC017 | CD01017 | Binding/names |
| COHDOC023 | CD01023 | Binding/names |
| COHDOC025 | CD01025 | Binding/names |
| New (#1727) | CD01026 | Binding/names — empty RETURNING output name in a directly constructed AST |
| New (#1728) | CD01027 | Binding/names — empty comprehension variable in a directly constructed AST |
| New (#1530, OQ-I2) | CD01028 | Binding/names — array index projection or WHEN path is not rooted in its element variable |
| New (#1729, OQ-G1) | CD01029 | Binding/names — invalid aggregate quantifier or target in a directly constructed call AST |
| New (#1729, OQ-G2) | CD01030 | Binding/names — invalid grouping alias declaration, shape or collision with FROM/UNNEST |
| New (#1729, OQ-G2) | CD01031 | Binding/names — GROUP AS referenced in an aggregate's row input |
| New (#1730) | CD01032 | Binding/names — invalid null placement in a direct ordering AST |
| COHDOC006 | CD02006 | Values/types |
| COHDOC007 | CD02007 | Values/types |
| COHDOC008 | CD02008 | Values/types |
| COHDOC013 | CD02013 | Values/types |
| COHDOC016 | CD02016 | Values/types |
| COHDOC018 | CD02018 | Values/types |
| COHDOC019 | CD02019 | Values/types |
| COHDOC021 | CD02021 | Values/types |
| COHDOC026 | CD02026 | Values/types |
| New (#1530, OQ-I2) | CD02027 | Values/types — array index WHEN constant is not a supported scalar literal |
| COHDOC010 | CD03010 | Schema/constraints |
| COHDOC011 | CD03011 | Schema/constraints |
| COHDOC012 | CD03012 | Schema/constraints |
| COHDOC020 | CD03020 | Schema/constraints |
| COHDOC022 | CD03022 | Schema/constraints |
| COHDOC024 | CD03024 | Schema/constraints |
| New (#1530) | CD03025 | Schema/constraints — multiple array keys, UNIQUE array index, or INCLUDE MISSING with a leading array key |
| New (#1530) | CD03026 | Schema/constraints — encoded index definition exceeds the 8,092-byte catalog record limit; refused before posting extraction or tree creation |
| New (#1530, OQ-I2) | CD03027 | Schema/constraints — array key source, projection or WHEN is outside the restricted expression grammar |
| New (#1530, OQ-I2) | CD03028 | Schema/constraints — array index WHEN exceeds 32 levels or 128 predicate nodes |
| COHDOC004 | CD04004 | Transactions/locking |
| COHDBD001 | CD04101 | Transactions/locking |
| COHDOC001 | CD05001 | Execution/budgets |
| COHDOC015 | CD05015 | Execution/budgets |
| COHDBD002 | CD06102 | Storage/durability |
| COHDBD003 | CD06103 | Storage/durability |
| COHDBD004 | CD06104 | Storage/durability |
| COHDBD005 | CD06105 | Storage/durability |

## Graph migration

GQL path modes (#1733) retain the existing identities: malformed mode values or INSERT modes in
hand-built ASTs use CG05001; text repetition bounds above 64 use CG00005; a visited qualifying
65th hop or physical-work exhaustion uses CG05004; retained-memory exhaustion uses CG05020.
Selector execution (#1734) reuses these conditions for endpoint search, retained history states,
identity comparisons and selected output. Shortest quotas stop before longer paths; unresolved
ANY identity prefixes or exhaustive ALL WALK beyond 64 hops refuse with CG05004.
Nested explicit modes/selectors and unselected unbounded WALK use CK00001. Selected unbounded WALK and inline predicates execute within these search budgets (#1734). BEGIN [TRANSACTION] (#1725) shares START TRANSACTION's
transaction-state diagnostics; it allocates no new identity. These changes add no disk or wire format.

| Previous code | Code | Category |
|---|---|---|
| GQL0001 | CG00001 | Language/syntax |
| GQL0002 | CG00002 | Language/syntax |
| GQL0003 | CG00003 | Language/syntax |
| GQL0004 | CG00004 | Language/syntax |
| GQL0005 | CG00005 | Language/syntax |
| GQL0006 | CG00006 | Language/syntax |
| GQL0007 | CG00007 | Language/syntax |
| GQL0008 | CG00008 | Language/syntax |
| GQL0009 | CG00009 | Language/syntax |
| GQL0010 | CG00010 | Language/syntax |
| New (#1734) | CG00011 | Language/syntax — SHORTEST selector requires a positive Int64 literal count |
| New (#1736, GQ-C1 fixes) | CG00012 | Language/syntax — list slice omits both bounds; rejected during planning |
| COHDBG002 | CG01002 | Binding/names |
| COHDBG010 | CG01010 | Binding/names |
| COHDBG011 | CG01011 | Binding/names |
| COHDBG014 | CG01014 | Binding/names |
| COHDBG015 | CG01015 | Binding/names |
| New (#1734) | CG01016 | Binding/names — path search prefilter references a binding introduced by a peer path in the same MATCH, with or without an explicit selector |
| New (#1736, GQ-C1) | CG01017 | Binding/names — missing/empty comprehension iterator declaration in a hand-built AST |
| COHDBG009 | CG02009 | Values/types |
| COHDBG013 | CG02013 | Values/types |
| New (#1736, GQ-C1) | CG02014 | Values/types — invalid list-operation source or numeric slice-bound kind/range |
| COHDBG003 | CG03003 | Schema/constraints |
| New (#1735, GQ-M2) | CG03004 | Constraints — MERGE equality is null, non-finite or non-scalar |
| New (#1735, GQ-M2) | CG03005 | Constraints — bound MERGE node is null, absent or incompatible with its node pattern |
| New (#1735, GQ-M2 fixes) | CG03006 | Constraints — repeated or already-bound MERGE node has labels or properties |
| New (#1735, GQ-M2 fixes) | CG03007 | Constraints — MERGE relationship variable is already declared before the clause |
| COHDBG007 | CG04007 | Transactions/locking |
| COHDBG022 | CG04022 | Transactions/locking |
| COHDBG023 | CG04023 | Transactions/locking |
| New (#1735, GQ-M1) | CG04024 | Transactions/locking — retryable stale Snapshot MERGE pattern decision |
| COHDBG001 | CG05001 | Execution/budgets |
| COHDBG004 | CG05004 | Execution/budgets — physical work or qualifying 65th hop, including selector search |
| COHDBG008 | CG05008 | Execution/budgets |
| COHDBG020 | CG05020 | Execution/budgets — retained statement memory, including selector history and output |
| New (#1536) | CG05022 | Execution — selected shortest-path record is absent while reconstruction's operation remains active |
| New (#1735, GQ-M1) | CG05023 | Execution — invalid bound MERGE pattern lock identity |
| New (#1736, GQ-C1) | CG05024 | Execution — aggregate inside a list comprehension predicate or projection |
| COHDBG006 | CG06006 | Storage/durability |
| COHDBG012 | CG06012 | Storage/durability |
| COHDBG017 | CG06017 | Storage/durability |
| COHDBG018 | CG06018 | Storage/durability |
| COHDBG005 | CG07005 | Protocol/transport |
| COHDBG016 | CG07016 | Protocol/transport |
| COHDBG021 | CG08021 | Configuration |
| COHDBG019 | CG09019 | Lifecycle |

## Blob migration

| Previous code | Code | Category |
|---|---|---|
| COHDBB007 | CB02007 | Values/types |
| COHDBB001 | CB04001 | Transactions/locking |
| COHDBB002 | CB06002 | Storage/durability |
| COHDBB003 | CB06003 | Storage/durability |
| COHDBB004 | CB06004 | Storage/durability |
| COHDBB005 | CB06005 | Storage/durability |
| COHDBB006 | CB06006 | Storage/durability |

## KeyValuePair migration

| Previous code | Code | Category |
|---|---|---|
| COHKV001 | CV00001 | Language/syntax |
| COHKV003 | CV00003 | Language/syntax |
| COHKV004 | CV00004 | Language/syntax |
| COHKV005 | CV00005 | Language/syntax |
| COHKV006 | CV00006 | Language/syntax |
| COHKV007 | CV00007 | Language/syntax |
| COHKV008 | CV00008 | Language/syntax |
| COHKV009 | CV00009 | Language/syntax |
| COHKV010 | CV00010 | Language/syntax |
| COHKV011 | CV00011 | Language/syntax |
| COHKV016 | CV00016 | Language/syntax |
| COHKV018 | CV00018 | Language/syntax |
| COHKV017 | CV01017 | Binding/names |
| COHKV012 | CV02012 | Values/types |
| COHKV013 | CV02013 | Values/types |
| COHKV014 | CV02014 | Values/types |
| COHKV015 | CV02015 | Values/types |
| COHKV002 | CV03002 | Schema/constraints |
| COHKV019 | CV04019 | Transactions/locking |
| COHDBK001 | CV04101 | Transactions/locking |
| COHDBK002 | CV06102 | Storage/durability |
| COHDBK004 | CV06104 | Storage/durability |
| COHDBK005 | CV06105 | Storage/durability |
| COHDBK006 | CV06106 | Storage/durability |

## Rules-audit condition additions

These identities fill previously empty diagnostic carriers. Existing messages, public exception types,
SQLSTATEs, wire error categories and storage formats remain unchanged unless a SQLSTATE is shown.
The CK conditions are shared where all models perform the same operation. Framework argument
and invariant exceptions and lower child-root typed failures retain their documented contracts.

| Code | Condition | SQLSTATE |
|---|---|---|
| CK02001 | Row accessor requires another value type | — |
| CK02002 | Required row value is NULL | — |
| CK03001 | Logical database or storage already exists | — |
| CK03002 | Logical database is absent | — |
| CK04001 | Requested isolation level is unsupported | — |
| CK04002 | Statement operation has already completed; MERGE includes session end during writer/parent/pattern waits or late grants while the database is live (database disposal retains CG09019) | — |
| CK04003 | Active statement apply bracket is absent | — |
| CK04004 | Session already has an active transaction | — |
| CK04005 | Transaction has already ended | — |
| CK04006 | Transaction commit or rollback is in progress | — |
| CK04007 | Running operation prevents transaction commit | — |
| CK06004 | Interrupted creation/drop left an orphan directory | — |
| CK06005 | Physical data or catalog file set is absent | — |
| CK07003 | Session operations overlap | — |
| CK07004 | Session is closed | — |
| CS06059 | Persisted SQL catalog metadata is malformed | XX001 |
| CS06060 | SQL catalog metadata format/version is unsupported | 0A000 |
| CS06061 | Standalone sequence catalog definition or reservation is malformed | XX001 |
| CD04102 | Catalog snapshot conflict | — |
| CD04103 | Expected document version differs | — |
| CD04104 | No explicit document transaction is open for COMMIT or ROLLBACK (#1725) | — |
| CD04105 | OQL collection/index DDL inside an explicit transaction (#1725) | — |
| CD05016 | Result byte/frame limit exceeded | — |
| CD05018 | Retained SELECT sources/groups, native aggregate state/values, DML pending buffers or RETURNING results exceed the statement memory budget (#1727, #1729, #1753, #1755) | — |
| CD05019 | Serialized RETURNING value exceeds 256 levels, including the named projection wrapper (#1727) | — |
| CD07001 | Session belongs to another database | — |
| CD07002 | Text transaction control requires the negotiated transaction-status capability (#1725) | — |
| CD09001 | Collection handle is stale | — |
| CB01001 | Container is absent | — |
| CB01002 | Blob is absent | — |
| CB03001 | Container already exists | — |
| CB03002 | Blob exists while overwrite is disabled | — |
| CB04002 | Catalog snapshot conflict | — |
| CB09001 | Container handle is stale | — |
| CB09002 | Engine cannot start the requested server | — |
| CV05001 | Put execution produced no outcome row | — |
| CV05002 | Executor request kind is unsupported | — |
| CV06107 | Entry-space format is unsupported | — |
| CV07001 | Session belongs to another database | — |

## Developer function conditions

Engine builders register application functions in the same frozen per-engine catalog as native
functions. Callback failures retain the original exception as their inner exception. At the function
boundary, existing Database, storage and timeout failures keep their identity. Engine execution
retains its existing failure classification and statement cancellation contract. These additions
change no persisted format, wire frame, protocol version, or SQLSTATE. Function-name and overload
failures use the binding category; return-type failures use the value category.

| Code | Condition | SQLSTATE |
|---|---|---|
| CG05021 | Graph application function callback failed | — |
| CD05017 | Document application function callback failed | — |
| CV00019 | CALL is missing its function name | — |
| CV01018 | Key-value function is absent from the engine catalog | — |
| CV01019 | No key-value function overload accepts the argument count | — |
| CV01020 | No key-value function overload accepts the exact argument types | — |
| CV01021 | More than one key-value function overload accepts the NULL-compatible arguments | — |
| CV02016 | Key-value function result violates its declared return type | — |
| CV05003 | Key-value application function callback failed | — |
