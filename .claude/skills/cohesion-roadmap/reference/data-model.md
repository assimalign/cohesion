# Roadmap data model: `cohesion.database.roadmap/2`

Field-by-field reference for `roadmap-data.json`, with **where each field comes from**. The
machine-readable twin is [roadmap-schema.json](roadmap-schema.json) (JSON Schema 2020-12). The script
validates its own output before writing; the schema is for anyone checking a file it did not produce.

The model in one paragraph: the horizontal axis is tier bands (Landed, Gate, T1, T2, T3, Unplaced),
never time. Dependency depth orders work inside a band, and in T2 the plan's listing order does too.
Untiered work is drawn across the window of its Wave, else its Priority. Every work item gets exactly one
placement. The board places every item; carried data fills only what the board cannot hold yet, and
says so.

Sources are abbreviated:

| Tag | Meaning |
| --- | --- |
| **board** | `board-snapshot.json`: issue pages, Project #13 fields, sub-issue links ([queries.md](queries.md)) |
| **board-field** | a Project #13 single-select field read from the issue page (Status, Priority, Wave, Category, Tier, Program, Area, Kind, Origin) |
| **body** | an issue body: dependency sections and lines, `Related work items: blocked-by #N`, the plan-family line |
| **native** | GitHub's native blocked-by links, read in one batch on every run |
| **tiers** | `data/tiers.json`: the plan (families in listing order, tiers, plan decisions, the plan's own ordering edges) |
| **programs** | `data/programs.json`: program definitions (board option, shape, tracking or gate issue) |
| **tax** | `data/taxonomy.json`: category definitions; the per-item mapping is a transition fallback only |
| **overlay** | `data/overlay.json`: lanes, stage names, prose-only program-gate edges, pins, window pins, open calls |
| **bug-policy** | `data/bug-policy.json`: the bug policy, B1–B6 with mechanics, and its pending extensions |
| **realign-plan** | `data/realign-plan.json`: the approved realignment's decision record, windows and checks (Changes tab only) |
| **realign-target** | `data/realign-target.json`, when the realign executor has exported it (Changes tab only) |
| **computed** | derived by the script; never stored upstream |

---

## 1. Document

| Field | Type | Source | Notes |
| --- | --- | --- | --- |
| `schema` | const `cohesion.database.roadmap/2` | — | a `/1` document or snapshot is refused |
| `generated` | date | computed | the run date (`-AsOf` overrides) |
| `generatedBy` | string | computed | names the script and restates that it is read-only |
| `asOf` / `snapshotAt` | date / timestamp | board | when the board was read; `asOf` ≠ `generated` raises `stale-asOf` |
| `sources.inputs[]` | array | computed | one row per input: the snapshot and every carried file present, with `schema`, `sha256`, `generated` |
| `sources.carried` | object | computed | what the carried data placed this run: `categoryFromCarried`, `programFromCarried`, `tierFromField`, `tierFromPlan`, whether each field exists, and the sentence the page prints |
| `sources.plan`, `integrationBranch`, `integrationPr`, `codeBaseline` | | tiers, programs | the plan this roadmap renders; what `delivered-unmerged` means |
| `legend.tiers` / `statuses` / `placements` / `decisions` | | tiers, computed | `decisions` is `tiers.decisions`, D1–D6 plus the 2026-10-16 clarifications D4a and D5a |
| `placement` | object | computed | the window rule, every Wave and Priority window, band spans and offsets (§5) |
| `stages[]`, `lanes[]`, `categories[]` | | §4, §6, §7 | |
| `nodes[]`, `edges[]` | | §2, §3 | |
| `programs[]`, `milestones` | | §8 | |
| `criticalPath`, `nextUp`, `contradictions[]`, `hygiene[]` | | §9 | |
| `conflicts[]` | array | tiers | items filed in two homes (five T2 families under Hardening, plus #1537) |
| `openCalls[]` | array | tiers, overlay, bug-policy, programs | decisions not made yet; each names the nodes it would move |
| `overlay` | object | overlay | `applied[]` (every pin and window pin that fired) and `unmatched[]` |
| `taxonomy`, `bugPolicy` | object | §10 | the two data-driven tabs |
| `realign` | object, optional | §10 | the Changes tab, present when `realign-plan.json` or `realign-target.json` exists |
| `changes` | object, optional | §11 | what moved since `-Previous` |
| `counts` | object | computed | §12 |
| `warnings[]` | array | computed | §13 |

---

## 2. `nodes[]`

One node per **work item**, per gate program, per untracked program phase, per family-tier slot with no
filed item, plus the T1 milestone. **Containers produce no node:** the root #1067, the epics (direct
sub-issues of the root with a 3-segment code, never a gate), category issues (`[L03.02.NN.Cxx]`) and
program gates (which get a `gate` node instead, §8).

| Field | Type | Source | Rule |
| --- | --- | --- | --- |
| `id` | string | computed | `i<number>`, `slot:<family>:<tier>`, `phase:<program>:<key>`, `ms:t1-complete` |
| `kind` | enum | computed | `program` (a tree program's tracking issue) · `feature` (has sub-issues) · `task` · `slot` · `milestone` · `gate` |
| `number`, `url`, `wbs`, `title`, `shortTitle` | | board | `wbs` parses `^\[(L\d{2}(?:\.(?:\d{2,}\|C\d{2}))+)\]`: serials of any width and `Cxx` segments |
| `lane` | string | computed | plan language for tiered items → the category's lane → the epic's lane → `other` (§6) |
| `band` | enum | computed | `landed gate t1 t2 t3 unplaced withdrawn out` |
| `category`, `categorySource` | string | board-field → board → tax → computed | Category field (option name = category title minus `[code] `) → a category issue as direct parent (`tree`) → carried mapping (`carried`) → nearest categorised ancestor (`inherited`) → `none` |
| `epic` | string | computed | the category's code prefix → a tree walk to an epic → the WBS prefix (`epic-from-prefix`) |
| `programs[]`, `program`, `programSource` | | board-field → board → native → tax/programs | §8 |
| `family`, `tier`, `tierSource`, `planLanguage` | | board-field → tiers → body | Tier field (via `tierBoardOptions`) → plan item → family → body line ``Tier N family `slug` `` (`family-line`) → nearest tiered ancestor (`inherited`) → none. A field that disagrees with the plan raises `tier-field-vs-plan`; the field wins. A lone `T1/T2/T3` token in a body never sets a tier (`tier-suggested-by-body`) |
| `planRank` | object | tiers | T2 only: `{ language, rank, of }`, the family's place in its language's T2 list |
| `placement` | enum | computed | §5: `withdrawn landed tier tier-inherited prerequisite dependency wave priority bars unplaced out gate milestone` |
| `window` | object | computed | for `wave`/`priority` placements: `{ field, value, lo, hi }` |
| `unplacedReason` | string | computed | `no-tier-wave-priority-or-dependency` · `feature-without-placed-children` |
| `stage`, `stageEnd` | int | computed | the column and the right edge of a bar or window. Footer nodes (`withdrawn`, `out`) carry `-1` |
| `stageSource` | enum | computed | `dependency` · `sequence` (deepest predecessor is a plan-sequence edge) · `band-floor` · `window` · `children` · `overlay` · `none` |
| `stageExplanation` | string | computed | the position in words. **Required on every node** |
| `status` | enum | board | Done → `delivered` (CLOSED as not planned or duplicate → `withdrawn`), In review → `delivered-unmerged`, In progress, Ready, Backlog; slots `unfiled`; tier Out `out`; a gate whose members are all closed `awaiting-evidence` |
| `state`, `stateReason`, `delivered` | | board | `delivered` is true for `delivered` and `delivered-unmerged` only; never for `withdrawn` |
| `blocked` | bool | computed | any `depends-on`, `plan-order` or `program-gate` predecessor (inherited edges included) not delivered, or an open external blocker. `plan-sequence` never blocks |
| `priority`, `wave`, `area`, `areaSource`, `boardKind`, `labels`, `issueType`, `isBug` | | board-field, board, tax | `area` falls back to the carried mapping |
| `parent`, `children[]` | | board | sub-issue links; containers are left out of `children` |
| `externalBlockers[]` | array | board | dependency targets outside L03.02, with their state and board Status (today #1087 → #1086) |
| `downstreamOpen` | int | computed | open items transitively waiting on this one (blocking edge kinds) |
| `onCriticalPath`, `conflict`, `pinned` | | computed, tiers, overlay | |
| `hasOpenDefect`, `openDefects` | bool, int | computed | on a delivered or delivered-unmerged item with an open child that has the `bug` label or issue type Bug. The item is never reopened (B5) and its bar is never stretched |
| `complete` | bool | computed | features: every placed descendant delivered. Never derived from the parent's own state |
| `label`, `gateMembers[]`, `gateOpenMembers` | | programs, computed | gate nodes only |
| `note`, `evidence` | string | tiers, programs | plan or program prose for the tooltip |

---

## 3. `edges[]`

`from` **waits on** `to`. Arrowheads point from the dependent to its prerequisite.

| Kind | Sources | Positions? | Blocks? |
| --- | --- | --- | --- |
| `depends-on` | `body:section`, `body:line`, `body:related`, `native`, `program-phase` | yes, inside a band | yes |
| `plan-order` | `plan-D5`, `plan-tier-order`, `owner-2026-10-16` (tiers) | yes, inside a band | yes |
| `plan-sequence` | `plan-D4`, derived per language from `tiers.families` order (D4a) | yes, inside T2 | **never** |
| `program-gate` | `overlay:tierRelation` | no | yes |
| `gate-member` | `gate-link`, `program-field` or `carried-program` | no | no; drawn on demand |
| `child-of` | `board-tree` | no | no |
| `conflict` | `tiers.conflicts` | no | no |

- **The dependency grammar** (§2.3 of the skill change): a `## Dependencies` / `Depends on` / `Blocked by` /
  `Prerequisites` section contributes every `#N`, and every `[WBS]` that resolves to exactly one issue;
  a line starting `Blocked by` or `Depends on` contributes its `#N`; a `Related work items:` line
  contributes only its `blocked-by #N` tokens. Self-references, refs that are not issues, and ambiguous
  WBS refs are dropped and counted in the snapshot (`dependencyDrops`).
- **One edge per (from, to, kind).** A second source for the same pair lands in `alsoFrom[]`.
- **Endpoints.** A container endpoint is dropped and counted (`counts.edgesIgnored.container`). A target
  outside the subtree becomes the node's `externalBlockers[]` entry, never an edge. A gate's own native
  blocked-by links become `gate-member` edges.
- **Inheritance.** A `depends-on`, `plan-order` or `program-gate` edge from a node with sub-issues to a
  node without them is copied to every descendant, with `source: inherited:<source>` and
  `inheritedFrom`. Feature → feature edges are drawn and never inherited.
- **Positioning works on leaves.** An edge into a feature waits on all of that feature's leaves.
- `crossLane`, `crossBand`, `critical` are drawing hints.
- **`decision-edge-on-board`** is raised when a `tiers.json` `plan-D5` or `owner-*` edge also exists as a
  native link: the carried edge can then be deleted.

---

## 4. `stages[]`

`{ id, index, name, band, localDepth, nodeCount, bandFloorCount, alongsideCount, named?, entryCondition?, exitCondition? }`

- Bands run `landed, gate, t1, t2, t3, unplaced`. A band's span is `1 + max local depth` of its leaves
  (Landed is always 1; Unplaced is 1 when anything is unplaced, else 0). The column count is an output.
- `id`, `name` and the two conditions come from `overlay.stages`, keyed by `band/localDepth`. An entry
  with `localDepth: "*"` names every column of that band that has no entry of its own, with `{n}` =
  `localDepth + 1` (`named: "pattern"`). A column with no name at all raises `no-stage-name`.
- `nodeCount` counts the column's own members: not bars, gates, milestones or window placements.
  `alongsideCount` counts the window placements whose window covers the column ("+N scheduled
  alongside"). `bandFloorCount` counts members that sit at the band floor because nothing orders them,
  which is unknown ordering, not readiness.
- The Unplaced column is a real band. Its `nodeCount` equals `counts.unplaced.total` for work items plus
  any unplaced feature, and the validator checks it.

---

## 5. Placement: every work item gets exactly one

The first rule that applies wins:

| # | Placement | When | Position |
| --- | --- | --- | --- |
| 1 | `withdrawn` | CLOSED as NOT_PLANNED or DUPLICATE | the footer; never Done |
| 2 | `landed` | delivered with no plan tier, or tier `Done` | column 0 |
| 3 | `tier` | a tier from the field, the plan, the family or the body family line | band + in-band depth (T2: + plan sequence) |
| 4 | `tier-inherited` | no tier, a tiered non-container ancestor | as 3 |
| 5 | `prerequisite` | untiered, open tiered work waits on it (directly or through other untiered open items), and its own window would start no earlier than that work | pulled into the band of the earliest such work, ahead of it, at its in-band depth |
| 6 | `dependency` | untiered, no Wave or Priority, a placed open predecessor | `[floor, floor]`, floor = predecessor `stageEnd` + 1 |
| 7 | `wave` | untiered with a Wave | its Wave window, `lo` clipped up to the floor |
| 8 | `priority` | untiered, no Wave, a Priority | its Priority window, an estimate, same clip |
| 9 | `bars` | a feature or program root | spans its placed open sub-issues; landed sub-issues count toward completion, never the span; no placed sub-issue → its own window; every placed sub-issue delivered → column 0 |
| 10 | `unplaced` | none of the above | the Unplaced column, with `unplacedReason` |

**The window rule** (`placement.windows`, recomputed every run): a value's window is the span of stages
its open, undelivered, tiered leaves occupy (gate to T3), after dropping any band that holds under 10% of
them. Fewer than 3 such items: the whole of their bands. None: the band after the previous value's
window. A window never starts before the previous value's window, and never past the last T3 column.
`overlay.waveWindows` can pin one (`{ "bands": [...], "reason": "..." }`), echoed in `overlay.applied`.
Each window records `{ value, field, lo, hi, basis, n, droppedBands[], pinned }`.

**Wave places; Priority orders.** Priority places an item only when it has no Wave.

**Prerequisite pull (rule 5).** The layout runs once without it, decides the pulls from that first pass,
and runs again with the pulled items as members of their band. A successor's position never caps a
window otherwise. A tiered item that still waits on an untiered item placed at or after it raises the
`scheduled-after-dependent` contradiction. On 2026-10-16 the pull placed #1577, #1578 and #1579 (the
execution seam) in the gate band, ahead of #190 and the T1 work their rename task blocks. It is a
generalisation of `skill-changes.md` §2.7, which applied no successor ceiling at all and would have drawn
the seam after the work that waits on it.

---

## 6. `lanes[]`

`{ id, name, order, epics?, epicIssues?, categories?, planLanguage?, collapsedByDefault?, familyCount?, itemCount }`
— taken from `overlay.lanes` in array order, which is lane order. The lane map is keyed by epic WBS and
category code, never by issue number. An epic with no lane raises `epic-without-lane` and its items land
in `other`.

## 7. `categories[]`

Every category issue on the board (`source: live`), plus every `taxonomy.json` category with no issue yet
(`source: proposed`): `{ id, name, epic, kind, precedence, source, issue, lane, itemCount, gateCount,
openCount, deliveredCount, withdrawnCount, slotCount, inTreeCount, items[], definition, boundaries[],
owningCode?, carriedName? }`. `itemCount` counts work items only; slots are `slotCount`.

---

## 8. Programs, gates and milestones

- **`programs[]`**: `{ id, name, boardOption, shape, source, members[], memberCount, fieldCount,
  treeCount, carriedCount, trackingIssue?, gate? }`.
  - A **tree** program's members are its Program-field items plus its tracking issue's subtree.
  - A **gate** program's link set is the gate's native blocked-by items, every descendant of those, and
    the gate itself. Its field set is Program = its option. The two must agree:
    `gate.membershipCheck { checked, basis, onlyField[], onlyLinks[] }`, and any difference raises
    `program-membership-mismatch` plus one hygiene row per item. The check runs once either the field or
    the links exist; before that it says why it did not run.
  - **Carried membership** (`program-from-carried`) applies only while no item carries a Program value:
    the carried mapping's `program`, then the `programs.json` lists for items the mapping does not name.
  - A node keeps every program in `programs[]`; `program` is the field value, else the carried value,
    else the first.
- **Gate nodes** (`kind: gate`, id `i<gate>`): drawn at the last column their placed members reach, in
  their category's lane. `delivered` only when the gate is CLOSED as completed; `awaiting-evidence` when
  open with every member closed; otherwise its board Status.
- **`milestones.t1Complete`**: drawn at the first T2 column. `remaining` = open T1 leaves plus open bug
  descendants of T1 items; `status` open or complete; `sinks` and `chains` are the longest chains through
  the gate and T1 bands, read from the data. `milestones.gates[]` summarises each gate node.

---

## 9. What will be worked on

- **`criticalPath`**: `{ chains[][], length, nodeCount, sinks[], truncated, edgeSets, blockers[] }`. Every
  maximal chain of `depends-on` and `plan-order` edges that are not inherited, between leaves in the gate
  and tier bands: `len[id] = 1 + max(len[pred])`, keeping every predecessor that reaches the maximum,
  enumerated back from the sinks, capped at 24 (`critical-path-truncated`). `blockers` is the top 12 by
  `downstreamOpen`, each with a note saying whether the blocker is a merge or unstarted work. **Quote
  `edgeSets` with any blocker number.**
- **`nextUp`**: `{ rule, total, top[25] }`. Open, undelivered leaves that are placed and not blocked,
  sorted by Priority, then Wave (missing last), then stage, then number: the workflow's dependency →
  Priority → Wave. Each row carries `why`.
- **`contradictions[]`**: `{ code, node, detail, fix }` with codes `ready-but-blocked`,
  `wave-tier-conflict`, `priority-wave-conflict`, `scheduled-after-dependent`. Board facts, listed, never
  fixed here.
- **`hygiene[]`**: `{ code, issue, detail, fix }`, the same definitions as tooling E15:
  `direct-under-categorised-epic` (open items only), `missing-category` (once the field exists),
  `missing-area` (once Area has Database options), `language-surface-without-tier` (open items in
  L03.02.01.C07, L03.02.02.C01, L03.02.03.C01, L03.02.05.C01), `parent-near-cap` (95+ sub-issues),
  `duplicate-wbs`, `program-membership-mismatch`, `no-priority-or-wave`.

---

## 10. The data-driven tabs

- **`taxonomy`**: `{ state (proposed | live), stateNote, source, approvedOn, categoryCount, epics[],
  rules, precedence[], boundaries, retired[], optionCeiling, newFields[], fieldOptions, hardening, carried,
  treeVersusField { count, items[], note }, emptyCategories[] }`. `state` turns `live` once one category
  issue exists. The carried per-category counts are ignored; the page computes them.
- **`bugPolicy`**: `bug-policy.json` verbatim, plus `stats { openBugs, bugChildrenOfClosed,
  bugsUnderEpicOrCategory, bugsUnderWithdrawn, hasOpenDefectParents, byPlacement, items }`.
- **`realign`** (Changes tab): `{ source, approvedOn, approvedBy, executorState, verifiedAsOf, headline,
  detail[], summary { byStage, windowsRun, windowsStarted, windowCount, complete, verifiedAsOf,
  corrections, seam }, windows[], decisions[], seam, windowsNote, rows[], journal }`.
  - Every row is `{ stage, op, issue, target, current, state, note? }`, where state is `done`,
    `pending` or `diverged`, read from the snapshot.
  - **Board corrections and the seam** are explicit checks in `realign-plan.json`.
  - **Window operations** come from `realign-target.json` when the executor has exported it. Until then
    they are derived from the approved carried data: category creates per epic, re-parents from the
    mapping's `plannedParent`, Category/Area/Program writes from the mapping, Tier writes from the plan,
    the gate's links and body append, Kind on #1511, and issue types.
  - Field rules: **done** when the board holds the target; **pending** when the board is empty or still
    holds the approval-time value (`from`); **diverged** otherwise.
  - The executor journal (`-RealignStateDir`) adds `lastApplied` per window. It never decides done.

## 11. `changes` (with `-Previous`)

`{ previous, moved[], added[], removed[], statusChanged[], unexplained }`. Each moved row is
`{ id, from { stage, stageEnd, placement }, to {...}, reasons[] }` with reasons from `tier`,
`placement`, `status`, `wave`, `priority`, `pin`, `window-recalibrated`, `band-resized`, `dependency`.
A move with no reason is `unexplained`, which means something broke. "A new item never moves an
existing one left" no longer holds: windows recalibrate and bands resize, and every move is explained
instead.

---

## 12. `counts`

| Key | Definition |
| --- | --- |
| `boardItems` | every subtree issue |
| `containers` | `{ root, epics, categories, gates, total }` |
| `workItems` | `boardItems − containers.total` |
| `open` | `{ total, byPlacement }`: open, undelivered, not withdrawn work items; `byPlacement` sums to `total` |
| `byPlacement` | every work item by placement |
| `delivered`, `deliveredUnmerged`, `withdrawn` | work items by status |
| `unplaced` | `{ total, byReason, items }`: **the only Unplaced number**; header, column and warning all read it |
| `t2` | `{ mode, steps { SQL, OQL, GQL, Shared }, planSequenceEdges }` |
| `families`, `familiesDistinct`, `familyTierPairs`, `familiesDeclared`, `familyAliases`, `filedItems`, `unfiledSlots`, `slotNodes`, `familiesWithNothingFiled` | over the node array; `sql-parameters` and `sql-explain` fold into `x-parameters` and `x-explain` |
| `byStatus`, `byLane` | work items |
| `nextUpTotal`, `contradictions`, `hygiene` | block sizes |
| `placementSources` | `{ category, tier, program, area }`: how many work items each source placed |
| `fieldsPresent`, `fieldsDefined` | board field values present; which fields exist on the project |
| `edges`, `edgesByKind`, `inheritedEdges`, `edgesIgnored`, `pulledPrerequisites`, `stages`, `nodes` | |

## 13. `warnings[]`

`{ code, message, node?, detail?, count?, nodes[]? }`. Aggregate codes carry `count` and `nodes`.

- **Transition (expected until the field pass):** `category-from-carried`, `program-from-carried`.
- **Model:** `category-inherited`, `uncategorised`, `category-option-unmatched`, `category-tree-mismatch`,
  `category-misparented`, `tier-field-vs-plan`, `tier-inherited`, `tier-option-unmatched`,
  `tier-suggested-by-body`, `t2-familyless`, `unknown-family-line`, `program-option-unmatched`,
  `program-membership-mismatch`, `epic-without-lane`, `epic-from-prefix`, `duplicate-epic`,
  `decision-edge-on-board`, `critical-path-truncated`, `scheduled-after-dependent`, `unplaced`,
  `unparented`, `gate-missing`.
- **Inputs:** `cycle`, `unresolved-dependency`, `stale-overlay-pin`, `no-stage-name`, `stale-asOf`. A
  snapshot with another schema is refused outright ("re-collect: /1 has no board fields or blocked-by").

Contradictions and hygiene rows are **blocks, not warnings**, so the warning list stays about the model.

---

## 14. How a build runs

Deterministic, in order:

1. **Collect** (read-only): issue pages with board fields, the subtree, bodies re-read when `updatedAt`
   changed, native blocked-by for every item that has one, the project's field options.
2. **Parse** the dependency grammar, the plan-family line and WBS codes.
3. **Containers**: root, epics, categories, gates.
4. **Resolve** category, epic, tier, programs, status, kind and lane per item.
5. **Slots** for plan families with no filed node, with T2 plan ranks.
6. **Edges**, then inheritance, then the leaf-level positioning graph.
7. **Bands** from tiers and status.
8. **Depth** inside each band over leaves (`depends-on`, `plan-order`, `plan-sequence`).
9. **Windows** from the tiered leaves.
10. **Ladder** for untiered leaves, predecessors first; then the **prerequisite pull** and a second layout.
11. **Bars** (open sub-issues), the **Unplaced** column, the **T1 milestone** and **gate nodes**.
12. **Derived**: blocked, downstreamOpen, critical path, blockers, next up, contradictions, hygiene.
13. **Blocks**: stages, lanes, categories, counts, warnings, open calls, taxonomy, bug policy, realign,
    changes.
14. **Validate**, then write atomically.

**Regeneration contract.** Same inputs, same output apart from `generated`: nodes sort by
(stage, lane order, number, id), edges by (from, to, kind), every tally is ordered, and no timestamp
appears inside the node array.

---

## 15. The page payload

`roadmap.html` embeds a compact projection in `<script type="application/json" id="payload">`:
`meta`, `legend`, `placement`, `stages`, `lanes`, `counts`, `criticalPath`, `conflicts`, `openCalls`,
`warnings`, `programs`, `milestones`, `nextUp`, `contradictions`, `hygiene`, `taxonomy`, `bugPolicy`,
`realign`, `changes`, `overlay`, `categories`, plus `nodes` and `edges` with short keys:

| Node key | Field | Node key | Field |
| --- | --- | --- | --- |
| `i` | id | `pl` | placement |
| `n` | number | `wf` / `wl` / `wh` | window value, lo, hi |
| `k` | kind | `ur` | unplacedReason |
| `w` | wbs | `s` / `se` / `ss` / `sx` | stage, stageEnd, stageSource, stageExplanation |
| `t` | title | `st` / `gh` | status, GitHub state |
| `ln` / `bd` | lane, band | `d` / `b` / `u` | delivered, blocked, untracked |
| `c` / `cs` | category, categorySource | `pr` / `wv` / `ar` | priority, wave, area |
| `e` | epic | `it` / `bug` | issue type, isBug |
| `pgs` / `pg` / `ps` | programs, program, programSource | `p` / `ch` | parent, children |
| `fm` | family | `xb` | external blockers `[{ n, t, gh, st }]` |
| `tr` / `tsx` / `lg` | tier, tierSource, planLanguage | `dn` / `cp` / `cf` | downstreamOpen, onCriticalPath, conflict |
| `rk` | plan rank `[language, rank, of]` | `hd` / `pin` / `cm` | open defects, pinned, complete |
| `lb` / `gm` | gate label, gate members | `nt` | note |

Edges: `f`, `t`, `k`, `s` (source), `x` (cross-lane), `c` (critical), `ih` (inherited from), `nt`.
`child-of` edges are left out; `p` rebuilds them. `New-RoadmapPayload` in the script and
`assets/roadmap-template.html` are one contract: change both together and record the new hashes in §16.

---

## 16. Installed asset and input hashes

Check these before trusting that a regeneration reproduces a reviewed page; `sources.inputs[]` in every
generated `roadmap-data.json` carries the data hashes too.

| File | sha256 |
| --- | --- |
| `assets/roadmap-template.html` | `bf88e0bbf1a396870c43bf413e7824fff481b73862045aa9b53ac38d5f5d2fca` |
| `data/tiers.json` | `d76a96111211353ad0323cdc8fe12fa24d88786bd346abc63b9108aac0560c38` |
| `data/programs.json` | `cadcba5b54b999bf1dcb2043a3c005da7b3be8a2ab114a1df0419e480ecfe5a9` |
| `data/taxonomy.json` | `db2030184e9749f5e772be4a32ee644d9523c6c35df0e148b411664539364614` |
| `data/overlay.json` | `847171afb97652ad60b0b6a3a61db8b76abaa33e497fb8b18d1e2a6d741b0a4d` |
| `data/bug-policy.json` | `bbf6bbfdc1995efb5ba4b453ae163074b6c2c0c8a81b6d5d93e06a4349b98651` |
| `data/realign-plan.json` | `268d0a6c0a28ef44f3c5303bd7b5fe007384009726bbc6fd3273e7ada7d8c301` |

Recorded 2026-10-16. Update this table in the same change as any edit to those files.
