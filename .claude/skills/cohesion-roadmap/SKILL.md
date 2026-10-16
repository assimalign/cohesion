---
name: cohesion-roadmap
description: Regenerate the Data Platform waterfall roadmap. It places Project #13's L03.02 subtree against the query-language tiers T1/T2/T3, reads the live board, and writes a self-contained HTML page plus its JSON dataset. It reads the Category, Tier, Program, Status, Priority and Wave fields, issue-body dependencies and native blocked-by links. Read-only. Use it when asked to "rebuild the roadmap", "refresh the waterfall", "what is next / what will be worked on", "where does this item sit", "what blocks the most work", "show T1 against T2", or "has the realignment run", and after new items are filed under L03.02. Answer "when is SQL/OQL/GQL done" and "what is next" from a regenerated dataset, not from memory. Only for assimalign/cohesion, Project #13, the L03.02 Data Platform tree.
---

# Cohesion Data Platform roadmap

Rebuilds the waterfall from the live board. One command produces `roadmap-data.json` (the auditable
dataset) and `roadmap.html` (the page). **The board places every item. No position is hand-set.** The
carried files in `data/` hold only what the board cannot: the plan, program and category definitions,
naming, and the approved realignment record.

```pwsh
.claude/skills/cohesion-roadmap/scripts/Get-CohesionRoadmapData.ps1 -OutDir <dir> -Html
```

Sanity check: run with `-Previous <the last roadmap-data.json>` and read `changes`. Every moved, added or removed
item carries reason codes. A move without a reason, or a large change that neither `warnings` nor `changes`
explains, means something broke. Do not compare against counts written here: they go stale with the next filing.

## Read-only, always

This skill **never mutates GitHub**. It runs GraphQL queries and `gh` reads only. It never creates, edits,
closes or labels an issue, never links a sub-issue or a dependency, never writes a project field and never
runs git.

The roadmap works in every board state: before, during and after the Data Platform realignment. The
Changes tab reports realignment progress *computed from the board*. It never applies anything. Board
changes go through `cohesion-work-items`, and only with the owner's explicit instruction for each change.

## The model (read this first)

- **The axis is tier bands, not time.**
  - The bands are landed, gate, T1, T2 and T3, plus an Unplaced column.
  - Dependency depth orders work inside a band.
  - T2 is also ordered by the plan's listing order (decision D4, read per language as D4a). These
    `plan-sequence` edges **position an item but never block it**.
- **Every work item gets exactly one placement.** The first rule that applies wins:
  1. withdrawn
  2. landed
  3. tier
  4. inherited tier
  5. prerequisite (pulled ahead)
  6. dependency floor
  7. Wave window
  8. Priority window (drawn as an estimate)
  9. bars (features)
  10. Unplaced
- **Prerequisite pull.** An untiered item that open tiered work waits on, and that its own window would draw
  no earlier than that work, joins the band of the earliest such work, ahead of it. This is how a board link
  like "#190 blocked by #1579" puts the execution-seam story ahead of the T1 work it gates without anyone
  giving it a tier.
- **What a window is.** A wave's window is the span of stages occupied by that wave's open tiered
  items. It is recomputed on every run, and the basis is printed on the page. **Wave places an item and
  Priority orders it.** Priority places an item only when it has no Wave.
- **Board fields are the truth.** Carried data fills a field only where the board has none yet, and
  every such fill raises a `*-from-carried` warning. Until the realignment creates Category, Tier and
  Program, categories and programs come from the carried v2 mapping (`data/taxonomy.json`, approved
  2026-10-16), tiers from the plan, and the page says so above the counts.
- **Status values:**
  - `delivered`: Done, with reason COMPLETED.
  - `delivered-unmerged`: In review, meaning the work is on PR #1168.
  - `in-progress`, `ready`, `backlog`.
  - `withdrawn`: closed as not planned or duplicate. It is never delivered.
  - `unfiled`: a slot with no issue.
  - `out`.
  - `blocked` is derived, never stored.
- **Lanes.**
  - Language lanes come from Category. For tiered items the plan's language wins.
  - The Category view shows 42 lanes grouped by epic.
  - Epics and categories are containers, never nodes.
