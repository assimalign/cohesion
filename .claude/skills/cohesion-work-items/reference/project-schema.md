# Cohesion GitHub Project — schema & manual recipes

Reference for the `cohesion-work-items` skill. First captured from the live project on 2026-06-26; the
Data Platform category layer, the Category / Tier / Program fields and the bug mechanics were added with the
board realignment approved on 2026-10-16 (`docs/programs/DATABASE_BOARD_REALIGNMENT.md`). The helper script
resolves fields and options **by name** at runtime, so the ids below are for the manual path only. If an id
stops working, re-run the discovery commands at the bottom.

## Coordinates

| Thing | Value |
| --- | --- |
| Repo | `assimalign/cohesion` |
| Org / owner | `assimalign` |
| Project | **#13 "Cohesion"** |
| Project node id | `PVT_kwDOA9eCcc4AwTRy` |

## The tree and the WBS

Work items carry a code in the title as `[<code>] <description>`. The **hierarchy is the native GitHub
parent/sub-issue links**, and every item is added to Project #13.

### The numbering rule

> A WBS code is minted once, at filing, and never rewritten. **The prefix names the epic an item was filed
> in; membership is the tree plus `Category` and `Area`.** Grep by code finds filing history; filter by field
> finds membership.

This replaced "a child's code extends its parent's, and prefix containment holds up to the epic" (realignment
decision 9). After the Data Platform run, 70 items carry a prefix from another epic than their Category and 55
from another epic than their tree parent, and no title is rewritten: rewriting would invalidate commit
messages, branch names and WBS citations in bodies and docs. So a parent is **always read from the tree**,
never derived by trimming a code.

