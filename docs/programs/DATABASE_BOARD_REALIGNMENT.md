# Data Platform Board Realignment (v2)

**Status:** approved; W1A runs right before this record's tooling PR merges, W1B–W4 after it · **Decided:**
2026-10-16 · **Owner:** Chase Crawford ·
**Scope:** GitHub Project #13, the `[L03.02.00] Cohesion - Data Platform` tree (#1067: 391 work items under 8
epics and the Hardening gate), and the two skills that file and draw it.

> **Why this file exists.** The Data Platform board grew flat (#31 reached 79 direct children, #4 61), defects
> were filed against epics, and tier and program membership lived only in issue prose. On 2026-10-16 the owner
> approved all 20 decisions of the realignment's second version: a category layer, three new board fields, a
> filing guard, a bug policy and a roadmap regenerated from the live board. This is the **decision record**:
> what was decided, the rules a session applies when filing, how the run executes and where it may stop. The
> proposal itself is not reproduced. When the run completes and the decay audit stays clean, fold the durable
> rules into the two skills and delete this file.

**Canonical data.** The category definitions and the item → category, area and program mapping live in the
roadmap skill's [`taxonomy.json`](../../.claude/skills/cohesion-roadmap/data/taxonomy.json); the program
gate in [`programs.json`](../../.claude/skills/cohesion-roadmap/data/programs.json); the plan tiers and
families in [`tiers.json`](../../.claude/skills/cohesion-roadmap/data/tiers.json); the bug policy in
[`bug-policy.json`](../../.claude/skills/cohesion-roadmap/data/bug-policy.json); the decisions, corrections and
windows, with the checks the roadmap evaluates against the board, in
[`realign-plan.json`](../../.claude/skills/cohesion-roadmap/data/realign-plan.json). **Numbers here are read
from those files; when they disagree, the files win.**

| Skill | Owns |
| --- | --- |
| [`cohesion-work-items`](../../.claude/skills/cohesion-work-items/SKILL.md) | Filing, placing and linking items: `New-CohesionWorkItem.ps1` (the guards below, `-Audit`), the field and WBS reference and the manual recipes in `reference/project-schema.md` |
| [`cohesion-roadmap`](../../.claude/skills/cohesion-roadmap/SKILL.md) | The Data Platform roadmap page, regenerated read-only from the live board; its Changes tab reports which windows and corrections the board shows as done |

---

## 1. The 20 decisions

All approved by the owner on 2026-10-16, in full. Decisions 1–10 gate the run, 11–15 are board corrections,
16–20 are defaults.

| # | Decision | Where it lives |
| --- | --- | --- |
| 1 | **Hardening (D1 v2).** #1511 stays open as the **program gate**: it keeps its acceptance criteria verbatim, takes no sub-issues, and is blocked by its 33 members. Its 8 features move to the categories of the code they change. Program = Hardening goes on 62 items (#1511, its 33 blockers and their 28 tasks); field and links are checked against each other on every roadmap run. | §7; `programs.json` gate |
| 2 | **Withdraw R5.** No scaffold child leaves the story that defines it (21 of 25 name that parent in their body). Cost: tree and field disagree on 86 items, and 6 categories look empty when browsed; the field stays exact. | §3 |
| 3 | **The 42-category set**, with rules R6 and R7 and the Core membership rule. | §3; `taxonomy.json` |
| 4 | **6 new cross-epic moves:** #1012, #1231, #1420 → Core C04; #1342 → C03; #1371 → C01; #1446 → Tooling C02. | `taxonomy.json` mapping |
| 5 | **Execution order (D7 v2): the tree first**, each category filled as it is created (#31 peaks at 80 of 100), then the gate, the cross-epic moves and the fields. 4 windows at 400 requests per hour. | §8 |
| 6 | **Tooling (D8 v2).** `-Category` is mandatory in Data Platform once the epic has categories, and an untiered filing needs `-Wave` or `-Priority`; `-DirectUnderEpic`, not `-Force`, is the only override of the epic guard; #1511 takes no child; a T2/T3 filing names its plan family. The tooling merges after Stage 0 and before Stage 1. Five of its edits reach every area (§5). | §5; the work-items skill |
| 7 | **One body edit:** a member note appended to #1511 after its features move; the existing text is untouched, and the append aborts if the body's hash changed. | §8 (W2) |
| 8 | **The Program rule:** an item in two programs carries the one the tree cannot show (#1373 becomes Hardening). No "Query Language T1" option: Tier carries T1. | `taxonomy.json` |
| 9 | **The WBS rule:** "the prefix names the epic an item was filed in; membership is the tree plus Category and Area". | §2 |
| 10 | **The bug policy:** B1–B6 plus the not-planned/duplicate guard now; `bug-policy.md`'s broad B6 and its B7 are decided at the backfill after PR #1168 merges. | §6 |
| 11 | **#190 first:** #190 to P001; #1126, #1127, #1128 and #1131 to Backlog; native link "#1097 blocked by #190". #190 itself stays Backlog, because the execution seam (#1579) now blocks it. | applied 2026-10-16 |
| 12 | **Ready means unblocked:** #1107, #1109, #1110, #1113, #1138 and #1141 to Backlog. | applied 2026-10-16 |
| 13 | **Hardening's tracked items:** #1280, #1276 and #1274 to P001; #1276 to W01; #1012 to W04. | applied 2026-10-16 |
| 14 | **Priority and Wave on the Unplaced items** (#1444–#1447, #1470, #1504, #1508, #1509). The values are the owner's; until set they stay Unplaced. | open (optional window D14) |
| 15 | **#1250 stays open** until its ratio timing guard lands. | holds |
| 16 | **The roadmap model v2:** untiered work in its Wave window, else its Priority window; T2 in plan order per language; every move explained; nothing cached across runs. | the roadmap skill; `tiers.json` D4a |
| 17 | #1540, #1522 and #1541 stay unedited and are flagged as conflicts. | the roadmap's contradictions panel |
| 18 | No HARDENING.md acceptance addenda on the 25 tracked items for now; no Effort field. | — |
| 19 | SQL C08 requires no Tier; the four language-surface categories do (C07, SQL C01, Documents C01, Graph C01). | §5 |
| 20 | Stage 0 verifies an option ceiling of at least 42 before it creates Category: the 42-option create is itself the check. | §8 (W1A) |

## 2. The WBS rule (decision 9)

> **The prefix names the epic an item was filed in; membership is the tree plus `Category` and `Area`.**

A code is minted once, at filing, and never rewritten: no title changes in this run, #1511 included. After the
run 70 items carry a prefix from another epic than their Category and 55 from another epic than their tree
parent. Grep by code finds filing history; filter by field finds membership. The consequence for tooling: **a
parent is read from the sub-issue tree, never derived by trimming a code** (the trimmed code named the epic by
construction, which is how the epics went flat). Category codes are letter-coded, `L03.02.NN.Cxx`; their
children are `L03.02.NN.Cxx.01` onward, and serials have any width.

## 3. The taxonomy

**8 epics, 42 categories, 391 items** (386 at approval plus the five execution-seam items #1576–#1580, all in
Core C07). A category exists twice: as a Feature issue `[L03.02.NN.Cxx] <Epic> - <Category>` under its epic
(what a person browses and files under), and as a value of the `Category` field on every item (what the
roadmap and every view read). **The field is the truth; the tree is the browsing affordance.**

| Epic | Issue | Categories | Items |
| --- | --- | ---: | ---: |
| L03.02.01 Core Engine | #31 | 17 | 163 |
| L03.02.02 SQL | #4 | 7 | 104 |
| L03.02.03 DocumentDB | #5 | 5 | 38 |
| L03.02.04 KV, Blob, Cache | #57 | 3 | 24 |
| L03.02.05 GraphDB | #50 | 5 | 47 |
| L03.02.06 Developer Tooling | #856 | 2 | 7 |
| L03.02.07 Object Mapping | #1006 | 1 | 5 |
| L03.02.09 Program and Roadmap (new epic) | created in W2 | 2 | 3 |
| L03.02.08 Hardening | #1511 | — | the program gate, no children after W2 |

The tree moves: 153 re-parents inside an epic and 27 across epics; 208 items change by field only; 3 closed
without delivery (#176, #906, #1232) are not moved. #31 ends with 18 direct children, #4 with 7.

**Filing rules** (`taxonomy.json` `rules`):

- **R1 parent of last resort.** An item moves to its category only if its parent is an epic, the root or the
  retired Hardening epic. An item with a meaningful parent (a program tracker, a T1 feature, a scaffold story,
  the item its bug regresses) keeps it and carries the Category field.
- **R2 file by the code that changes**, not the surface that breaks: the merged diff for merged work, the
  stated fix sites for open work, the title's subject otherwise. Tie-breaks: the published precedence order
  (kernel categories first), then the kernel epic for a multi-model item, then the subject the title names first.
- **Core membership.** An item files in Core when its change lands mainly in kernel code, changes two or more
  models with no dominant one, changes kernel and model code about equally, delivers a Core concern with no
  product behaviour change (R6), or has no model home by a stated boundary (replication).
- **R4** a category is never closed. **R6** the observability, API-shape and testing categories win only when
  the concern itself is the deliverable. **R7** a container takes the plurality category of its children.
- **Growth:** the Category field holds at most 45 options by the realignment's own cap (3 free). A new concern
  inside a model is an existing category; a new category needs a code that owns it and the owner's sign-off.

**Language-surface categories** (an open item there carries a Tier): `L03.02.01.C07`, `L03.02.02.C01`,
`L03.02.03.C01`, `L03.02.05.C01`.

## 4. Board fields

| Field | Options | Set on |
| --- | --- | --- |
| **Category** (new) | 42, named as the category issue's title minus its code | 391 items (W3) |
| **Tier** (new) | T1, T2, T3, Fix-first, Prerequisite, Done-pre-plan, Out-of-scope | 71 items (W2): the plan's 66 plus #1576–#1580 (Prerequisite) |
| **Program** (new) | Hardening, Engine Extensibility, Concrete-First API, EventSource Tracing, Storage Journal, Object Mapping | 110 items (W2): 62, 14, 16, 9, 4, 5 |
| **Area** (existing) | + Database, Database.Sql, Database.Documents, Database.Graph, Database.KeyValue, Database.Blob, Database.Cache | 391 items (W4): kernel, Tooling, Mapping and Program → Database; model categories → their model |
| **Kind** (existing) | + Bug | defects |
| **Origin** (existing) | + Defect | defects |

Until a field exists and is filled, the roadmap reads the carried mapping and says so on the page.

## 5. Filing after the realignment (decision 6)

`New-CohesionWorkItem.ps1` implements it; the [work-items skill](../../.claude/skills/cohesion-work-items/SKILL.md)
is the guide. In Data Platform:

- A work item goes **under a category**. An epic with categories refuses a direct child unless
  `-DirectUnderEpic`, the guard's only override (`-Force` only skips the duplicate check). The refusal lists
  every category.
- **`-Category` is mandatory** once the epic has categories, unless the parent is a category. It comes from
  the epic the item sits under and names the category there of the code that changes; the parent is not a
  reliable answer (it disagrees for about half of the measured children), so the script suggests it but never
  inherits it. A Category from another epic draws a warning (file the item under that epic instead).
- **Untiered work needs `-Wave` or `-Priority`**, bugs included, or the roadmap leaves it Unplaced.
- **Language-surface work carries a Tier** (bugs exempt); **T2 and T3 work names its plan family**
  (`-Family <slug>`, or `-Family none` for out-of-plan work), and a language family must match its category's
  language (`oql-*` under SQL C01 is refused; cross-cutting families fit anywhere).
- Category, Tier, Program and Area are written at create; Tier and Program are inherited from the nearest
  ancestor that has one, Area is copied from the category.
- **#1511 takes no child** (read from `programs.json`); §7 gives the three steps instead.

**In every area** (decision 6 names these five as repo-wide): a WBS that names more than one issue stops the
run; a parent at 100 sub-issues is refused before anything is created; new scope under a closed parent is
refused; once the issue exists, any failed step (project add, a field write, the read-back, the link) still
records it in the branch manifest as `UNLINKED` and/or `INCOMPLETE:<step>`, prints its number and exits
non-zero, and re-running the same filing resumes it; and bulk filing is paced at 35 filings an hour, none during
a realignment window. `-Audit` reports decay through the paths no guard sees: open work directly under a
categorised epic, missing Category or Area, untiered language-surface work, parents near the cap, duplicated
codes and unfinished manifest rows.

**Two choices made after `tooling.md` (the main session's calls; the owner can override either):**

- **A Story's child is typed Task, not Story.** `tooling.md`'s E4 table typed every child of a Feature, Story,
  Task or Bug as a Story. The board says otherwise: all 72 Story → child links on it are typed Task (#1577–#1580
  among them), the realignment executor types its depth-6 items Task, and the owner's hierarchy of 2026-10-11
  (Initiative → Epic → Feature → Story) puts Data Platform's sixth-segment items as Tasks under a Story. The
  script now types a Feature's child a Story and a Story's, Task's or Bug's child a Task.
- **`-Origin Defect` is a bug filing.** Either it or `-As bug` takes the bug path with all its guards, and a
  bug's Origin is always Defect, so an `-Origin Defect` task cannot bypass the open, not-planned or duplicate
  parent refusals.

## 6. The bug policy (decision 10)

| # | Rule |
| --- | --- |
| **B1** | A defect in behaviour a closed item delivered is a native sub-issue of **that closed item**: not of its epic, not of its category. |
| **B2** | Closed means GitHub state `CLOSED` with reason `COMPLETED`. Board Status *In review* is open: a defect found there is in-scope rework on the open item. **Guard:** a parent closed as *not planned* or *duplicate* delivered nothing, so nothing regressed against it: file under the owning category. |
| **B3** | Attach to the most specific delivered item whose acceptance criteria are violated: the leaf, never the feature above it, an epic or a category. When the leaf has no room (eight nesting levels, 100 sub-issues), the nearest ancestor that has room, naming the leaf. |
| **B4** | If no delivered item's criteria are violated, it is an ordinary item under the owning category. |
| **B5** | Never reopen a delivered item to carry a defect. Its progress bar stays incomplete, by design. |
| **B6** | A bug child of a bug is legitimate: a regression in a fix attaches to the item that shipped the fix. |

**Pending, decided at the post-#1168 backfill against real cases** (open calls C2 and C3 on the roadmap): X1,
`bug-policy.md`'s broad B6 (an identifiable breaking change beats B3), and X2, its B7 (with two owners, the code
owner takes the defect). Until then they are judgement, not rules. Mechanics: issue type Bug, Kind Bug, Origin
Defect, label `bug`, the stamp `> Defect in [<wbs>] (#N), closed <date>.`, and the next free child code. One
defect qualifies today (#1099 under #912); the backfill moves it after #1168 merges.

## 7. The Hardening program gate

#1511 holds the program's acceptance criteria (SF10, at most 2× the best engine, load at most 1.5×, no
native → emulated regression) and is **complete only when #1511 is closed as completed**, never because its
members closed. After W2 it has no sub-issues and 33 blockers, Kind Feature, Program Hardening, and an appended
member note. To put work in the program: file it by the code it changes, pass `-Program Hardening`, then
`gh api -X POST repos/assimalign/cohesion/issues/1511/dependencies/blocked_by -F issue_id=<REST id>`. A task
under an existing Hardening feature needs nothing extra. A gate failure after a member closed is a bug under
that member, plus the same link.

## 8. Execution

The run is about **1,566 content requests in 4 one-hour windows** at 400 requests per rolling hour, at least
9 s apart (GitHub's content limit is about 500 per hour, account-wide and including web UI clicks; whether
project-field and sub-issue mutations count is undocumented, so every mutation is counted). The executor
(regenerated from the approved data and dry-run end to end on 2026-10-16, with a full rollback rehearsal) keeps
its journal, limiter state and the rollback pre-images in `%LOCALAPPDATA%\cohesion-board-realign-v2`, never in
a session scratchpad. An independent review of the executor was running when this record was written.

| Window | Content | Requests | Waits for | State at the end |
| --- | --- | ---: | --- | --- |
| **W1A** | Stage 0: create Category (the 42-option ceiling check), Tier and Program; the option-id probe (Program's sixth option, added with the other five resent by id); add the Area, Kind and Origin options | 7 | the final rollback pre-image (≤ 6 h old); runs right before the tooling PR merges | 3 new fields; tree untouched. **Safe for any pause** |
| **W1B** | Create and fill the Core (17), SQL (7), Documents (5) and KV/Blob/Cache (3) categories, one at a time | 388 | **`tooling-merged`**: this record's tooling PR on `main` (`New-CohesionWorkItem.ps1` contains `DirectUnderEpic`) | 4 epics categorised; #31 at 21 (peak 80), #4 at 22; nothing has left #1511. Safe for hours |
| **W2** | Graph, Tooling, Mapping and the new L03.02.09 epic; the Hardening gate (Program, Kind, 33 links, read back); the 27 cross-epic moves; the #1511 append; Tier 71, Program, issue type | 389 | **`skill-gate-entry`**: `programs.json` with the #1511 gate on `main` | **Tree final**, gate live. **The safe point for a pause of days** |
| **W3** | Category on all 391 items | 391 | W2 read back | Category roll-ups complete |
| **W4** | Area on all 391 items | 391 | W3 read back | Field pass complete; the roadmap reads no carried category or program |
| D14 | Optional: Priority and Wave on the 8 Unplaced items (decision 14), from the owner's values | 16 | W4 | — |

**The executor stops itself** at each checkpoint above and on: a missing, tampered or stale pre-image
(`preimage`); a plan that no longer matches the board (`drift`, every target re-read at window start; a moved
target is skipped only with `--accept-drift`, never forced); a second executor on the state directory
(`locked`); a window run out of order (`order`); a probe or option-list rewrite that loses an option id
(`probe-failed`, `options-lost`); a parent about to pass 100 sub-issues; a feature leaving #1511 before the
gate's links and Program values read back; and a changed #1511 body before the append. A crash is resumed by
re-running the same command: an intent with no recorded outcome is decided by reading the board, never by
resending.

**Filing between windows.** Each window reads the board live at its start and accepts a Data Platform item filed
during the pause when its Category belongs to the epic it sits under (it then gets Area, and from W2 an item
filed flat during the W1A → W1B pause is moved into its category). It halts, sending nothing, on an item whose
Category comes from another epic (`late-epic-mismatch`), on one with no Category (`unmapped`, until it has an
entry in the executor's `mapping-additions.json`), and from W2 on a new item created directly under an epic
after that epic's first category, unless the owner marks it to stay (`late-direct-under-epic`, `stayDirect`).
So: **between windows, file only under an epic that already has categories, with a `-Category` from that epic;
during a window, file nothing.**

**Rollback** runs windows in reverse (W4 → W1A) through the same limiter: moves return to the pre-image parent,
fields to their pre-image value, the 33 links are deleted, #1511's body is restored only while it still hashes
to the appended version, issue types are unset, and created issues are **closed as not planned, never deleted**.
A created field stays, empty.

**During a window the owner does not:** run any other bulk activity on the account; file, move, close or
retitle anything under #1067; edit #1511's body before the W2 append; edit the Area, Kind, Origin, Category,
Tier or Program field definitions in the UI.

## 9. Ledger

Filled in as the run goes: one row per step, with when it ran and the evidence (journal line, merge commit,
read-back).

| Step | Ran | Evidence |
| --- | --- | --- |
| Decisions 11–13 board corrections; the execution seam #1576–#1580 filed and linked | 2026-10-16 | read back by the roadmap's Changes tab |
| W1A (Stage 0) | | |
| Realignment-tooling PR merged (`tooling-merged`) | | |
| W1B | | |
| `programs.json` gate entry on `main` (`skill-gate-entry`) | | |
| W2 | | |
| W3 | | |
| W4 | | |
| Decision 14 values (optional window D14) | | |
| Bug-policy X1 / X2 decided (post-#1168 backfill) | | |