- **Unfiled plan families render as slots** (`counts.slotNodes`, `counts.unfiledSlots`). Drop them and T2/T3 lie.
- **Bars span open work.** A feature's bar spans its placed, undelivered sub-issues; delivered ones count
  toward its completion, never toward its span.
- **Bugs are placed like any other item.**
  - A delivered parent gets `hasOpenDefect`.
  - Tier milestones count open defects.
  - There is no Rework lane and no notch.
  - The policy is `data/bug-policy.json`.
- **Hardening is a gate program.**
  - Members are the items with Program = Hardening; the links are #1511's blocked-by items, their descendants and #1511.
    The two sets must match. A deferral removes the link and clears Program together.
  - #1511 is a gate milestone drawn at its members' last column.
  - It is complete only when #1511 is closed with reason COMPLETED.
- **Counts come from one object.** "Unplaced" is always `counts.unplaced`.

Full model with field-level sources: `reference/data-model.md`.

## Regenerating

```pwsh
scripts/Get-CohesionRoadmapData.ps1 -OutDir C:\temp\roadmap -Html                       # collect + build + page
scripts/Get-CohesionRoadmapData.ps1 -OutDir C:\temp\roadmap -Stage build -Html          # rebuild from the snapshot, no GitHub traffic
scripts/Get-CohesionRoadmapData.ps1 -OutDir C:\temp\roadmap -MaxAgeHours 2 -Html         # reuse issue pages up to 2 h old (local rebuilds only)
scripts/Get-CohesionRoadmapData.ps1 -OutDir C:\temp\roadmap -Html -Previous C:\temp\roadmap-last\roadmap-data.json   # explain what moved
```

`-RealignStateDir <dir>` (default `%LOCALAPPDATA%\cohesion-board-realign` when it exists) adds the realign
executor's journal timestamps to the Changes tab. The journal never decides whether an operation is done.
The board does. `-Verbose` prints how long each build step took.

**Outputs, all under `-OutDir`:**
- `board-snapshot.json`
- `roadmap-data.json`
- `payload.json`
- `roadmap.html`
- `cache/`

`roadmap.html` is an Artifact **body**: it starts at `<title>`. To view it locally, wrap it in a doctype,
charset and viewport skeleton. To publish it, hand the file to the Artifact tool as it is.

**Read the warnings:**

| Warnings | Meaning |
|---|---|
| `unplaced` | Items with no Tier, Wave, Priority or dependency. The hygiene panel names each one and the board edit that places it. |
| `*-from-carried` | Expected until the field pass. |
| `ready-but-blocked`, `wave-tier-conflict`, `priority-wave-conflict`, `scheduled-after-dependent`, `direct-under-categorised-epic` | Board hygiene and contradictions. Report these. Do not fix them here. |
| `decision-edge-on-board` | A `tiers.json` decision edge now exists as a native link; the carried edge can be deleted. |
| `cycle`, `unresolved-dependency`, `category-option-unmatched`, `epic-without-lane` | A carried input needs an edit, and the chart is suspect until it gets one. |

## Carried inputs: what to edit when the model is wrong

| File | Holds | Edit it when | Never used for |
|---|---|---|---|
| `tiers.json` | **The plan.** Families in listing order, with body slugs; tier per family; plan decisions (D1–D6, D4a, D5a) and the plan's own ordering edges (D5, tier order, owner decisions); conflicts; open calls; Tier option names; the T2 order mode | The plan changes | Placing an item that has a Tier field |
| `programs.json` | **Program definitions.** Name, Program option, shape (`tree` or `gate`), tracking or gate issue, gate label, untracked phases | A program starts or ends | Membership once the Program field is set |
| `taxonomy.json` | **Category definitions.** Rules, boundaries, precedence, retired codes, plus a transition-only per-item mapping of Category, Area and Program (the approved 386 rows plus later additions, each recorded under `postApproval`) | The owner changes the taxonomy, or an item filed before the field pass needs a carried row | An item's category or program once its field is set |
| `overlay.json` | Lanes (keyed by epic WBS and category code), stage names, prose-only program-gate edges, pins, wave-window pins, the extra open calls | A new epic needs a lane, or a decision contradicts the data | — |
| `bug-policy.json` | The one bug policy (B1–B6 plus mechanics) and its pending extensions | The owner changes the policy | — |
| `realign-plan.json` | The approved realignment: decisions 1–20, the four board windows, the board corrections and the execution-seam checks. **The Changes tab only.** | The owner approves a change to the run | Any placement |
| `realign-target.json` | The approved per-operation target, exported by the realign executor. **The Changes tab only.** Absent until the executor's v2 plan exists; the tab then derives the target from the carried data | The executor exports a new approved plan | Any placement |

