# Collecting the board: the exact queries

Every read the roadmap needs, in the order the script issues them. All are **queries**; nothing here
mutates an issue, a project field, a sub-issue link, a dependency or a label.

## Quota first

The org GraphQL budget is **5,000 points per hour**, and bulk per-item reads exhaust it. One full
collection costs **about 40 points (15 × 2 + 8 + 1 + 1)**: 15 issue pages at 2 points each, 8 body
batches of 50, 1 blocked-by batch and 1 field-options read. Measured 2026-10-16.

- **Never one request per issue.** 394 issues × 2 requests is how an earlier session burned the hour's
  budget and then failed *silently*: partially written data with no error.
- Page the whole repository once and filter locally. It is cheaper than searching per item, and it is
  the only way to invert sub-issue links (GitHub gives you `parent`, not `children`).
- **Nothing is reused across runs by default.** Issue pages are re-read every run, a cached body is
  re-read when the page's `updatedAt` changed, and blocked-by lists are never cached (a link change may
  not bump `updatedAt`; on 2026-10-16 five new links left their issues' `updatedAt` untouched).
  `-MaxAgeHours <h>` opts back into page reuse for repeated local builds.
- `gh` surfaces a rate limit as an error in the `errors[]` block; the script backs off (5 s, 10 s, 20 s,
  40 s, capped at 120 s) up to five attempts, then throws.
- Check what is left:

  ```bash
  gh api rate_limit --jq '.resources.graphql'
  ```

## 1. Preflight

```bash
gh auth status          # must succeed; the token needs read access to the repo and to Project #13
```

Without project read scope every board field comes back null, every item lands in `backlog`, and the
roadmap silently reports finished programs as not started. If `notOnBoard` in the snapshot's counts is
large, suspect the scope before suspecting the board.

## 2. Every issue in the repository, 100 at a time (2 points per page, measured 2026-10-16)

Board fields ride along in the same query: `fieldValueByName` costs nothing extra and avoids a second
pass over Project #13 items. **A field that does not exist yet returns `null`, not an error**, so the
same query runs before, during and after the realignment's field pass.

```graphql
query($endCursor: String) {
  repository(owner: "assimalign", name: "cohesion") {
    issues(first: 100, after: $endCursor, states: [OPEN, CLOSED], orderBy: {field: CREATED_AT, direction: ASC}) {
      pageInfo { hasNextPage endCursor }
      nodes {
        number title state stateReason createdAt closedAt updatedAt
        issueType { name }
        parent { number title }
        subIssuesSummary { total completed }
        issueDependenciesSummary { totalBlockedBy totalBlocking }
        labels(first: 20) { nodes { name } }
        projectItems(first: 5) {
          nodes {
            project { number }
            status:   fieldValueByName(name: "Status")   { ... on ProjectV2ItemFieldSingleSelectValue { name } }
            priority: fieldValueByName(name: "Priority") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
            wave:     fieldValueByName(name: "Wave")     { ... on ProjectV2ItemFieldSingleSelectValue { name } }
            category: fieldValueByName(name: "Category") { ... on ProjectV2ItemFieldSingleSelectValue { name optionId } }
            tier:     fieldValueByName(name: "Tier")     { ... on ProjectV2ItemFieldSingleSelectValue { name } }
            program:  fieldValueByName(name: "Program")  { ... on ProjectV2ItemFieldSingleSelectValue { name } }
            area:     fieldValueByName(name: "Area")     { ... on ProjectV2ItemFieldSingleSelectValue { name } }
            kind:     fieldValueByName(name: "Kind")     { ... on ProjectV2ItemFieldSingleSelectValue { name } }
            origin:   fieldValueByName(name: "Origin")   { ... on ProjectV2ItemFieldSingleSelectValue { name } }
          }
        }
      }
    }
  }
}
```

Omit `endCursor` for the first page; take the next cursor from `pageInfo.endCursor` and stop when
`hasNextPage` is false. 1,427 issues on 15 pages as of 2026-10-16. **Ordering by `CREATED_AT ASC` is
deliberate**: page boundaries stay stable between runs.

## 3. The subtree

Two rules, unioned; neither alone is correct:

- **Sub-issue closure** from the root `#1067` (`[L03.02.00] Cohesion - Data Platform`), by inverting the
  `parent` field into a child map. The script throws if #1067's title no longer parses as `[L03.02.00]`:
  that issue number is its only hard-coded subtree fact.
- **Title match** on `^\[L03\.02`: catches an item filed with the right WBS but never linked.

The script fails if the union comes back under 50 items rather than emitting a partial roadmap. A
divergence between the two numbers is itself a finding.

## 4. Bodies, batched 50 aliases per request

Fetched for the subtree only, and only for issues whose `updatedAt` differs from the cached copy
(`cache/bodies.json` is `{ number: { updatedAt, body } }`).

```graphql
query { repository(owner: "assimalign", name: "cohesion") {
  i1068: issue(number: 1068) { number body }
  i1069: issue(number: 1069) { number body }
  # … up to 50 aliases per request
} }
```