| Code shape | Level | Issue type | `Kind` | Example | Parent |
| --- | --- | --- | --- | --- | --- |
| `L01.01.00` (`.00`) | Program root | **Initiative** | Program | `[L01.01.00] Cohesion - Foundation Libraries` (#7) | — |
| `L01.01.NN` | **Area epic** | **Epic** | Area Epic | `[L01.01.11] Foundation - Http` (#314) | program root |
| `L03.02.NN.Cxx` | **Category** (Data Platform) | **Feature** | Feature | `[L03.02.02.C04] SQL - Indexing` | area epic |
| `<parent>.NN` under an epic or category | **Feature** | **Feature** | Feature | `[L01.01.11.18] Implement HTTP request lifetime…` (#703) | area epic or category |
| `<parent>.NN` under a Feature/Story/Task/Bug | **Story** | **Story** | Task | `[L01.01.11.18.01] Implement the … HttpRequestLifetime` (#713) | an item |
| `<parent>.NN` with `-As bug` | **Defect** | **Bug** | Bug | `[L03.02.02.02.02.04.01]` | the **closed** item whose behaviour is wrong |

- **Serials have any width**: `.07`, `.99`, `.100`, `.101`. The next code is the highest existing direct
  child (open **and** closed) plus one; the script then asserts nobody carries it. A two-digit allocator
  re-issued `.100` (#1436 holds `[L01.01.11.100]`).
- **Codes repeat** (7 codes on 14 issues when measured), so a code that names more than one issue is never
  resolved to the first match: the script stops and asks for `-Parent <number>`.
- **Depth is measured, not counted.** GitHub allows eight nesting levels; the script reads the parent and its
  ancestors in one GraphQL query and refuses a ninth level (the root counts as level 1). WBS segment counts
  say nothing about depth: #912 has a 6-segment code and sits at level 5.
- **Categories are never minted by the script.** `-Wbs` refuses a final `Cxx`; a category comes from a
  signed-off design, through the realignment executor or the category recipe below.
- Older Data Platform items sit one level deeper than the shapes above (`[L03.02.00]` #1067 was added above
  the sub-areas). Retype by the parent's kind with the table above if the types drift.

`L01.01` = Foundation Libraries (area epics titled `Foundation - <Area>`); `L03.02` = the Data Platform
(epics titled `Cohesion - Data Platform (<Model>)`).

Branch convention: `feature/<wbs>-<slug>` (e.g. `feature/L01.01.11.14-extended-connect`); `fix/<wbs>-<slug>`
for fixes. The code in the branch names the **item in flight**; any serial width and `Cxx` segments parse.

### Foundation area epics (parents for new features)

| Issue | Code | Area |
| --- | --- | --- |
| #308 | L01.01.01 | Amqp |
| #11  | L01.01.02 | ApplicationModel |
| #14  | L01.01.05 | Content |
| #8   | L01.01.06 | Core |
| #323 | L01.01.07 | DependencyInjection |
| #311 | L01.01.08 | Dns |
| #313 | L01.01.10 | Hosting |
| #314 | L01.01.11 | Http (at 100/100 sub-issues: file under one of its features) |
| #105 | L01.01.12 | IdentityModel |
| #324 | L01.01.14 | Net |
| #316 | L01.01.15 | OpenApi |
| #317 | L01.01.16 | OpenTelemetry |
| #318 | L01.01.17 | Resilience |
| #325 | L01.01.18 | Security |

(Re-list with: `gh issue list --repo assimalign/cohesion --state open --search '"Foundation -" in:title' --json number,title`)

### Data Platform epics and categories

Root `[L03.02.00] Cohesion - Data Platform` (#1067). The 42 categories, their definitions and the
item → category mapping are carried in the roadmap skill's
[`taxonomy.json`](../../cohesion-roadmap/data/taxonomy.json); the category issues exist once the realignment
has created them (windows W1B and W2).

| Epic | Issue | Categories |
| --- | --- | --- |
| L03.02.01 Core Engine | #31 | C01–C07, C09–C18 (17) |
| L03.02.02 SQL | #4 | C01–C05, C07, C08 (7) |
| L03.02.03 DocumentDB | #5 | C01–C05 (5) |
| L03.02.04 KV, Blob, Cache | #57 | C01–C03 (3) |
| L03.02.05 GraphDB | #50 | C01–C05 (5) |
| L03.02.06 Developer Tooling | #856 | C01–C02 (2) |
| L03.02.07 Object Mapping | #1006 | C01 (1) |
| L03.02.09 Program and Roadmap | created in W2 | C01–C02 (2) |
| L03.02.08 Hardening | #1511 | none: **the Hardening program gate**, not an epic (see below) |

**Language-surface categories** (an open item there carries a Tier; realignment decision 19):
`L03.02.01.C07`, `L03.02.02.C01`, `L03.02.03.C01`, `L03.02.05.C01`. SQL C08 is deliberately not on the list.

**A category is never closed**: it is a container for the life of the area. Its progress bar is
informational.

## Custom fields (single-select)

The script resolves these **by name**; the ids are for the manual path.

| Field | Field id | Options |
| --- | --- | --- |
| **Status** | `PVTSSF_lADOA9eCcc4AwTRyzgmmAUg` | Backlog=`f75ad846`, Ready=`08afe404`, In progress=`47fc9ee4`, In review=`4cc61d42`, Done=`98236657` |
| **Priority** | `PVTSSF_lADOA9eCcc4AwTRyzgmmAXc` | P001=`b310d11b` … P007=`3637977d` |
| **Wave** | `PVTSSF_lADOA9eCcc4AwTRyzhBivEo` | W01=`e74c191b`, W02=`9fbf32aa`, W03=`c8f13de9`, W04=`8db9e325`, W05=`e04894c2`, W06=`c1ccc362` |
| **Kind** | `PVTSSF_lADOA9eCcc4AwTRyzhWf6Os` | Program=`2fe515c1`, Area Epic=`50b6b808`, Feature=`5e827738`, Task=`9d3e180d`, **Bug** (added by Stage 0) |
| **Area** | `PVTSSF_lADOA9eCcc4AwTRyzhWf6Jc` | one per Foundation area (Amqp…Security, e.g. Http=`50bb65d8`), plus `Database`, `Database.Sql`, `Database.Documents`, `Database.Graph`, `Database.KeyValue`, `Database.Blob`, `Database.Cache` (added by Stage 0) |
| **Origin** | `PVTSSF_lADOA9eCcc4AwTRyzhWf6JY` | Planned=`19e7b7e6`, DiscoveredTask=`89001270`, DiscoveredFeature=`9d353cbc`, **Defect** (added by Stage 0) |
| **Category** | created by Stage 0 | 42 options, one per category: the category issue's title minus its `[code] ` |
| **Tier** | created by Stage 0 | `T1`, `T2`, `T3`, `Fix-first`, `Prerequisite`, `Done-pre-plan`, `Out-of-scope` (the roadmap skill's `tiers.json` `tierBoardOptions`) |
| **Program** | created by Stage 0 | `Hardening`, `Engine Extensibility`, `Concrete-First API`, `EventSource Tracing`, `Storage Journal`, `Object Mapping` (the roadmap skill's `programs.json`) |
| **Codebase** | — | cohesion, cohesion-platforms (not set by the script) |

### What the script sets on every create, and from where

| Field | Source, in order | When it is required |
| --- | --- | --- |
| Status | `-Status` (default Backlog) | always |
| Kind / issue type | the parent's kind (table above) | always |
| Origin | `-Origin`, else inferred (Planned / DiscoveredTask / DiscoveredFeature / Defect) | always |
| Category | `-Category <code\|issue#>`, else the parent **is** a category | once the item's epic has categories (never inherited: the parent is wrong about half the time) |
| Tier | `-Tier`, else the nearest ancestor's T1/T2/T3/Fix-first/Prerequisite | in a language-surface category, except for a bug |
| Program | `-Program`, else the nearest ancestor's | never |
| Area | the category's own Area, else the `Foundation - X` epic title | in Data Platform, whenever a category is resolved (a category without Area is refused) |
| Priority / Wave | `-Priority`, `-Wave` | in Data Platform, at least one when the item has no tier (bugs included) |
| Family (body line) | `-Family <slug>` | for T2 and T3 work (`-Family none` for out-of-plan work) |

**Preflight:** before anything is created, every option about to be written must exist; a missing option on
Kind, Origin, Category, Tier, Program or a Data Platform Area stops the run with a message naming it. (Status,
Priority, Wave and a Foundation Area keep the old warn-and-skip.) After the writes, the item's field values are
read back and diffed against what was written: a mismatch exits 1.

`Discovered*` items get the **`scope-creep`** label; defects get **`bug`**.

> **Removed:** the old **Size** field (XS–XL) was deleted (0% populated). If sizing is wanted later, prefer the
> built-in numeric **Estimate** field.

Also present (built-in / non-select): Title, Assignees, Labels, Linked pull requests, Milestone, Repository,
Reviewers, Parent issue, Sub-issues progress, Estimate, Iteration, Start date, End date.

### Counting scope creep

```bash
# By label (issues CLI):
gh issue list --repo assimalign/cohesion --label scope-creep --state all --json number,title,state
# By field on the board: filter or group Project #13 by Origin (DiscoveredTask / DiscoveredFeature).
```

## Bugs: the mechanics

The policy (B1–B6 and the not-planned/duplicate guard) is in the skill's `SKILL.md`; the carried copy the
roadmap reads is the roadmap skill's [`bug-policy.json`](../../cohesion-roadmap/data/bug-policy.json).

- **Parent:** the closed (`COMPLETED`) item whose delivered behaviour is wrong, by **issue number**. Refused:
  an open parent (unless `-AllowOpenParent`), a parent closed as `NOT_PLANNED` or `DUPLICATE`, an epic, a
  category, a program root, a program gate.
- **WBS:** `<parentWbs>.NN`, the same allocator as planned work; no reserved range.
- **Type and fields:** issue type **Bug**, `Kind` **Bug**, `Origin` **Defect**, label **bug**. In Data
  Platform also Category (required once the epic has categories), Tier (inherited when an ancestor has one;
  never required) and Wave or Priority when untiered.
- **Stamp:** `> Defect in [<wbs>] (#N), closed <yyyy-mm-dd>.`, with `## Observed`, `## Expected` and
  `### Regression scope` sections in the body.
- **The delivered parent keeps an incomplete progress bar.** GitHub does not normalise sub-issue progress
  when a parent closes, so a delivered item with a bug child shows "0 of 1" for good. That is the point:
  shipped work visibly carries its rework. Reopening it instead would fire *Item reopened → In progress* and
  erase it from the delivered record (B5). Closes cascade in neither direction.

## Program gates

A program gate is an issue that holds a program's acceptance criteria and is **blocked by** its members. It is
**never a parent**. The gates are listed in the roadmap skill's
[`programs.json`](../../cohesion-roadmap/data/programs.json) (`programs[].gate.issue`; today only #1511,
Hardening), which the script reads. To put an item in a gated program:

1. File it by the code it changes (the owning category or item).
2. Pass `-Program Hardening`.
3. Link the gate: `gh api -X POST repos/assimalign/cohesion/issues/1511/dependencies/blocked_by -F issue_id=<REST id>`
   (the REST id: `gh api repos/assimalign/cohesion/issues/<N> --jq .id`).

A task under an existing Hardening feature needs none of this: it inherits Program and its feature is already
linked. A gate failure after a member closed is a bug **under that member** (B1), plus the same link.

## Body template (de-facto standard across issues)

```markdown
## Summary
- <one or two sentences: what and why. For scope-creep items, note it was discovered out of scope.>
- Tier 2 family `sql-upsert` of the owner-approved query-language plan (2026-09-30).   <- only with -Family

## Acceptance Criteria
- <observable, testable outcome>
- Tests cover the new behavior.
- The implementation remains NativeAOT-safe and trimming-safe.

### Standards and Compliance
- <RFC/spec conformance if a wire protocol, else a note that it is a runtime-contract concern>
```

## Manual recipe (when not using the helper script)

Every step the script does, by hand. **Never link a work item directly to an epic that has categories**,
check the parent's room before linking, set **every** field, read them back, and pace bulk work.

```bash
REPO=assimalign/cohesion ; OWNER=assimalign ; PROJ=13
PROJECT_ID=PVT_kwDOA9eCcc4AwTRy

# 1. The parent (a category or an item, by NUMBER) and its room: refuse at 100 sub-issues.
gh api repos/$REPO/issues/703 --jq '{number, title, state, state_reason, sub_issues: .sub_issues_summary.total}'

# 2. The next free child code. Do NOT use --search for a dotted code (it silently drops siblings).
#    Serials can pass 99, so match any width and take max + 1 across OPEN and CLOSED:
gh api --paginate "repos/$REPO/issues?state=all&per_page=100" \
  --jq '.[] | select(.title | test("^\\[L01\\.01\\.11\\.18\\.[0-9]{2,}\\]")) | .title'
#    Then confirm no issue already carries the new code.

# 3. Create the issue, typed in the same call (Feature under an epic or category, Story under an item, Bug).
URL=$(gh issue create --repo $REPO --type Story \
  --title '[L01.01.11.18.05] <short imperative description>' --body-file body.md)
NUM=${URL##*/}

# 4. Add to the project (if it says "Content already exists", the auto-add won: look the item up instead).
ITEM=$(gh project item-add $PROJ --owner $OWNER --url "$URL" --format json --jq .id)

# 5. Set ALL the fields: Status, Kind, Origin, and in Data Platform Category, Area, Tier, Program, and Wave or
#    Priority (repeat with each field id and option id from the table above).
gh project item-edit --id "$ITEM" --project-id $PROJECT_ID \
  --field-id PVTSSF_lADOA9eCcc4AwTRyzgmmAUg --single-select-option-id 47fc9ee4   # Status = In progress
#    Read them back and diff against what you set:
gh api graphql -f query='query($id:ID!){ node(id:$id){ ... on ProjectV2Item { fieldValues(first:50){ nodes{ ... on ProjectV2ItemFieldSingleSelectValue { name field { ... on ProjectV2SingleSelectField { name } } } } } } } }' -F id="$ITEM"

# 6. Re-check the parent's room, then link as a native sub-issue.
PARENT_ID=$(gh issue view 703 --repo $REPO --json id --jq .id)
CHILD_ID=$(gh issue view "$NUM" --repo $REPO --json id --jq .id)
gh api graphql -f query='mutation($p:ID!,$c:ID!){ addSubIssue(input:{issueId:$p, subIssueId:$c}){ subIssue { number } } }' \
  -F p="$PARENT_ID" -F c="$CHILD_ID"
```

**Pacing.** GitHub's secondary limit on content creation is about 500 requests per hour, account-wide and
including web UI clicks, and it cannot be queried. Count every mutation (create, project add, each field
write, the link): a filing is 8–12. **Bulk filing runs at 400 per hour at most, at least 9 s apart — about 35
filings — and never during a realignment window.** Enumerate issues over REST (as above), not
`gh issue list`/GraphQL, and cache ids up front: re-enumerating per item is what exhausted the 5,000-point
GraphQL budget at about 40 items on 2026-09-30.

## Manual recipe — creating a `Cxx` category

**Categories are created by the realignment executor, or by hand with this recipe; never by the script.** The
code is assigned by a signed-off design (the roadmap skill's `taxonomy.json`), not allocated, and a new
category needs the owner's sign-off and a free `Category` option (the field holds at most 45 by the
realignment's own cap).

```bash
EPIC=4                                   # [L03.02.02] Cohesion - Data Platform (SQL)
CODE=L03.02.02.C04                       # from the design, NOT computed
NAME='SQL - Indexing'

# 1. The code must not be in use, and the epic must have room.
gh api --paginate "repos/$REPO/issues?state=all&per_page=100" --jq ".[] | select(.title | startswith(\"[$CODE]\")) | .number"
gh api repos/$REPO/issues/$EPIC --jq .sub_issues_summary.total
# 2. Create it typed; the body is the category's definition: what belongs in it and what does not.
URL=$(gh issue create --repo $REPO --type Feature --title "[$CODE] $NAME" --body-file category-body.md)
# 3. Add it to the project and set Kind=Feature, Status=Backlog, Origin=Planned, Area (the model's
#    Database* option) and Category (its own name). Read them back.
# 4. Link it under the epic (manual recipe step 6). Moving an existing item under it needs
#    addSubIssue(input:{..., replaceParent:true}), behind the same room check.
```

## Closing multiple work items from one PR

GitHub only auto-closes an issue when the PR body contains a **closing keyword + that issue's number**. A single
`Closes #1, #2` links only the first. Use **one keyword per issue, one per line**:

```
Closes #713
Closes #714
Closes #715
```

The script's `-EmitClosesBlock` groups them as **planned / discovered (out-of-scope) / defects in delivered
work**, from the Origin recorded in the per-branch manifest (`.git/cohesion/workitems.tsv`: branch, number,
code, origin, title, parent). Defects are listed but kept out of the creep tally. A row whose link failed is
recorded as `<origin>|UNLINKED` and printed as a warning above the block.

Closing a parent feature does **not** close its sub-issues, and closing every sub-issue does **not** close the
parent. List each work item the PR actually resolves.

The repo has a **PR template** (`.github/pull_request_template.md`) with a "Work items resolved" block, and
**issue forms** (`.github/ISSUE_TEMPLATE/feature.yml`, `task.yml`, `scope-creep.yml`) as a no-PowerShell
fallback. The forms can't compute the next WBS code or set the fields; prefer the script.

## Built-in Project workflows — enable in the UI (one-time, not API-automatable)

GitHub's built-in Project automations are **not** exposed by the REST/GraphQL API or `gh` (only
`deleteProjectV2Workflow` exists), so this is the one step that must be done by hand. At
`https://github.com/orgs/assimalign/projects/13` → **⋯ menu → Workflows**, enable:

| Workflow | Set |
| --- | --- |
| Item added to project | Status → **Backlog** |
| Item closed | Status → **Done** |
| Pull request merged | Status → **Done** |
| Item reopened | Status → **In progress** |

With these on, closing/merging a PR moves every work item it `Closes` to Done automatically, so the script's
`-Status` only needs to seed the initial state. The *Item reopened* workflow is why a delivered item is never
reopened to carry a defect.

## Re-discovering IDs if the schema changes

```bash
# Project node id
gh project view 13 --owner assimalign --format json --jq .id
# All single-select fields with their option ids
gh project field-list 13 --owner assimalign --format json --limit 100 \
  --jq '.fields[] | select(.type=="ProjectV2SingleSelectField") | {name, id, options:[.options[]|{name,id}]}'
```
