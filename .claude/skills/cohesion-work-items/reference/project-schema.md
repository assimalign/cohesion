# Cohesion GitHub Project — schema & manual recipes

Reference for the `cohesion-work-items` skill. Captured from the live project on 2026-06-26.
The helper script resolves IDs **dynamically**, so these are for the manual path and for
understanding the model. If an ID stops working, re-run the discovery commands at the bottom.

## Coordinates

| Thing | Value |
| --- | --- |
| Repo | `assimalign/cohesion` |
| Org / owner | `assimalign` |
| Project | **#13 "Cohesion"** |
| Project node id | `PVT_kwDOA9eCcc4AwTRy` |

## WBS taxonomy

Work items carry their position in the title as `[<code>] <description>`. The hierarchy is held by
**native GitHub parent/sub-issue links**, and every item is added to Project #13.

| Code shape | Segments | Level | Example | Parent |
| --- | --- | --- | --- | --- |
| `L01.01.00` | 3 (`.00`) | Program root | `[L01.01.00] Cohesion - Foundation Libraries` (#7) | — |
| `L01.01.NN` | 3 | **Area epic** | `[L01.01.11] Foundation - Http` (#314) | program root |
| `L01.01.NN.MM` | 4 | **Feature** | `[L01.01.11.18] Implement HTTP request lifetime…` (#703) | area epic |
| `L01.01.NN.MM.PP` | 5 | **Task** | `[L01.01.11.18.01] Implement the … HttpRequestLifetime` (#713) | feature |

`L` = Libraries program; `L01.01` = Foundation Libraries. Area epics are titled `Foundation - <Area>`.

Branch convention: `feature/<wbs>-<slug>` (e.g. `feature/L01.01.11.14-extended-connect`). The WBS in the
branch names the **feature** currently in flight.

### Current area epics (parents for new sibling features)

| Issue | Code | Area |
| --- | --- | --- |
| #308 | L01.01.01 | Amqp |
| #11  | L01.01.02 | ApplicationModel |
| #14  | L01.01.05 | Content |
| #8   | L01.01.06 | Core |
| #323 | L01.01.07 | DependencyInjection |
| #311 | L01.01.08 | Dns |
| #313 | L01.01.10 | Hosting |
| #314 | L01.01.11 | Http |
| #105 | L01.01.12 | IdentityModel |
| #324 | L01.01.14 | Net |
| #316 | L01.01.15 | OpenApi |
| #317 | L01.01.16 | OpenTelemetry |
| #318 | L01.01.17 | Resilience |
| #325 | L01.01.18 | Security |

(Re-list with: `gh issue list --repo assimalign/cohesion --state open --search '"Foundation -" in:title' --json number,title`)

## Custom fields (single-select)

The script resolves these **by name** at runtime, so the ids below are only for the manual path.

| Field | Field id | Options (name = optionId) |
| --- | --- | --- |
| **Status** | `PVTSSF_lADOA9eCcc4AwTRyzgmmAUg` | Backlog=`f75ad846`, Ready=`08afe404`, In progress=`47fc9ee4`, In review=`4cc61d42`, Done=`98236657` |
| **Priority** | `PVTSSF_lADOA9eCcc4AwTRyzgmmAXc` | P001=`b310d11b` … P007=`3637977d` (97% populated — load-bearing) |
| **Wave** | `PVTSSF_lADOA9eCcc4AwTRyzhBivEo` | W01=`e74c191b`, W02=`9fbf32aa`, W03=`c8f13de9`, W04=`8db9e325`, W05=`e04894c2`, W06=`c1ccc362` (49%) |
| **Kind** | `PVTSSF_lADOA9eCcc4AwTRyzhWf6Os` | Program=`2fe515c1`, Area Epic=`50b6b808`, Feature=`5e827738`, Task=`9d3e180d` |
| **Area** | `PVTSSF_lADOA9eCcc4AwTRyzhWf6Jc` | 18 options, one per area (Amqp…Security) — option name = the area, e.g. Http=`50bb65d8` |
| **Origin** | `PVTSSF_lADOA9eCcc4AwTRyzhWf6JY` | Planned=`19e7b7e6`, DiscoveredTask=`89001270`, DiscoveredFeature=`9d353cbc` |

The script sets **Status, Kind, Area, Origin** on every item it creates (plus Priority/Wave when passed).
`Kind` comes from the WBS depth (Feature/Task), `Area` from the area-epic ancestor's "Foundation - X" title,
`Origin` from the scope-creep classification. Discovered items also get the **`scope-creep`** repo label.

> **Removed:** the old **Size** field (XS–XL) was deleted — it was 0% populated. If T-shirt sizing is wanted
> later, prefer the built-in numeric **Estimate** field over re-adding Size.

Also present (built-in / non-select): Title, Assignees, Labels, Linked pull requests, Milestone, Repository,
Reviewers, Parent issue, Sub-issues progress, Estimate, Iteration, Start date, End date.

### Counting scope creep

```bash
# By label (issues CLI):
gh issue list --repo assimalign/cohesion --label scope-creep --state all --json number,title,state
# By field on the board: filter or group Project #13 by Origin (DiscoveredTask / DiscoveredFeature).
```

## Body template (de-facto standard across issues)

```markdown
## Summary
- <one or two sentences: what and why. For scope-creep items, note it was discovered out of scope.>

## Acceptance Criteria
- <observable, testable outcome>
- Tests cover the new behavior.
- The implementation remains NativeAOT-safe and trimming-safe.

### Standards and Compliance
- <RFC/spec conformance if a wire protocol, else a note that it is a runtime-contract concern>
```

## Manual recipe (when not using the helper script)

```bash
REPO=assimalign/cohesion ; OWNER=assimalign ; PROJ=13
PROJECT_ID=PVT_kwDOA9eCcc4AwTRy

# 1. Find the parent + its node id (feature for a task, area epic for a sibling feature)
gh issue view 703 --repo $REPO --json number,title,id

# 2. Find the next free child number. Do NOT use --search for the dotted code (it silently drops
#    siblings). Fetch all issues and filter on the title with gh's built-in jq (-q):
gh issue list --repo $REPO --state all --limit 5000 --json number,title \
  -q '.[] | select(.title | test("^\\[L01\\.01\\.11\\.14\\.[0-9]{2}\\]")) | .title'
#    Then take the max trailing NN across OPEN and CLOSED, add 1, zero-pad to two digits.

# 3. Create the issue
URL=$(gh issue create --repo $REPO \
  --title '[L01.01.11.14.05] <short imperative description>' \
  --body-file body.md)
NUM=${URL##*/}

# 4. Add to project, capture the project item id
ITEM=$(gh project item-add $PROJ --owner $OWNER --url "$URL" --format json --jq .id)

# 5. Set fields (repeat with the ids from the table above): Status, Kind, Area, Origin, [Priority, Wave]
gh project item-edit --id "$ITEM" --project-id $PROJECT_ID \
  --field-id PVTSSF_lADOA9eCcc4AwTRyzgmmAUg --single-select-option-id 47fc9ee4   # Status = In progress

# 6. Link as a native sub-issue of the parent
PARENT_ID=$(gh issue view 703 --repo $REPO --json id --jq .id)
CHILD_ID=$(gh issue view "$NUM" --repo $REPO --json id --jq .id)
gh api graphql -f query='mutation($p:ID!,$c:ID!){ addSubIssue(input:{issueId:$p, subIssueId:$c}){ subIssue { number } } }' \
  -F p="$PARENT_ID" -F c="$CHILD_ID"
```

## Closing multiple work items from one PR

GitHub only auto-closes an issue when the PR body contains a **closing keyword + that issue's number**.
A single `Closes #1, #2` links only the first. Use **one keyword per issue, one per line**:

```
Closes #713
Closes #714
Closes #715
```

Closing a parent feature does **not** close its sub-issues, and closing every sub-issue does **not** close
the parent. List each work item the PR actually resolves.

The repo has a **PR template** (`.github/pull_request_template.md`) with a "Work items resolved" block, and
**issue forms** (`.github/ISSUE_TEMPLATE/feature.yml`, `task.yml`, `scope-creep.yml`) as a no-PowerShell
fallback. The forms can't compute the next WBS code — prefer the script when exact placement matters.

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
`-Status` only needs to seed the initial state.

## Re-discovering IDs if the schema changes

```bash
# Project node id
gh project view 13 --owner assimalign --format json --jq .id
# All single-select fields with their option ids
gh project field-list 13 --owner assimalign --format json \
  --jq '.fields[] | select(.type=="ProjectV2SingleSelectField") | {name, id, options:[.options[]|{name,id}]}'
```