Parsed out of each body, with one grammar (the WBS part is shared with tooling E1):

```powershell
$WBS_RE     = '^\[(L\d{2}(?:\.(?:\d{2,}|C\d{2}))+)\]\s*(.*)$'
$DEP_HEADER = '(?i)^#{1,6}\s*(?:Dependencies|Depends on|Blocked by|Prerequisites)\b'
$ANY_HEADER = '^#{1,6}\s'
$DEP_LINE   = '(?i)^\s*[-*]?\s*(?:\*\*)?(?:Blocked by|Depends on)\b(.*)$'
$RELATED    = '(?i)^Related work items:(.*)$'          # only 'blocked-by #N' tokens count
$ISSUE_REF  = '(?<![A-Za-z0-9])#(\d{1,5})\b'
$WBS_REF    = '\[(L\d{2}(?:\.(?:\d{2,}|C\d{2}))+)\]'   # in a dependency section only; must resolve to one issue
$FAMILY     = 'Tier ([123]) family `([a-z0-9-]+)`'
```

- **Section rule:** every line after a `$DEP_HEADER`, up to the next `$ANY_HEADER`, contributes its
  `$ISSUE_REF` and `$WBS_REF` hits. A line matching `$DEP_LINE` anywhere contributes its `#N`. A
  `Related work items:` line contributes only `blocked-by #N`; `related #N` is never a dependency.
- **Dropped and counted** (`counts.dependencyDrops` in the snapshot): self-references, refs that are not
  issues (PR numbers included), and WBS refs that do not resolve to exactly one issue.
- **The plan-family line** fills a plan slot: ``Tier 2 family `sql-upsert` `` places a task at T2 in
  that family (slugs map through `tiers.json` `families[].bodySlugs`).
- **Tier tokens are a suggestion only.** A lone `T1`/`T2`/`T3` in a body raises
  `tier-suggested-by-body` and never sets a tier.
- Also kept: `bodyHeadings` (to verify the #1511 member note), `bodyProgramDocs`, `bodyFirstLine`,
  `bodyLength`, `bodySha256`.

## 4b. Native blocked-by, one batch (1 point for 22 aliases, measured 2026-10-16)

For every subtree item whose `issueDependenciesSummary.totalBlockedBy` is above 0, up to 50 aliases per
request:

```graphql
query { repository(owner: "assimalign", name: "cohesion") {
  i1097: issue(number: 1097) { number blockedBy(first: 50) { totalCount nodes { number } } }
  # …
} }
```

GitHub caps a dependency relationship at 50, so `first: 50` returns the complete list; the script
throws if `totalCount` is larger than the nodes returned. Read every run, never cached.

## 4c. The project's single-select fields (1 point)

```graphql
query { organization(login: "assimalign") { projectV2(number: 13) {
  fields(first: 50) { nodes { ... on ProjectV2SingleSelectField { name options { name } } } }
} } }
```

It says which fields exist (Category, Tier and Program do not until the realignment's Stage 0), which
Area options exist, and the Wave and Priority order the windows use.

## 5. Useful one-off reads

```bash
# the L03.02 epics and the root. --limit must cover the WHOLE repository: the list comes back
# newest-first, so a small limit quietly hides the oldest epics (#4, #5, #31, #50).
gh issue list --repo assimalign/cohesion --state all --limit 2000 \
  --json number,title --jq '.[] | select(.title | test("^\\[L03\\.02\\.\\d\\d\\]")) | "\(.number) \(.title)"'

# one item, with its body
gh issue view 1109 --repo assimalign/cohesion --json number,title,state,labels,body

# one item's native dependencies
gh api repos/assimalign/cohesion/issues/1511/dependencies/blocked_by --jq '.[].number'
```

`gh issue list --search` with a dotted WBS code **silently drops siblings**: it tokenises the dots.
Fetch and filter with `--jq`, as above.

## 6. What is deliberately not collected

- **Timelines, comments and events.** The roadmap positions by dependency, not by history.
- **Per-item project item ids.** Nothing here writes a field.
- **Repositories other than `assimalign/cohesion`** and projects other than #13.

## 7. If the shape of the board changes

Re-discover before assuming:

```bash
gh project view 13 --owner assimalign --format json --jq '{id, title, number}'
gh project field-list 13 --owner assimalign --format json \
  --jq '.fields[] | select(.type=="ProjectV2SingleSelectField") | {name, options: [.options[].name]}'
```

- The Category, Tier and Program options are read every run (§4c). A Category option is matched to its
  category issue by name: **renaming a Category option without renaming its category issue raises
  `category-option-unmatched`** (the carried name is tried next).
- A Tier option not listed in `tiers.json` `tierBoardOptions` raises `tier-option-unmatched`; a Program
  option not named by a `programs.json` `boardOption` raises `program-option-unmatched`.
- A new single-select field joins the collection by adding one `fieldValueByName` alias to the page
  query in §2 and one line to the snapshot item shape, not by a second pass over the board.