Never hand-edit `roadmap-data.json` or `roadmap.html`. A pin in `overlay.json` is the last resort, and every
pin is echoed in `overlay.applied[]`.

## Placing a newly discovered item

There is nothing to edit, and no flag to pass. Nothing is cached across runs by default: issue pages and blocked-by
links are read every run, and a body is re-read when its `updatedAt` changed. On the next run the item is placed
from what the board says:

| The filer sets | It lands |
|---|---|
| A parent category, or the Category field | Its category lane, and its language lane |
| Tier, or a tiered parent | Its band. A body line ``Tier 2 family `sql-upsert` `` fills that plan slot. |
| A `## Dependencies` section, a `Blocked by` line, `Related work items: blocked-by #N`, or a native blocked-by link | Its depth inside the band, and `blocked` |
| A blocked-by link from tiered work onto an untiered item | The untiered item is pulled ahead of that work |
| Wave, else Priority, when it has no tier | That value's window |
| Program, plus a link from the gate | Program and gate membership |
| The `bug` label or the Bug type, under a delivered item | Placed like any item. The parent gets `hasOpenDefect`. |

Only structural changes need a hand edit:
- **A new epic:** one lane entry in `overlay.json`.
- **A new program:** one entry in `programs.json`.
- **A plan change:** `tiers.json`.

Two rules complete it:
1. **Every move is explained.** Windows recalibrate and bands resize when the board changes, so positions
   can move left as well as right. Run with `-Previous` and read `changes.moved[]`. Each move carries
   reason codes.
2. **Every node carries `placement`, `stageSource` and `stageExplanation`.** An unauditable chart gets
   overridden by hand.

## Answering questions from the dataset

Answer in words from `roadmap-data.json`. Read every number from the file; never retype one.

- *What is next?* `nextUp`. It applies the workflow's own rule: unblocked items first, then Priority, then Wave.
- *What blocks the most?*
  - Read `criticalPath.blockers` and quote `criticalPath.edgeSets` with it.
  - Check each top entry's `status`: a `delivered-unmerged` blocker (#1068 and #1101 on 2026-10-16) is a merge,
    not a task.
- *When is T1 done?* Read:
  - `milestones.t1Complete`: its `chains` and `sinks` (the longest chains through the gate and T1 bands) and
    `remaining`;
  - `criticalPath.chains`: every maximal chain through all the bands, with its sink (read the sinks from the
    data; do not quote them from here).
- *Is the board consistent?* `contradictions` and `hygiene`.
- *Has the realignment run?* `realign.headline` and `realign.summary`. Both are computed from the board.
- *Why is X here, and why did it move?* The node's `placement` and `stageExplanation`, and `changes.moved`.
- *What is not decided?* `openCalls`.

## Guardrails

- **Scope.** Only `assimalign/cohesion`, Project #13, and root #1067. The script hard-codes these three
  and nothing else, and throws if #1067's title stops parsing as `[L03.02.00]`.
- **Read-only.** If a task needs a board change, stop and hand it to `cohesion-work-items` with the
  owner's instruction.
- **Quota.** A full collection costs about 40 points:
  - 15 issue pages at 2 points each;
  - 8 body batches (fewer when bodies are unchanged);
  - 1 blocked-by batch;
  - 1 project field-options read.

  Never query per item. Details are in `reference/queries.md`.
- **Fail loudly.** The script validates before it writes and leaves the previous outputs untouched on
  failure.
- **One contract.** `assets/roadmap-template.html` and `New-RoadmapPayload` are one contract. Change
  both together, and record the hashes in `reference/data-model.md`.
