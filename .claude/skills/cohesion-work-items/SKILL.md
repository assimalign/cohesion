---
name: cohesion-work-items
description: Create and link GitHub work items (features / stories / bugs, plus the read-only -Audit decay report) in the assimalign/cohesion Project #13 using the gh CLI — placed from the native sub-issue tree, numbered with the L01.01.NN WBS scheme, typed at create, and given their board fields (Category, Tier, Program, Area, Wave, Priority) and the Summary / Acceptance Criteria body template. Use whenever new development is requested, ESPECIALLY when scope creep is discovered mid-branch (file the out-of-scope work as its own tracked item so one PR can attach and close several), and when a defect is found in behaviour a closed item already delivered. Triggers include "create a work item", "file an issue for this", "capture this scope creep", "track this extra change", "open a Cohesion issue", "this is out of scope — log it", "file a bug against the item that shipped this", "audit the board", or "assemble the Closes list for my PR". Use only in the assimalign/cohesion repo.
---

# Cohesion Work Items

Capture development as tracked GitHub work items in **Project #13 "Cohesion"** under the `assimalign` org,
using the `gh` CLI. The defining use case is **scope-creep capture**: while implementing an item you do
extra work that falls outside it; this skill turns that work into its own properly placed issue, so the
eventual PR closes *every* item it resolved, not just the one you started with.

Full schema, field options, the bug mechanics and the manual recipes live in
[reference/project-schema.md](reference/project-schema.md). The reliable end-to-end path is the script
[scripts/New-CohesionWorkItem.ps1](scripts/New-CohesionWorkItem.ps1). The Data Platform realignment that
added categories, the Category / Tier / Program fields and the bug policy is recorded in
[docs/programs/DATABASE_BOARD_REALIGNMENT.md](../../../docs/programs/DATABASE_BOARD_REALIGNMENT.md).

## The model (read this first)

The **tree is held by native GitHub parent/sub-issue links**; every item is added to Project #13. The GitHub
**issue type** names the level: **Initiative → Epic → Feature → Story** (owner, 2026-10-11), a **Task** below a
Story, and **Bug** for a defect. The script chooses the type from the parent's kind and sets it in the create
call.

| Parent | New item's type / `Kind` |
| --- | --- |
| Area epic (`[L01.01.11] Foundation - Http`) or Data Platform category (`[L03.02.02.C04] SQL - Indexing`) | Feature / Feature |
| Feature | Story / Task |
| Story, Task or Bug | Task / Task |
| any closed delivered item, with `-As bug` or `-Origin Defect` | Bug / Bug |
| Program root (`[L01.01.00]`, `[L03.02.00]`) or a program gate (#1511) | refused |

Titles carry a WBS code: `[<wbs>] <description>`. **The prefix names the epic an item was filed in;
membership is the tree plus `Category` and `Area`.** A code is minted once, at filing, and never rewritten,
so after an item moves its code no longer names its parent: grep by code finds filing history, filter by
field finds membership. A child's code is its parent's code plus the next free serial (`.01`, `.02`, …
`.100`, any width). A category's code ends in a letter code (`L03.02.02.C04`); its children are
`L03.02.02.C04.01` and so on. **The script never mints a category.**

Your branch names the item in flight: `feature/L01.01.11.14-extended-connect` → the issue titled
`[L01.01.11.14]`. Category branches (`feature/L03.02.02.C04.07-...`) work the same way.

## Step 1 — Classify the work before creating anything

When a piece of work surfaces, decide where it belongs. **This classification is the whole point**: it is
what makes the tree, the scope-creep accounting and the multi-item PR meaningful. The script records it as
the item's **Origin**.

- **In scope of the branch's item, but separable** → a **child** of that item (`-As task`, the default).
  → Origin **DiscoveredTask**.
- **Out of the branch's item's scope, same container** → a **sibling**: a child of the branch item's
  **actual parent in the tree** (`-As feature`). The script reads that parent from the sub-issue link; it
  never derives it by trimming the WBS, which named the epic by construction and flattened the epics.
  → Origin **DiscoveredFeature**, a signal the original item was under-scoped.
- **Different area, or planned work** → pass `-Parent <issue#|WBS>`. The type then follows the parent's
  kind (`-As task|feature` has no effect). Origin **Planned**; add `-Origin DiscoveredTask|DiscoveredFeature`
  if it really is creep.
- **A defect in behaviour a *closed* item delivered** → a **bug child of that item**
  (`-As bug -Parent <issue number>`), Origin **Defect**. See *Bugs* below. `-Origin Defect` means the same
  thing: either one makes the filing a bug, with every bug guard, and a bug's Origin is always Defect.
- **Same intent as an existing item** → do **not** create a duplicate; reuse it.

If the classification is ambiguous, state your reasoning and ask the user which bucket it is before creating.

### Data Platform (`[L03.02.00]`, #1067): four more rules

The realignment of 2026-10-16 (decision 6) adds rules for any filing under #1067. They exist because the
Data Platform roadmap reads the board fields, and because filing flat under an epic is how the previous
category layer decayed.

1. **File under the category, not under the epic.** An epic with categories (`[L03.02.NN.Cxx]` children)
   refuses a work item directly under it; the refusal lists every category with its open-children count.
   Pass `-Parent <category#>`. `-DirectUnderEpic` is the only override, for work that belongs to the epic
   itself; `-Force` does **not** override it (it only skips the duplicate check).
2. **`-Category` is mandatory once the epic has categories,** unless the parent is itself a category.
   The Category must come from **the epic the item sits under**: name the category there that owns **the
   code the work changes** (taxonomy rule R2), which is not necessarily the parent's (the parent is the wrong
   answer about half the time). If that code belongs to another epic, file the item under that epic's
   category instead. The refusal suggests the nearest ancestor's category; a Category from another epic
   draws a warning, and the realignment executor halts on one.
3. **Untiered work needs `-Wave` or `-Priority`.** The roadmap places work by its tier, else its Wave
   window, else its Priority window; with none of them it lands in *Unplaced*. Bugs included. A tiered
   item (from `-Tier` or a tiered ancestor) may omit both.
4. **Language-surface work carries a Tier.** In `L03.02.01.C07`, `L03.02.02.C01`, `L03.02.03.C01` and
   `L03.02.05.C01`, an item needs `-Tier` (or a tiered ancestor). Bugs are exempt. A **T2 or T3** item also
   names its plan family, `-Family <slug>` (the slugs are in the roadmap skill's `tiers.json`), which writes
   the body line ``Tier N family `slug` `` that puts it in its plan slot; `-Family none` files out-of-plan
   work and the roadmap flags it.

Category, Tier, Program and Area are written at create; Tier and Program are inherited from the nearest
ancestor that has one, Area is copied from the category. **Program gates take no children:** #1511 holds the
Hardening acceptance criteria and is *blocked by* its members. To put work in that program, file it by the
code it changes, pass `-Program Hardening`, and link the gate:
`gh api -X POST repos/assimalign/cohesion/issues/1511/dependencies/blocked_by -F issue_id=<REST id>`.
A task under an existing Hardening feature needs none of that: it inherits Program.

### Bugs (bug policy B1–B6, realignment decision 10)

- **B1** A defect in behaviour a closed item delivered is a native sub-issue **of that closed item**: not of
  its epic, not of its category.
- **B2** Closed means GitHub state `CLOSED` with reason `COMPLETED`. Board Status *In review* is still open:
  a defect found there is in-scope rework on the open item, so file a task under it (the script refuses an
  open parent unless `-AllowOpenParent`). **A parent closed as *not planned* or *duplicate* delivered nothing**,
  so nothing regressed against it: file an ordinary item under the owning category.
- **B3** Attach to the **most specific** delivered item whose acceptance criteria the behaviour violates —
  the leaf, never the feature above it, never an epic or a category. When the leaf has no room (eight
  nesting levels or 100 sub-issues), attach to the nearest ancestor that has room and name the leaf.
- **B4** If no delivered item's criteria are violated, it is not a bug child: file an ordinary item under
  the owning category. Shipping it adds criteria; it does not restore them.
- **B5** **Never reopen a delivered item to carry a defect:** the board's *Item reopened → In progress*
  workflow takes it off the delivered record. Its progress bar stays incomplete instead, by design.
- **B6** A bug child of a bug is legitimate: a regression in a fix attaches to the item that shipped the fix.

Two broader rules from `bug-policy.md` (a breaking change beats B3; with two owners the code owner takes the
defect) are **not** policy yet: the owner decides them at the backfill after PR #1168 merges. Use judgement
and name the other item `Relates to #N`. `-Parent` must be an issue **number** for a bug: WBS codes repeat.

### Search before creating

```pwsh
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -Search "websocket bootstrap http3"
```

If a strong match exists, add the work to it instead. The create path also runs the check and **blocks on
a likely duplicate** (open item, high title overlap); pass `-Force` only after confirming the new item is
genuinely distinct. For a bug the parent is excluded and its other children rank first.

## Step 2 — Create the work item

Run the helper from the repo root. **Run it with `-DryRun` first**: it resolves and validates everything,
prints each field with its source, and stops.

```pwsh
# Scope-creep task under the branch's item:
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 `
  -Title "Validate CONNECT authority form against RFC 9110 origin rules" `
  -Summary "Hardening discovered while wiring extended CONNECT; outside the original feature scope." `
  -Acceptance "Reject malformed authority components.","Add targeted unit tests.","NativeAOT-safe." `
  -Status "In progress"

# Out-of-scope sibling, under the branch item's actual parent:
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -As feature `
  -Title "Implement HTTP/1.1 informational (1xx) response support" -Wave W02 -Priority P003

# Data Platform: under a category, untiered, so Wave or Priority is required:
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -Parent L03.02.02.C04 `
  -Title "Rebuild an index online" -Priority P003

# Data Platform: T2 language work names its plan family:
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -Parent L03.02.02.C01 `
  -Title "Accept MERGE statements" -Tier T2 -Family sql-upsert

# Defect in behaviour a CLOSED item delivered (-Parent is a NUMBER):
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -As bug -Parent 912 `
  -Title "TIMESTAMP index keys disagree with SQL equality" -Priority P002 -Category L03.02.02.C04
```

Key options: `-Search "<keywords>"`, `-As task|feature|bug`, `-Parent <issue#|WBS>`, `-Category <code|issue#>`,
`-Tier`, `-Program`, `-Family <slug|none>`, `-DirectUnderEpic`, `-AllowOpenParent` (bug on an open item),
`-AllowClosedParent` (new scope under a closed item), `-Origin`, `-Title`, `-Summary`, `-Acceptance a,b,c`,
`-Standards a,b`, `-BodyFile <path>`, `-Status` (default `Backlog`), `-Priority`, `-Wave`, `-Label`,
`-Wbs <code>` (a pre-assigned serial; never a category), `-Force` (duplicate check only), `-DryRun`.

What the script refuses, and the only way past each refusal:

| Refusal | Why | Override |
| --- | --- | --- |
| A WBS (branch, `-Parent`) that names 0 or 2+ issues | codes repeat; the first match is a guess | `-Parent <number>` |
| A program root or a program gate as parent | roots are made by hand; a gate takes no children | none |
| A ninth nesting level; a parent at 100 sub-issues | GitHub refuses both | file under a child / the nearest ancestor with room |
| A work item directly under an epic with categories | the decay path | `-DirectUnderEpic` (with `-Category`) |
| New scope under a closed parent | it vanishes from every open view | `-As bug`, or `-AllowClosedParent` |
| A bug under an open, not-planned, duplicate, epic or category parent | bug policy B2–B4 | `-AllowOpenParent` (open only) |
| A Data Platform filing missing Category, Tier, Wave/Priority or Family where required | the roadmap cannot place it | pass the value |
| A `-Family` of another language than the category (`oql-*` under SQL C01) | it would take the wrong plan slot | the category's own language (cross-cutting `x-*` families fit anywhere) |
| An option that does not exist on the board (preflight) | a skipped field is how items landed unclassified | create the option, or wait for realignment Stage 0 |

**Once the issue exists, every outcome is recorded.** After the create it adds the item to the project, sets
the fields, reads them back, re-checks the parent's room and links it. If any of those steps fails, or the
create itself errors after making the issue, the script still writes the manifest row — `|UNLINKED` unless the
link succeeded, `|INCOMPLETE:<step>` when a step failed or a field read back wrong — prints the issue number and
exits 1. **Re-running the same command resumes it:** the row (same branch, parent and title) makes the script
reuse that issue instead of creating another, finish the remaining steps and replace the row.

Before the realignment's Stage 0 creates `Category`, `Tier` and `Program` (and adds `Kind` Bug, `Origin`
Defect and the `Database*` Area options), a filing that does not need them behaves as it always did; one that
does fails at preflight, naming the missing field or option. Nothing is created.

If PowerShell isn't available, follow the **manual recipe** in
[reference/project-schema.md](reference/project-schema.md): same steps, raw `gh`.

## Step 3 — Body content

The repo's de-facto issue body is `## Summary` → `## Acceptance Criteria` → `### Standards and Compliance`;
a bug adds `## Observed`, `## Expected` and `### Regression scope`, and is stamped
`> Defect in [<wbs>] (#N), closed <date>.`; discovered work is stamped
`> Discovered while implementing [<wbs>] (#N).`. The script generates the skeleton; fill it with real,
testable criteria. For wire-protocol work cite the RFC or spec in *Standards and Compliance*; for
runtime-contract work, note that no conformance suite applies (mirrors #703/#713).

## Step 4 — Close every item from the PR

GitHub auto-closes an issue only when the PR body has a **closing keyword + that issue's number**, and
`Closes #1, #2` on one line links only the first. Use **one keyword per line**. The script records every item
it created on this branch in a per-worktree manifest under `.git/cohesion/`; emit the block from the **same
worktree**:

```pwsh
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -EmitClosesBlock
```

```
## Work items resolved by this PR
Closes #339

### Discovered (out-of-scope) work
Closes #714

### Defects in delivered work
Closes #1251

<!-- creep: 1 of 2 scope items were discovered out-of-scope; 1 defect in delivered work, excluded from the creep tally -->
```

Defects are listed but kept out of the creep tally: rework on shipped work is not scope the branch grew. An
item whose link failed is printed as an `UNLINKED` warning above the block. Closing a parent does not close
its sub-issues or vice versa, so list each item the PR resolves.

## Audit: is the board decaying?

```pwsh
.claude/skills/cohesion-work-items/scripts/New-CohesionWorkItem.ps1 -Audit            # default -Root 1067
```

Read-only. Lists open work items directly under an epic that has categories, items under such epics missing
Category or Area, open language-surface items without a Tier, parents at 95+ sub-issues, WBS codes carried by
more than one issue, and `UNLINKED` and `INCOMPLETE` rows in this worktree's manifest. **Exit 1 when it finds
anything.** It catches what no filing guard sees: REST bulk filing, the manual recipe, the web UI and
`-DirectUnderEpic`.

## Guardrails

- **Only operate on `assimalign/cohesion`** and Project #13. Confirm `gh auth status` has the `project` scope.
- **Never invent a WBS code**: the script derives the next free child across open and closed siblings. A
  category code comes from a signed-off design and is created by the realignment executor or the manual
  category recipe, never by the script.
- **Pace bulk filing: at most 35 filings per hour.** A filing is 8–12 content-generating requests and
  GitHub's account-wide content limit is about 500 per hour, web UI clicks included. GraphQL points matter
  less: a `-DryRun` costs **2 points** (3 when the parent is inferred as a sibling, +1 for each `-Category`
  read by number or code), each of the script's own queries measured at 1 point, and the issue list is read
  over REST. A filing costs about **15–20 points**: the dry-run reads, the read-back, the node-id read and the
  link, plus gh's own requests inside `issue create` and `project item-add` and one per field write, each
  counted at GitHub's minimum of 1 point (those were not measured live: they write).
- **While the board realignment runs:** between windows, file only under an epic that already has
  categories, with a `-Category` from that epic; during a window, file nothing. A window spends 400 of the
  500 hourly requests, and the executor halts the next window on a Data Platform item whose Category comes
  from another epic, on one with no Category, and on a new item directly under a categorised epic unless the
  owner marks it to stay.
- **One concern per item.** If the creep is really several things, create several items.
- **Don't create duplicates**: search first (Step 1).
- **Confirm before creating** when the parent or classification is uncertain; `-DryRun` previews everything.
- **Set Status.** Use `In progress` if you're starting the work now, else `Backlog` / `Ready`. *Ready means
  unblocked*: an item with an open blocker stays in Backlog.
- **Never reopen a delivered item** to carry a defect (B5), and **never close a category**: it is a container
  for the life of the area.
- This skill is intake and tracking only; it does not change code. Follow the auto-loaded repo coding rules
  (`.claude/rules/`) for the implementation.
