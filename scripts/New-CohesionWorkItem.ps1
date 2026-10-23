#!/usr/bin/env pwsh
<#
.SYNOPSIS
    File a GitHub work item (feature / story / bug) in the assimalign/cohesion Project #13, placed from the
    sub-issue tree, numbered with the WBS scheme, typed at create, given its board fields, linked as a native
    sub-issue, and recorded for the PR's Closes block. Also: -Search, -EmitClosesBlock and -Audit.

.DESCRIPTION
    The intake path behind the `cohesion-work-items` skill:

      1. Resolve the PARENT from -Parent <issue#|WBS>, or from the item the current branch names:
         -As task files a child of that item, -As feature a sibling under that item's actual parent in the
         sub-issue tree (never a parent derived by trimming the WBS). A WBS that names 0 or 2+ issues stops.
      2. Read the parent and up to seven ancestors in one GraphQL query: type, Kind, state, sub-issue count
         and the Category / Tier / Program / Area / Priority / Wave values.
      3. Refuse what must not be filed: a program root, a program gate (from the roadmap skill's
         programs.json), a ninth nesting level, a parent already at 100 sub-issues, new scope under a closed
         parent (-AllowClosedParent), a work item directly under an epic that has categories
         (-DirectUnderEpic), and a bug against an open (-AllowOpenParent), not-planned, duplicate or
         container parent.
      4. Choose the issue type and Kind from the parent's kind, and the next free WBS child code (a serial of
         any width, never reused).
      5. Resolve every board field with its source: Category (-Category, or the parent is a category; required
         once the epic has categories), Tier (-Tier, else the nearest tiered ancestor; required in a
         language-surface category, never for a bug), Program (-Program, else the nearest ancestor's), Area
         (the category's own Area, else the "Foundation - X" epic title), and in Data Platform a Wave or
         Priority for untiered work. -Family writes the plan-family body line; T2/T3 work must name one.
      6. Block on a likely duplicate (open, high title overlap) unless -Force.
      7. Preflight that every option about to be written exists, create the issue with its type, add it to
         the project (idempotent), set the fields and read them back, re-check the parent's room and link
         the sub-issue, then record the item in the per-branch manifest. A failed link or read-back exits 1
         and the manifest row is marked UNLINKED.

    Category, Tier and Program are optional project fields: before they exist, a filing that does not need
    them behaves as it always did, and one that does fails with a message naming the missing field.

    "Origin" classifies how the item was born:
      Planned           - filed deliberately under an explicit -Parent.
      DiscoveredTask    - in-scope creep: a separable child of the item the branch names.
      DiscoveredFeature - escaped-scope creep: a sibling of the item the branch names.
      Defect            - a defect in behaviour a CLOSED item delivered (-As bug); a sub-issue of that item,
                          never creep, listed apart in the Closes block.

    Every network call goes through a rate-limit-aware wrapper (Retry-After, the primary reset, else
    exponential backoff). Single-select options are resolved by NAME at runtime. See
    ../reference/project-schema.md for the board model and the manual recipe.

.EXAMPLE
    # Before creating, see whether anything already covers the work:
    ./New-CohesionWorkItem.ps1 -Search "websocket bootstrap http3"

.EXAMPLE
    # A separable task discovered on the branch's item (feature/L01.01.11.14-extended-connect):
    ./New-CohesionWorkItem.ps1 -Title "Validate CONNECT authority form against RFC 9110 origin rules"

.EXAMPLE
    # Out-of-scope work: a sibling of the branch's item, under that item's actual parent:
    ./New-CohesionWorkItem.ps1 -As feature -Title "Implement HTTP/1.1 informational (1xx) responses" -Wave W02

.EXAMPLE
    # Data Platform, under a category, untiered, so a Wave or Priority is required:
    ./New-CohesionWorkItem.ps1 -Parent L03.02.02.C04 -Title "Rebuild an index online" -Priority P003

.EXAMPLE
    # Defect in behaviour a CLOSED item delivered (-Parent must be an issue NUMBER):
    ./New-CohesionWorkItem.ps1 -As bug -Parent 912 -Title "TIMESTAMP index keys disagree with SQL equality" -Priority P002

.EXAMPLE
    # Read-only decay report for the Data Platform tree (exit 1 when it finds anything):
    ./New-CohesionWorkItem.ps1 -Audit

.EXAMPLE
    # Assemble the grouped "Closes #N" block (planned / discovered / defects) for the PR body:
    ./New-CohesionWorkItem.ps1 -EmitClosesBlock
#>
[CmdletBinding(DefaultParameterSetName = 'Create')]
param(
    [Parameter(ParameterSetName = 'Create', Mandatory = $true)]
    [string] $Title,

    # The relation to the item the current branch names, when -Parent is not given:
    #   task    -> a child of that item (the default)
    #   feature -> a sibling: a child of that item's actual parent in the sub-issue tree
    #   bug     -> a defect in behaviour a CLOSED item delivered; requires -Parent <issue number>
    # With an explicit -Parent, task and feature have no effect: the type follows the parent's kind.
    [Parameter(ParameterSetName = 'Create')]
    [ValidateSet('task', 'feature', 'bug')]
    [string] $As = 'task',

    # Explicit parent: an issue number (e.g. 314) or a WBS code that names exactly one issue (e.g. L01.01.11).
    [Parameter(ParameterSetName = 'Create')]
    [string] $Parent,

    # Pre-assigned WBS code for the new item. A direct child of the parent's code, ending in a serial of any
    # width; a Cxx category code is refused (categories are never minted by this script).
    [Parameter(ParameterSetName = 'Create')]
    [ValidatePattern('(?-i)^L\d{2}(?:\.(?:\d{2,}|C\d{2}))*\.\d{2,}$')]
    [string] $Wbs,

    # How the item was born. Inferred when omitted; pass to override.
    [Parameter(ParameterSetName = 'Create')]
    [ValidateSet('Planned', 'DiscoveredTask', 'DiscoveredFeature', 'Defect')]
    [string] $Origin,

    [Parameter(ParameterSetName = 'Create')]
    [string] $Summary,

    [Parameter(ParameterSetName = 'Create')]
    [string[]] $Acceptance,

    [Parameter(ParameterSetName = 'Create')]
    [string[]] $Standards,

    # Provide a complete body instead of the generated template.
    [Parameter(ParameterSetName = 'Create')]
    [string] $BodyFile,

    [Parameter(ParameterSetName = 'Create')]
    [ValidateSet('Backlog', 'Ready', 'In progress', 'In review', 'Done')]
    [string] $Status = 'Backlog',

    [Parameter(ParameterSetName = 'Create')]
    [ValidateSet('P001', 'P002', 'P003', 'P004', 'P005', 'P006', 'P007')]
    [string] $Priority,

    [Parameter(ParameterSetName = 'Create')]
    [ValidateSet('W01', 'W02', 'W03', 'W04', 'W05', 'W06')]
    [string] $Wave,

    # The Category: a category code (L03.02.02.C04) or the category issue's number. Required once the
    # item's epic has categories, unless the parent is itself a category.
    [Parameter(ParameterSetName = 'Create')]
    [string] $Category,

    # The Tier field option (T1, T2, T3, Fix-first, Prerequisite, Done-pre-plan, Out-of-scope; the names
    # are the keys of the roadmap skill's tiers.json tierBoardOptions). Inherited from the nearest tiered
    # ancestor when omitted.
    [Parameter(ParameterSetName = 'Create')]
    [string] $Tier,

    # The Program field option (a boardOption in the roadmap skill's programs.json). Inherited from the
    # nearest ancestor that has one when omitted.
    [Parameter(ParameterSetName = 'Create')]
    [string] $Program,

    # The plan family slug (a family id or one of its bodySlugs in tiers.json); writes the body line
    # "Tier N family `slug`" the roadmap reads. Required for T2 and T3 work; "none" files out-of-plan work.
    [Parameter(ParameterSetName = 'Create')]
    [string] $Family,

    # Allow a work item directly under an epic that has categories. The only override of that guard.
    [Parameter(ParameterSetName = 'Create')]
    [switch] $DirectUnderEpic,

    # Allow -As bug to attach to a parent that is still OPEN (bug policy B2 says that is in-scope rework).
    [Parameter(ParameterSetName = 'Create')]
    [switch] $AllowOpenParent,

    # Allow a non-bug child under a CLOSED parent (new scope belongs under the owning category instead).
    [Parameter(ParameterSetName = 'Create')]
    [switch] $AllowClosedParent,

    # GitHub labels to apply. 'scope-creep' is added for discovered items and 'bug' for defects.
    [Parameter(ParameterSetName = 'Create')]
    [string[]] $Label,

    # Create even when a likely-duplicate existing item is found. Overrides nothing else.
    [Parameter(ParameterSetName = 'Create')]
    [switch] $Force,

    # Read-only: resolve, validate and print everything the run would do, then stop.
    [Parameter(ParameterSetName = 'Create')]
    [switch] $DryRun,

    # Read-only: list existing work items whose titles look similar to the given text, then exit.
    [Parameter(ParameterSetName = 'Search', Mandatory = $true)]
    [string] $Search,

    # Print the grouped "Closes #N" block for every work item captured on the current branch, then exit.
    [Parameter(ParameterSetName = 'Closes', Mandatory = $true)]
    [switch] $EmitClosesBlock,

    # Read-only decay report over the tree under -Root (default #1067, Data Platform). Exit 1 on findings.
    [Parameter(ParameterSetName = 'Audit', Mandatory = $true)]
    [switch] $Audit,

    [Parameter(ParameterSetName = 'Audit')]
    [int] $Root = 1067
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- Constants -------------------------------------------------------------------------------
$Owner       = 'assimalign'
$Repo        = 'assimalign/cohesion'
$ProjectNum  = 13
# A WBS segment is a serial of any width or a Cxx category code. The prefix names the epic an item was
# filed in; membership is the sub-issue tree plus the Category and Area fields.
$WbsSegRx    = '(?:\d{2,}|C\d{2})'
$WbsRx       = 'L\d{2}(?:\.' + $WbsSegRx + ')+'
# GitHub allows eight levels of nested sub-issues; the root counts as level 1 (the stricter reading).
$MaxNestingLevel = 8
$SubIssueCap     = 100
# The Data Platform program root ([L03.02.00]). A filing under it is a Data Platform filing.
$DataPlatformRoot = 1067
# Categories whose open work must carry a Tier (realignment decision 19).
$LanguageSurfaceCategories = @('L03.02.01.C07', 'L03.02.02.C01', 'L03.02.03.C01', 'L03.02.05.C01')
# Tier options a child inherits from its nearest tiered ancestor. Done-pre-plan and Out-of-scope describe
# the ancestor's own work, not new work under it.
$InheritableTiers = @('T1', 'T2', 'T3', 'Fix-first', 'Prerequisite')
# The roadmap skill's carried data: the program gates (E18), the tier options and plan families (E19).
$RoadmapDataDir = Join-Path $PSScriptRoot '../.claude/skills/cohesion-roadmap/data'
$script:AllIssuesCache = $null
$script:FieldMapCache  = $null
$script:ProjectId      = $null
$StopWords = @('add','the','and','for','with','to','of','a','an','is','are','be','via','use','new',
               'implement','implementation','support','enable','provide','create','make','into','over')

# --- Rate-limit-aware gh wrapper -------------------------------------------------------------

function Test-RateLimited {
    param([string] $Text)
    return [bool]($Text -match '(?i)(api rate limit exceeded|secondary rate limit|exceeded a secondary|abuse detection|was submitted too quickly|RATE_LIMITED|retry[- ]after|\b429\b)')
}

function Get-EpochNow { return [int][double]::Parse((Get-Date -UFormat %s)) }

function Get-BackoffSeconds {
    param([int] $Attempt, [string] $Text)
    # 1. Honor an explicit Retry-After (typical for secondary limits)
    $m = [regex]::Match($Text, '(?i)retry[- ]after[:\s]+(\d+)')
    if ($m.Success) { return [Math]::Min([int]$m.Groups[1].Value + 1, 300) }
    # 2. For a primary limit, sleep until the soonest exhausted resource resets
    if ($Text -match '(?i)api rate limit exceeded') {
        try {
            $rl = & gh api rate_limit 2>$null | ConvertFrom-Json
            $resets = @()
            foreach ($p in $rl.resources.PSObject.Properties) {
                if ($p.Value.remaining -le 0) { $resets += [int]$p.Value.reset }
            }
            if ($resets.Count -gt 0) {
                $wait = (($resets | Measure-Object -Minimum).Minimum) - (Get-EpochNow)
                if ($wait -gt 0) { return [Math]::Min($wait + 2, 300) }
            }
        } catch { Write-Verbose "rate_limit read failed: $_" }
    }
    # 3. Exponential backoff with jitter
    $base = [Math]::Min(8 * [Math]::Pow(2, $Attempt - 1), 180)
    return [int]($base + (Get-Random -Minimum 0 -Maximum 5))
}

# Run gh with retry on rate limits. -AllowFailure returns $null instead of throwing on terminal failure.
# -GraphQL also treats an exit-0 response carrying a rate-limited `errors` array as retryable.
function Invoke-GhRetry {
    param(
        [Parameter(Mandatory = $true)][string[]] $GhArgs,
        [switch] $AllowFailure,
        [switch] $GraphQL,
        [int] $MaxAttempts = 6
    )
    for ($attempt = 1; ; $attempt++) {
        $out  = & gh @GhArgs 2>&1
        $code = $LASTEXITCODE
        $text = ($out | Out-String)
        # A GraphQL call exits 0 even when its body carries an `errors` array, so treat that as a failure.
        $gqlError  = ($code -eq 0 -and $GraphQL -and $text -match '"errors"')
        $retryable = if ($code -ne 0) { Test-RateLimited $text }
                     elseif ($gqlError) { Test-RateLimited $text }
                     else { $false }
        if ($code -eq 0 -and -not $gqlError) { return @($out | Where-Object { $_ -is [string] }) }   # clean success
        if ($retryable -and $attempt -lt $MaxAttempts) {
            $wait = Get-BackoffSeconds -Attempt $attempt -Text $text
            Write-Warning "GitHub rate limit hit; backing off $([int]$wait)s then retrying (attempt $attempt/$MaxAttempts)."
            Start-Sleep -Seconds $wait
            continue
        }
        # terminal: a non-zero exit, or a GraphQL error body that isn't a rate limit
        if ($AllowFailure) { return $null }
        throw "gh $($GhArgs -join ' ') failed (attempt $attempt):`n$text"
    }
}

# DryRun-aware wrapper for MUTATING gh calls.
function Invoke-Gh {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $GhArgs)
    if ($DryRun) { Write-Host "DRYRUN gh $($GhArgs -join ' ')" -ForegroundColor DarkYellow; return $null }
    return Invoke-GhRetry -GhArgs $GhArgs
}

function ConvertFrom-GhJson {
    param($Lines)
    $text = (@($Lines) | Where-Object { $_ -is [string] }) -join "`n"
    if (-not $text.Trim()) { return $null }
    return $text | ConvertFrom-Json
}

# A property of a JSON object, or $null when it is absent (StrictMode makes a missing member an error).
function Get-Prop {
    param($Object, [string] $Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}

# --- Issue / WBS helpers ---------------------------------------------------------------------

function Get-TitleWbs {
    param([string] $IssueTitle)
    $m = [regex]::Match($IssueTitle, '^\[(' + $WbsRx + ')\]')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Get-BranchName { return (& git rev-parse --abbrev-ref HEAD 2>$null) }

function Get-BranchWbs {
    $branch = Get-BranchName
    if (-not $branch) { return $null }
    $m = [regex]::Match($branch, $WbsRx)
    if ($m.Success) { return $m.Value }
    return $null
}

# Absolute, cwd-independent, per-worktree manifest path under the git dir.
function Get-ManifestPath {
    $gitDir = (& git rev-parse --absolute-git-dir 2>$null)
    if (-not $gitDir) { return $null }
    $dir = Join-Path $gitDir 'cohesion'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    return (Join-Path $dir 'workitems.tsv')
}

# Every issue (open + closed) once, over REST so the GraphQL budget is left alone. Filtered client-side
# because GitHub's search tokenizes '[', ']' and '.' unreliably and drops dotted-code siblings.
function Get-AllIssues {
    if ($null -ne $script:AllIssuesCache) { return $script:AllIssuesCache }
    $jq = '.[] | select(has("pull_request") | not) | {number, title, state: (.state | ascii_upcase), id: .node_id, url: .html_url}'
    $out = Invoke-GhRetry -GhArgs @('api', '--paginate', "repos/$Repo/issues?state=all&per_page=100", '--jq', $jq)
    $arr = @($out | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    if ($arr.Count -eq 0) { throw "Issue enumeration returned no issues; refusing to compute WBS codes from an empty list." }
    $script:AllIssuesCache = $arr
    return $script:AllIssuesCache
}

# Every issue whose title starts with exactly [<code>].
function Get-IssuesByWbs {
    param([string] $Code)
    $rx = '^\[' + [regex]::Escape($Code) + '\]'
    return @((Get-AllIssues) | Where-Object { $_.title -cmatch $rx })
}

# The one issue a WBS code names, or stop: codes repeat (7 codes on 14 issues when measured), and taking the
# first match files under whichever issue GitHub happened to list first.
function Resolve-UniqueWbs {
    param([string] $Code, [string] $What)
    $hits = @(Get-IssuesByWbs $Code)
    if ($hits.Count -eq 1) { return $hits[0] }
    if ($hits.Count -eq 0) { throw "$What [$Code] names no issue. Pass -Parent <issue number>." }
    $list = ($hits | Sort-Object number | ForEach-Object { "#$($_.number) ($($_.state.ToLowerInvariant())) $($_.title)" }) -join '; '
    throw "$What [$Code] names $($hits.Count) issues: $list. Pass -Parent <issue number>."
}

# The next free DIRECT child code under a parent code, across OPEN and CLOSED issues, with serials of any
# width (a two-digit match cannot see .100 and re-issues it). Asserts the result is unused.
function Get-NextChildWbs {
    param([string] $ParentWbs)
    $rx = '^\[' + [regex]::Escape($ParentWbs) + '\.(\d{2,})\]'
    $max = 0
    foreach ($i in (Get-AllIssues)) {
        $m = [regex]::Match($i.title, $rx)
        if ($m.Success) { $n = [int]$m.Groups[1].Value; if ($n -gt $max) { $max = $n } }
    }
    $next = '{0}.{1:D2}' -f $ParentWbs, ($max + 1)
    $taken = @(Get-IssuesByWbs $next)
    if ($taken.Count -gt 0) {
        throw "Allocated [$next], but #$($taken[0].number) already carries that code. Pass -Wbs with a free code."
    }
    return $next
}

# The issue and up to seven ancestors in one GraphQL query (about 1 point). fieldValueByName returns null
# for a field the project does not have, so Category, Tier and Program read as empty before they exist.
$ChainQuery = @'
fragment N on Issue {
  id number title url state stateReason closedAt issueType { name } subIssuesSummary { total completed }
  projectItems(first: 10) { nodes { id project { number }
    kind: fieldValueByName(name: "Kind") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
    area: fieldValueByName(name: "Area") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
    category: fieldValueByName(name: "Category") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
    tier: fieldValueByName(name: "Tier") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
    program: fieldValueByName(name: "Program") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
    priority: fieldValueByName(name: "Priority") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
    wave: fieldValueByName(name: "Wave") { ... on ProjectV2ItemFieldSingleSelectValue { name } } } }
}
query($n: Int!) {
  repository(owner: "assimalign", name: "cohesion") { issue(number: $n) { ...N
    parent { ...N parent { ...N parent { ...N parent { ...N parent { ...N parent { ...N parent { ...N
      parent { number } } } } } } } } } }
}
'@

function ConvertTo-ChainNode {
    param($Raw)
    $item = $null
    foreach ($pi in @(Get-Prop (Get-Prop $Raw 'projectItems') 'nodes')) {
        if ($pi -and [int](Get-Prop (Get-Prop $pi 'project') 'number') -eq $ProjectNum) { $item = $pi; break }
    }
    $value = { param($Name) $v = Get-Prop $item $Name; if ($v) { [string](Get-Prop $v 'name') } else { $null } }
    $title = [string](Get-Prop $Raw 'title')
    $summary = Get-Prop $Raw 'subIssuesSummary'
    return [pscustomobject]@{
        number       = [int](Get-Prop $Raw 'number')
        nodeId       = [string](Get-Prop $Raw 'id')
        url          = [string](Get-Prop $Raw 'url')
        title        = $title
        wbs          = Get-TitleWbs $title
        state        = [string](Get-Prop $Raw 'state')
        stateReason  = [string](Get-Prop $Raw 'stateReason')
        closedAt     = Get-Prop $Raw 'closedAt'
        issueType    = [string](Get-Prop (Get-Prop $Raw 'issueType') 'name')
        subTotal     = [int](Get-Prop $summary 'total')
        subCompleted = [int](Get-Prop $summary 'completed')
        itemId       = Get-Prop $item 'id'
        kindField    = & $value 'kind'
        area         = & $value 'area'
        category     = & $value 'category'
        tier         = & $value 'tier'
        program      = & $value 'program'
        priority     = & $value 'priority'
        wave         = & $value 'wave'
    }
}

# Returns @{ nodes = [issue, parent, grandparent, ...]; overflow = more ancestors than were read }.
function Get-IssueChain {
    param([int] $Number)
    $resp = ConvertFrom-GhJson (Invoke-GhRetry -GraphQL -GhArgs @('api', 'graphql', '-f', "query=$ChainQuery", '-F', "n=$Number"))
    $cur = Get-Prop (Get-Prop (Get-Prop $resp 'data') 'repository') 'issue'
    if (-not $cur) { throw "Issue #$Number not found in $Repo." }
    $nodes = [System.Collections.Generic.List[object]]::new()
    $overflow = $false
    for ($i = 0; $i -le 7 -and $cur; $i++) {
        $nodes.Add((ConvertTo-ChainNode $cur))
        $next = Get-Prop $cur 'parent'
        if ($i -eq 7) { $overflow = ($null -ne $next); break }
        $cur = $next
    }
    return [pscustomobject]@{ nodes = @($nodes); overflow = $overflow }
}

# The kind of an issue as a parent: Category, Program (root), AreaEpic, Feature, Story, Task or Bug.
# -NoThrow returns 'Unknown' for an untyped issue with no Kind (used when scanning ancestors).
function Get-IssueKind {
    param($Node, [switch] $NoThrow)
    if ($Node.wbs -and $Node.wbs -cmatch '\.C\d{2}$') { return 'Category' }
    if ($Node.issueType -eq 'Initiative' -or $Node.kindField -eq 'Program' -or ($Node.wbs -and $Node.wbs -match '\.00$')) { return 'Program' }
    if ($Node.issueType -eq 'Epic' -or (-not $Node.issueType -and $Node.kindField -eq 'Area Epic')) { return 'AreaEpic' }
    if ($Node.issueType -in @('Feature', 'Story', 'Task', 'Bug')) { return $Node.issueType }
    if (-not $Node.issueType -and $Node.kindField -in @('Feature', 'Task', 'Bug')) { return $Node.kindField }
    if ($NoThrow) { return 'Unknown' }
    throw "#$($Node.number) has no issue type and no Kind, so its children's type cannot be chosen. Type it first ('$($Node.title)')."
}

# Direct sub-issues over REST (one page: the cap is 100).
function Get-SubIssues {
    param([int] $Number)
    $jq = '.[] | {number, title, state: (.state | ascii_upcase), total: (.sub_issues_summary.total // 0), completed: (.sub_issues_summary.completed // 0)}'
    $out = Invoke-GhRetry -GhArgs @('api', "repos/$Repo/issues/$Number/sub_issues?per_page=100", '--jq', $jq)
    return @($out | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
}

# Refuse a parent without room for -Adding more sub-issues. -Known skips the read when the count is fresh.
function Assert-SubIssueRoom {
    param([int] $ParentNumber, [int] $Adding = 1, [int] $Known = -1)
    $total = $Known
    if ($total -lt 0) {
        $out = Invoke-GhRetry -GhArgs @('api', "repos/$Repo/issues/$ParentNumber", '--jq', '.sub_issues_summary.total // 0')
        $total = [int]((@($out) -join '').Trim())
    }
    if ($total + $Adding -gt $SubIssueCap) {
        throw "#$ParentNumber has $total/$SubIssueCap sub-issues: file under one of its children instead (GitHub refuses a sub-issue past $SubIssueCap)."
    }
    return $total
}

# The program gates (programs[].gate.issue) from the roadmap skill's programs.json.
function Get-ProgramGates {
    $path = Join-Path $RoadmapDataDir 'programs.json'
    if (-not (Test-Path $path)) {
        Write-Warning "Program gate list unavailable ($path not found); no program gate is refused."
        return @()
    }
    $data = Get-Content $path -Raw | ConvertFrom-Json
    $gates = foreach ($pr in @(Get-Prop $data 'programs')) {
        $g = Get-Prop $pr 'gate'
        if ($g -and (Get-Prop $g 'issue')) {
            [pscustomobject]@{ issue = [int]$g.issue; program = [string](Get-Prop $pr 'boardOption'); label = [string](Get-Prop $g 'label') }
        }
    }
    return @($gates)
}

function Get-ProgramOptions {
    $path = Join-Path $RoadmapDataDir 'programs.json'
    if (-not (Test-Path $path)) { return $null }
    $data = Get-Content $path -Raw | ConvertFrom-Json
    return @(@(Get-Prop $data 'programs') | ForEach-Object { [string](Get-Prop $_ 'boardOption') } | Where-Object { $_ })
}

# tiers.json: the Tier option -> plan tier map and the plan families.
function Get-TierData {
    $path = Join-Path $RoadmapDataDir 'tiers.json'
    if (-not (Test-Path $path)) { return $null }
    $data = Get-Content $path -Raw | ConvertFrom-Json
    $map = [ordered]@{}
    foreach ($p in (Get-Prop $data 'tierBoardOptions').PSObject.Properties) { if ($null -ne $p.Value) { $map[$p.Name] = [string]$p.Value } }
    return [pscustomobject]@{ options = $map; families = @(Get-Prop $data 'families') }
}

# The plan language a category or epic code belongs to, for listing families.
function Get-PlanLanguage {
    param([string] $Code)
    if (-not $Code) { return $null }
    switch -regex ($Code) {
        '^L03\.02\.02(\.|$)' { return 'SQL' }
        '^L03\.02\.03(\.|$)' { return 'OQL' }
        '^L03\.02\.05(\.|$)' { return 'GQL' }
        '^L03\.02\.01(\.|$)' { return 'Cross-cutting' }
    }
    return $null
}

# --- Existing-item discovery (reuse before creating) -----------------------------------------

function Get-TitleTokens {
    param([string] $Text)
    if (-not $Text) { return @() }
    return @(($Text.ToLowerInvariant() -split '[^a-z0-9]+') |
        Where-Object { $_.Length -ge 3 -and $StopWords -notcontains $_ } | Select-Object -Unique)
}

# Rank existing issues by significant-word overlap with the given title text (the [WBS] prefix is
# stripped from candidates before tokenizing). Returns up to -Top scored candidates.
function Find-SimilarIssues {
    param([string] $TitleText, [int] $Top = 6)
    $want = @(Get-TitleTokens $TitleText)
    if ($want.Count -eq 0) { return @() }
    $scored = foreach ($i in (Get-AllIssues)) {
        $bare = ($i.title -replace '^\[[^\]]+\]\s*', '')
        $have = @(Get-TitleTokens $bare)
        if ($have.Count -eq 0) { continue }
        $shared = @($want | Where-Object { $have -contains $_ })
        if ($shared.Count -eq 0) { continue }
        $union = @($want + $have | Select-Object -Unique).Count
        [pscustomobject]@{
            Number  = $i.number
            Title   = $i.title
            State   = $i.state
            Shared  = $shared.Count
            Jaccard = [Math]::Round($shared.Count / $union, 2)
            Terms   = ($shared -join ', ')
        }
    }
    return @($scored | Sort-Object -Property @{Expression='Shared';Descending=$true}, @{Expression='Jaccard';Descending=$true} | Select-Object -First $Top)
}

# --- Project field helpers -------------------------------------------------------------------

# The project's id and its fields with their options, in one GraphQL query (1 point). `gh project field-list`
# reads items x field values as well and costs about 101 points a call.
$FieldMapQuery = @'
query {
  organization(login: "assimalign") { projectV2(number: 13) { id
    fields(first: 50) { nodes {
      ... on ProjectV2SingleSelectField { id name options { id name } }
      ... on ProjectV2IterationField { id name }
      ... on ProjectV2Field { id name } } } } }
}
'@

function Get-FieldMap {
    if ($null -ne $script:FieldMapCache) { return $script:FieldMapCache }
    $resp = ConvertFrom-GhJson (Invoke-GhRetry -GraphQL -GhArgs @('api', 'graphql', '-f', "query=$FieldMapQuery"))
    $project = Get-Prop (Get-Prop (Get-Prop $resp 'data') 'organization') 'projectV2'
    if (-not $project) { throw "Project #$ProjectNum of $Owner could not be read." }
    $script:ProjectId = [string](Get-Prop $project 'id')
    $map = @{}
    foreach ($f in @(Get-Prop (Get-Prop $project 'fields') 'nodes')) {
        $name = [string](Get-Prop $f 'name')
        if (-not $name) { continue }
        $opts = @{}
        foreach ($o in @(Get-Prop $f 'options')) { if ($o) { $opts[[string]$o.name] = [string]$o.id } }
        $map[$name] = @{ id = [string](Get-Prop $f 'id'); options = $opts }
    }
    $script:FieldMapCache = $map
    return $map
}

function Set-ProjectField {
    param($FieldMap, [string] $ProjectId, [string] $ItemId, [string] $FieldName, [string] $OptionName)
    if (-not $OptionName) { return $false }
    $field = $FieldMap[$FieldName]
    if (-not $field) { Write-Host "  ($FieldName is not a project field; skipped)" -ForegroundColor DarkGray; return $false }
    $optId = $field.options[$OptionName]
    if (-not $optId) { Write-Warning "Option '$OptionName' not found on field '$FieldName'; skipping."; return $false }
    Invoke-Gh project item-edit --id $ItemId --project-id $ProjectId `
        --field-id $field.id --single-select-option-id $optId | Out-Null
    Write-Host "  set $FieldName = $OptionName" -ForegroundColor DarkGray
    return $true
}

function New-BodyTemplate {
    param([string] $Sum, [string[]] $Acc, [string[]] $Std, [switch] $Bug, [string] $ParentRef, [string] $PlanLine)
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('## Summary')
    if ($Sum) { [void]$sb.AppendLine($Sum) }
    elseif ($Bug) { [void]$sb.AppendLine('- <one or two sentences: which delivered behaviour is wrong, and how it was found>') }
    else { [void]$sb.AppendLine('- <one or two sentences: what and why; note this was captured as out-of-scope work>') }
    if ($PlanLine) { [void]$sb.AppendLine($PlanLine) }
    [void]$sb.AppendLine('')
    if ($Bug) {
        [void]$sb.AppendLine('## Observed')
        [void]$sb.AppendLine('- <what happens today, with the smallest reproduction>')
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('## Expected')
        [void]$sb.AppendLine('- <what the parent item''s acceptance criteria require instead>')
        [void]$sb.AppendLine('')
    }
    [void]$sb.AppendLine('## Acceptance Criteria')
    if ($Acc) { foreach ($a in $Acc) { [void]$sb.AppendLine("- $a") } }
    elseif ($Bug) {
        $ref = if ($ParentRef) { $ParentRef } else { 'the parent item' }
        [void]$sb.AppendLine("- The behaviour matches $ref's acceptance criteria again.")
        [void]$sb.AppendLine('- A regression test fails before the fix and passes after it.')
        [void]$sb.AppendLine('- The implementation remains NativeAOT-safe and trimming-safe.')
    }
    else {
        [void]$sb.AppendLine('- <observable, testable outcome>')
        [void]$sb.AppendLine('- Tests cover the new behavior.')
        [void]$sb.AppendLine('- The implementation remains NativeAOT-safe and trimming-safe.')
    }
    [void]$sb.AppendLine('')
    if ($Bug) {
        [void]$sb.AppendLine('### Regression scope')
        [void]$sb.AppendLine('- <which other delivered behaviour shares the broken code path, and what was checked>')
        [void]$sb.AppendLine('')
    }
    [void]$sb.AppendLine('### Standards and Compliance')
    if ($Std) { foreach ($s in $Std) { [void]$sb.AppendLine("- $s") } }
    else { [void]$sb.AppendLine('- Follows the repo coding rules in `.claude/rules/` (interface-first, file-scoped namespaces, area-scoped exceptions).') }
    return $sb.ToString()
}

# Manifest rows: branch, number, wbs, origin[|UNLINKED][|INCOMPLETE:<step>], title, parent.
# UNLINKED: the item is not a sub-issue of its parent. INCOMPLETE:<step>: the filing stopped at that step
# (create, project-add, fields, read-back); re-running the same filing resumes it.
function Read-ManifestRows {
    param([string] $Branch)
    $manifest = Get-ManifestPath
    if (-not $manifest -or -not (Test-Path $manifest)) { return @() }
    $rows = foreach ($l in (Get-Content $manifest)) {
        if (-not $l) { continue }
        $parts = $l -split "`t"
        if ($parts.Count -lt 2) { continue }          # skip malformed rows rather than throwing
        if ($Branch -and $parts[0] -ne $Branch) { continue }
        if ($parts[1] -notmatch '^\d+$') { continue }  # second field must be an issue number
        $flags = @((($(if ($parts.Count -ge 4 -and $parts[3]) { $parts[3] } else { 'Planned' })) -split '\|') | Where-Object { $_ })
        $incomplete = @($flags | Where-Object { $_ -like 'INCOMPLETE:*' } | ForEach-Object { $_.Substring(11) }) | Select-Object -First 1
        [pscustomobject]@{
            branch     = $parts[0]
            number     = [int]$parts[1]
            wbs        = $(if ($parts.Count -ge 3) { $parts[2] } else { '' })
            origin     = $(if ($flags.Count) { $flags[0] } else { 'Planned' })
            unlinked   = ($flags -contains 'UNLINKED')
            incomplete = $incomplete
            title      = $(if ($parts.Count -ge 5) { $parts[4] } else { '' })
            parent     = $(if ($parts.Count -ge 6 -and $parts[5] -match '^\d+$') { [int]$parts[5] } else { $null })
        }
    }
    return @($rows)
}

# Record a filing in the per-branch manifest, replacing an earlier row for the same branch and issue (a resumed
# filing) instead of adding a second one. Tabs and newlines are stripped so they cannot corrupt a row.
function Write-ManifestRow {
    param([string] $Branch, [int] $Number, [string] $Code, [string] $RowOrigin, [string] $RowTitle, [int] $ParentNumber)
    $manifest = Get-ManifestPath
    if (-not $manifest) { Write-Warning "No git directory, so #$Number was not recorded in a branch manifest."; return $null }
    $row = "{0}`t{1}`t{2}`t{3}`t{4}`t{5}" -f $Branch, $Number, $Code, $RowOrigin, ($RowTitle -replace "[`t`r`n]", ' '), $ParentNumber
    $kept = @()
    if (Test-Path $manifest) {
        $kept = @(Get-Content $manifest | Where-Object { $p = $_ -split "`t"; -not ($p.Count -ge 2 -and $p[0] -eq $Branch -and $p[1] -eq "$Number") })
    }
    Set-Content -Path $manifest -Value (@($kept) + $row)
    return $manifest
}

# --- Search mode -----------------------------------------------------------------------------

if ($PSCmdlet.ParameterSetName -eq 'Search') {
    $cands = @(Find-SimilarIssues -TitleText $Search -Top 10)
    if ($cands.Count -eq 0) {
        Write-Host "No similar existing work items found for: $Search" -ForegroundColor Yellow
        return
    }
    Write-Host "Existing work items that may already cover: $Search" -ForegroundColor Cyan
    foreach ($c in $cands) {
        Write-Host ("  #{0} [{1}] shared={2} jaccard={3}  {4}" -f $c.Number, $c.State, $c.Shared, $c.Jaccard, $c.Title)
    }
    Write-Host "Reuse one of these instead of creating a duplicate where it fits." -ForegroundColor DarkGray
    return
}

# --- Closes-block mode -----------------------------------------------------------------------

if ($PSCmdlet.ParameterSetName -eq 'Closes') {
    $branch = Get-BranchName
    $rows = @(Read-ManifestRows -Branch $branch)
    if ($rows.Count -eq 0) {
        Write-Host "# No work items recorded for branch '$branch'." -ForegroundColor Yellow
        return
    }
    foreach ($r in @($rows | Where-Object { $_.unlinked -or $_.incomplete })) {
        $to = if ($r.parent) { "#$($r.parent)" } else { 'its parent (not recorded)' }
        if ($r.incomplete) { Write-Warning "INCOMPLETE: #$($r.number) [$($r.wbs)] stopped at '$($r.incomplete)'$(if ($r.unlinked) { " and is not linked under $to" }). Re-run the same filing (same title, same parent) to resume it, before merging." }
        else { Write-Warning "UNLINKED: #$($r.number) [$($r.wbs)] was created but never linked under $to. Link it before merging: the filing exited 1 when the link failed." }
    }
    $planned    = @($rows | Where-Object { $_.origin -notlike 'Discovered*' -and $_.origin -ne 'Defect' } | ForEach-Object { $_.number })
    $discovered = @($rows | Where-Object { $_.origin -like 'Discovered*' } | ForEach-Object { $_.number })
    $defects    = @($rows | Where-Object { $_.origin -eq 'Defect' } | ForEach-Object { $_.number })
    # Defects stay out of the creep tally: a defect in delivered work is rework, not scope the branch grew.
    $scopeTotal = $planned.Count + $discovered.Count
    Write-Output "## Work items resolved by this PR"
    foreach ($n in $planned) { Write-Output "Closes #$n" }
    if ($discovered.Count -gt 0) {
        Write-Output ""
        Write-Output "### Discovered (out-of-scope) work"
        foreach ($n in $discovered) { Write-Output "Closes #$n" }
    }
    if ($defects.Count -gt 0) {
        Write-Output ""
        Write-Output "### Defects in delivered work"
        foreach ($n in $defects) { Write-Output "Closes #$n" }
    }
    if ($discovered.Count -gt 0 -or $defects.Count -gt 0) {
        Write-Output ""
        $note = "creep: $($discovered.Count) of $scopeTotal scope items were discovered out-of-scope"
        if ($defects.Count -gt 0) {
            $word = if ($defects.Count -eq 1) { 'defect' } else { 'defects' }
            $note += "; $($defects.Count) $word in delivered work, excluded from the creep tally"
        }
        Write-Output "<!-- $note -->"
    }
    return
}

# --- Audit mode (read-only) ------------------------------------------------------------------

if ($PSCmdlet.ParameterSetName -eq 'Audit') {
    $fieldMap = Get-FieldMap
    $hasCategory = $fieldMap.ContainsKey('Category')
    $hasTier     = $fieldMap.ContainsKey('Tier')
    $auditQuery = @'
query($endCursor: String) {
  repository(owner: "assimalign", name: "cohesion") {
    issues(first: 100, after: $endCursor) {
      pageInfo { hasNextPage endCursor }
      nodes { number title state issueType { name } parent { number } subIssuesSummary { total }
        projectItems(first: 10) { nodes { project { number }
          kind: fieldValueByName(name: "Kind") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
          area: fieldValueByName(name: "Area") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
          category: fieldValueByName(name: "Category") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
          tier: fieldValueByName(name: "Tier") { ... on ProjectV2ItemFieldSingleSelectValue { name } } } } }
    }
  }
}
'@
    $out = Invoke-GhRetry -GraphQL -GhArgs @('api', 'graphql', '--paginate', '-f', "query=$auditQuery", '--jq', '.data.repository.issues.nodes[]')
    $all = @{}
    foreach ($line in @($out | Where-Object { $_.Trim() })) {
        $raw = $line | ConvertFrom-Json
        $item = $null
        foreach ($pi in @(Get-Prop (Get-Prop $raw 'projectItems') 'nodes')) {
            if ($pi -and [int](Get-Prop (Get-Prop $pi 'project') 'number') -eq $ProjectNum) { $item = $pi; break }
        }
        $sel = { param($Name) $v = Get-Prop $item $Name; if ($v) { [string](Get-Prop $v 'name') } else { $null } }
        $title = [string]$raw.title
        $all[[int]$raw.number] = [pscustomobject]@{
            number = [int]$raw.number; title = $title; wbs = Get-TitleWbs $title; state = [string]$raw.state
            issueType = [string](Get-Prop (Get-Prop $raw 'issueType') 'name')
            parent = $(if (Get-Prop $raw 'parent') { [int]$raw.parent.number } else { $null })
            subTotal = [int](Get-Prop (Get-Prop $raw 'subIssuesSummary') 'total')
            kindField = & $sel 'kind'; area = & $sel 'area'; category = & $sel 'category'; tier = & $sel 'tier'
        }
    }
    if (-not $all.ContainsKey($Root)) { throw "Audit root #$Root was not found." }
    $children = @{}
    foreach ($n in $all.Values) { if ($null -ne $n.parent) { if (-not $children.ContainsKey($n.parent)) { $children[$n.parent] = [System.Collections.Generic.List[int]]::new() }; $children[$n.parent].Add($n.number) } }
    $subtree = [System.Collections.Generic.List[int]]::new(); $queue = [System.Collections.Generic.Queue[int]]::new(); $queue.Enqueue($Root)
    while ($queue.Count) { $x = $queue.Dequeue(); $subtree.Add($x); if ($children.ContainsKey($x)) { foreach ($c in $children[$x]) { $queue.Enqueue($c) } } }
    $isEpic = { param($n) $n.issueType -eq 'Epic' -or (-not $n.issueType -and $n.kindField -eq 'Area Epic') }
    $findings = 0
    $show = { param([string] $Head, $List)
        $l = @($List | Where-Object { $null -ne $_ })
        Write-Host ("{0}: {1}" -f $Head, $l.Count) -ForegroundColor $(if ($l.Count) { 'Yellow' } else { 'Green' })
        foreach ($s in ($l | Select-Object -First 25)) { Write-Host "  $s" }
        if ($l.Count -gt 25) { Write-Host "  ... and $($l.Count - 25) more" }
    }
    Write-Host "Audit of #$Root ($($all[$Root].title)): $($subtree.Count) issues in the tree, read-only." -ForegroundColor Cyan

    # 1. Categorised epics and their direct open work items.
    $catEpics = @($subtree | ForEach-Object { $all[$_] } | Where-Object { (& $isEpic $_) -and $_.wbs } | Where-Object {
            $e = $_; @(@($children[$e.number]) | Where-Object { $_ -and $all[$_].wbs -and $all[$_].wbs -cmatch ('^' + [regex]::Escape($e.wbs) + '\.C\d{2}$') }).Count -gt 0 })
    Write-Host "Categorised epics: $($catEpics.Count)$(if ($catEpics.Count) { ' (' + (($catEpics | ForEach-Object { "#$($_.number)" }) -join ', ') + ')' })"
    $direct = @(foreach ($e in $catEpics) {
        foreach ($c in @($children[$e.number] | Where-Object { $_ })) {
            $n = $all[$c]
            if ($n.state -eq 'OPEN' -and -not ($n.wbs -and $n.wbs -cmatch '\.C\d{2}$')) { "#$($n.number) under #$($e.number): $($n.title)" }
        }
    })
    & $show '1. Open work items directly under a categorised epic' $direct; $findings += @($direct).Count

    # 2-3. Fields, inside categorised epics only (elsewhere nothing requires them yet).
    $inCat = [System.Collections.Generic.List[object]]::new()
    foreach ($e in $catEpics) {
        $q2 = [System.Collections.Generic.Queue[int]]::new(); foreach ($c in @($children[$e.number])) { if ($c) { $q2.Enqueue($c) } }
        while ($q2.Count) { $x = $q2.Dequeue(); $inCat.Add($all[$x]); if ($children.ContainsKey($x)) { foreach ($c in $children[$x]) { $q2.Enqueue($c) } } }
    }
    if ($hasCategory) {
        $missing = @($inCat | Where-Object { -not $_.category -or -not $_.area } | ForEach-Object { "#$($_.number) Category=$(if ($_.category) { $_.category } else { '-' }) Area=$(if ($_.area) { $_.area } else { '-' })" })
        & $show '2. Items under categorised epics missing Category or Area' $missing; $findings += $missing.Count
    }
    else { Write-Host '2. Items missing Category or Area: skipped, the project has no Category field yet.' -ForegroundColor DarkGray }
    if ($hasCategory -and $hasTier) {
        $lsNames = @{}
        foreach ($n in $all.Values) { if ($n.wbs -and $LanguageSurfaceCategories -ccontains $n.wbs) { $lsNames[($n.title -replace '^\[[^\]]+\]\s*', '')] = $n.wbs } }
        $untiered = @($inCat | Where-Object { $_.state -eq 'OPEN' -and $_.issueType -ne 'Bug' -and -not ($_.wbs -and $_.wbs -cmatch '\.C\d{2}$') -and $_.category -and $lsNames.ContainsKey($_.category) -and -not $_.tier } | ForEach-Object { "#$($_.number) ($($lsNames[$_.category])): $($_.title)" })
        & $show '3. Open language-surface items without a Tier' $untiered; $findings += $untiered.Count
    }
    else { Write-Host '3. Language-surface items without a Tier: skipped, the project has no Category or Tier field yet.' -ForegroundColor DarkGray }

    # 4. Parents near the sub-issue cap.
    $full = @($subtree | ForEach-Object { $all[$_] } | Where-Object { $_.subTotal -ge 95 } | ForEach-Object { "#$($_.number) $($_.subTotal)/$SubIssueCap $($_.title)" })
    & $show '4. Parents at 95 or more sub-issues' $full; $findings += $full.Count

    # 5. WBS codes carried by more than one issue, where one of them is in the tree.
    $byCode = @{}
    foreach ($n in $all.Values) { if ($n.wbs) { if (-not $byCode.ContainsKey($n.wbs)) { $byCode[$n.wbs] = [System.Collections.Generic.List[int]]::new() }; $byCode[$n.wbs].Add($n.number) } }
    $inTree = [System.Collections.Generic.HashSet[int]]::new([int[]]@($subtree))
    $dups = @($byCode.GetEnumerator() | Where-Object { $_.Value.Count -gt 1 -and @($_.Value | Where-Object { $inTree.Contains($_) }).Count } | Sort-Object Key | ForEach-Object { "[$($_.Key)] " + (($_.Value | Sort-Object | ForEach-Object { "#$_" }) -join ', ') })
    & $show '5. WBS codes on more than one issue' $dups; $findings += $dups.Count

    # 6-7. Unfinished filings in this worktree's manifest.
    $manifestRows = @(Read-ManifestRows)
    $unl = @($manifestRows | Where-Object { $_.unlinked } | ForEach-Object { "#$($_.number) [$($_.wbs)] on $($_.branch)$(if ($_.parent) { ", meant for #$($_.parent)" })" })
    & $show '6. UNLINKED manifest rows in this worktree' $unl; $findings += $unl.Count
    $inc = @($manifestRows | Where-Object { $_.incomplete } | ForEach-Object { "#$($_.number) [$($_.wbs)] on $($_.branch), stopped at '$($_.incomplete)'" })
    & $show '7. INCOMPLETE manifest rows in this worktree (re-run the filing to resume)' $inc; $findings += $inc.Count

    if ($findings -gt 0) { Write-Host "Audit: $findings finding(s)." -ForegroundColor Yellow; exit 1 }
    Write-Host "Audit: clean." -ForegroundColor Green
    return
}

# --- Create mode -----------------------------------------------------------------------------

# A defect is one filing however it is asked for: -As bug or -Origin Defect. Either makes it a bug, so every bug
# guard applies (an -Origin Defect filing must not slip past them as a plain task), and a bug's Origin is Defect.
$isBug = ($As -eq 'bug' -or $Origin -eq 'Defect')
if ($isBug) {
    if ($As -ne 'bug') { Write-Host "Note: -Origin Defect files a defect: the bug path and all its guards apply, as with -As bug." -ForegroundColor Yellow }
    elseif ($Origin -and $Origin -ne 'Defect') { Write-Host "Note: -As bug files a defect, so its Origin is Defect, not $Origin." -ForegroundColor Yellow }
    $Origin = 'Defect'
    if (-not $Parent) {
        throw "A defect (-As bug or -Origin Defect) requires -Parent <issue#>: it attaches to the item whose delivered behaviour is wrong (bug policy B1). There is no branch inference for a defect."
    }
    if ($Parent -notmatch '^\d+$') {
        throw "A defect (-As bug or -Origin Defect) requires -Parent as an ISSUE NUMBER, not [$Parent]: WBS codes repeat (7 codes on 14 issues when measured), so a code can attach a defect to the wrong delivered item."
    }
}
$tierData = Get-TierData
if ($Tier) {
    $known = if ($tierData) { @($tierData.options.Keys) } else { $InheritableTiers + @('Done-pre-plan', 'Out-of-scope') }
    if ($known -cnotcontains $Tier) { throw "-Tier '$Tier' is not a Tier option. Use one of: $($known -join ', ')." }
}
if ($Program) {
    $knownPrograms = Get-ProgramOptions
    if ($knownPrograms -and $knownPrograms -cnotcontains $Program) { throw "-Program '$Program' is not a program. Use one of: $($knownPrograms -join ', ')." }
}

# 1. Resolve the parent: explicit, or from the item the branch names (its child, or its sibling).
$branchWbs  = Get-BranchWbs
$branchItem = $null
$inferred   = $false
$parentHow  = $null
if ($Parent) {
    if ($Parent -match '^\d+$') { $parentNumber = [int]$Parent; $parentHow = 'from -Parent' }
    else {
        if ($Parent -cnotmatch ('^' + $WbsRx + '$')) { throw "-Parent '$Parent' is neither an issue number nor a WBS code." }
        $parentNumber = [int](Resolve-UniqueWbs -Code $Parent -What '-Parent').number
        $parentHow = "from -Parent [$Parent]"
    }
    if ($As -eq 'feature') { Write-Host "Note: -As feature has no effect with an explicit -Parent; the type follows the parent's kind." -ForegroundColor DarkGray }
}
else {
    $inferred = $true
    if (-not $branchWbs) { throw "Could not infer a WBS code from the current branch. Pass -Parent <issue#|WBS> explicitly." }
    $branchItem = Resolve-UniqueWbs -Code $branchWbs -What 'Branch WBS'
    if ($As -eq 'feature') {
        $branchChain = Get-IssueChain -Number ([int]$branchItem.number)
        if (@($branchChain.nodes).Count -lt 2) { throw "#$($branchItem.number) [$branchWbs] has no parent in the sub-issue tree, so it has no siblings. Pass -Parent explicitly." }
        $parentNumber = [int]$branchChain.nodes[1].number
        $parentHow = "the parent of #$($branchItem.number) [$branchWbs], from its sub-issue link"
    }
    else {
        $parentNumber = [int]$branchItem.number
        $parentHow = "the item branch [$branchWbs] names"
    }
}

# 2. One read: the parent and its ancestors.
$chainInfo   = Get-IssueChain -Number $parentNumber
$chain       = @($chainInfo.nodes)
$parentNode  = $chain[0]
$ancestors   = $chain.Count - 1
if (-not $parentNode.wbs) { throw "Parent #$parentNumber title has no [WBS] prefix: '$($parentNode.title)'." }
$parentWbs   = $parentNode.wbs
Write-Host "Parent: #$($parentNode.number) $($parentNode.title)  ($parentHow)" -ForegroundColor Green

# 3. Refusals. A program gate first: it is the most specific message (E18).
foreach ($g in (Get-ProgramGates)) {
    if ($g.issue -eq $parentNode.number) {
        throw @"
#$($g.issue) is the $($g.program) program gate ($($g.label)). A gate takes no sub-issues, bugs included.
To put work in the $($g.program) program:
  1. File it by the code it changes: -Parent <the owning category or item>.
  2. Pass -Program $($g.program).
  3. Link the gate to it:  gh api -X POST repos/$Repo/issues/$($g.issue)/dependencies/blocked_by -F issue_id=<the new issue's REST id>
A task under an existing $($g.program) feature needs no link: it inherits Program and is a descendant of a linked feature.
A gate failure after a member closed is a bug under that member (bug policy B1), plus the same link.
"@
    }
}
$parentKind = Get-IssueKind $parentNode
if ($parentKind -eq 'Program') { throw "#$($parentNode.number) [$parentWbs] is a program root. Area epics and programs are created by hand; file under an area epic, a category or an item." }
$isDataPlatform = [bool]@($chain | Where-Object { $_.number -eq $DataPlatformRoot }).Count
$closedParent = ($parentNode.state -eq 'CLOSED')

if ($isBug) {
    if ($parentKind -in @('AreaEpic', 'Category')) {
        throw "#$($parentNode.number) [$parentWbs] is $(if ($parentKind -eq 'Category') { 'a category' } else { 'an epic' }). A defect attaches to the delivered item whose behaviour is wrong, never to a container (bug policy B3). If nothing delivered this behaviour, file an ordinary item under the owning category (B4)."
    }
    if (-not $closedParent -and -not $AllowOpenParent) {
        throw @"
Parent #$($parentNode.number) is OPEN ($($parentNode.title)).
Bug policy B2: a defect found against work that has not shipped is in-scope rework on that item, not a bug
child of it. Add it to #$($parentNode.number)'s acceptance criteria, or file it as a task:  -Parent $($parentNode.number)
Pass -AllowOpenParent only for a separable defect in behaviour #$($parentNode.number) already delivered on a branch.
"@
    }
    if ($closedParent -and $parentNode.stateReason -in @('NOT_PLANNED', 'DUPLICATE')) {
        throw "Parent #$($parentNode.number) was closed as $($parentNode.stateReason): it delivered nothing, so nothing regressed against it (bug policy B2). File an ordinary item under the owning category instead."
    }
}
elseif ($closedParent -and -not $AllowClosedParent) {
    throw @"
Parent #$($parentNode.number) is CLOSED ($($parentNode.title)). New scope under delivered work disappears from every open view.
  - A defect in what it delivered:  -As bug -Parent $($parentNode.number)
  - New scope: file it under the category or item that owns the behaviour (bug policy B4).
Pass -AllowClosedParent only when the closed item really is the right home.
"@
}

$childLevel = $ancestors + 2
if ($chainInfo.overflow -or $childLevel -gt $MaxNestingLevel) {
    $path = ($chain | ForEach-Object { "#$($_.number)" }) -join ' <- '
    $lvl = if ($chainInfo.overflow) { 'more than 9' } else { "$childLevel" }
    throw "A child of #$($parentNode.number) would sit at nesting level $lvl (ancestry: $path$(if ($chainInfo.overflow) { ' <- ...' })); GitHub allows $MaxNestingLevel. Attach it to the nearest ancestor with room and name the leaf in the body (bug policy B3)."
}
Assert-SubIssueRoom -ParentNumber $parentNode.number -Known $parentNode.subTotal | Out-Null

# The nearest area epic and its categories (one REST read); "categorised" means it has a Cxx child.
$epicNode = $null
foreach ($n in $chain) { if ((Get-IssueKind $n -NoThrow) -eq 'AreaEpic') { $epicNode = $n; break } }
$epicCategories = @()
if ($epicNode -and $epicNode.wbs) {
    $epicCategories = @(Get-SubIssues -Number $epicNode.number | Where-Object { $_.title -cmatch ('^\[' + [regex]::Escape($epicNode.wbs) + '\.C\d{2}\]') })
}
$categorised = ($epicCategories.Count -gt 0)
if ($parentKind -eq 'AreaEpic' -and $categorised -and -not $DirectUnderEpic) {
    $lines = $epicCategories | Sort-Object title | ForEach-Object {
        $code = Get-TitleWbs $_.title
        "  #$($_.number) [$code] $($_.title -replace '^\[[^\]]+\]\s*', '') (open children $([Math]::Max(0, [int]$_.total - [int]$_.completed)))"
    }
    $msg = @("#$($parentNode.number) [$parentWbs] has categories: a work item goes under the category that owns the code it changes, not directly under the epic.") +
        @($lines) +
        @("Re-run with -Parent <category#> (it supplies the Category), or, for work that belongs to the epic itself, with -DirectUnderEpic -Category <code>.")
    throw ($msg -join "`n")
}
if ($DirectUnderEpic -and -not ($parentKind -eq 'AreaEpic' -and $categorised)) {
    Write-Host "Note: -DirectUnderEpic has no effect: the parent is not an epic with categories." -ForegroundColor DarkGray
}

# 4. Type, Kind and Origin from the parent's kind. The issue types follow Initiative -> Epic -> Feature -> Story
# (owner, 2026-10-11), and a child of a Story, Task or Bug is a Task: the board's Story children are all typed
# Task, the realignment executor types depth-6 items Task, and Data Platform's sixth-segment items sit as Tasks.
if ($isBug) { $issueType = 'Bug'; $kindOpt = 'Bug' }
elseif ($parentKind -in @('AreaEpic', 'Category')) { $issueType = 'Feature'; $kindOpt = 'Feature' }
elseif ($parentKind -eq 'Feature') { $issueType = 'Story'; $kindOpt = 'Task' }
else { $issueType = 'Task'; $kindOpt = 'Task' }
if (-not $Origin) {
    if ($inferred) { $Origin = if ($As -eq 'feature') { 'DiscoveredFeature' } else { 'DiscoveredTask' } }
    else { $Origin = 'Planned' }
}

# An earlier run of this same filing (same branch, parent and title) that created the issue but stopped before
# finishing is resumed, not created a second time.
$resume = $null; $alreadyLinked = $false
$cleanTitle = ($Title -replace "[`t`r`n]", ' ')
$pending = @(Read-ManifestRows -Branch (Get-BranchName) | Where-Object { ($_.unlinked -or $_.incomplete) -and $_.parent -eq $parentNode.number -and $_.title -ceq $cleanTitle })
if ($pending.Count) {
    $row = $pending[-1]
    $prior = $null
    try { $prior = @((Get-IssueChain -Number $row.number).nodes) } catch { Write-Warning "The manifest records an unfinished filing #$($row.number), but it cannot be read ($_); filing anew." }
    if ($prior -and $prior[0].state -ne 'OPEN') { Write-Warning "The manifest records an unfinished filing #$($row.number), but it is closed; filing anew."; $prior = $null }
    if ($prior) {
        if ($prior.Count -ge 2 -and $prior[1].number -ne $parentNode.number) {
            throw "#$($row.number), an unfinished earlier filing of '$Title', now sits under #$($prior[1].number), not #$($parentNode.number). Finish it by hand, or delete its row from the branch manifest."
        }
        $resume = $prior[0]; $alreadyLinked = ($prior.Count -ge 2)
        $stoppedAt = if ($row.incomplete) { $row.incomplete } else { 'link' }
        Write-Host "Resuming #$($resume.number): already created by an earlier run that stopped at '$stoppedAt'; it is not created again." -ForegroundColor Yellow
        if ($Wbs -and $Wbs -cne $resume.wbs) { Write-Host "Note: -Wbs $Wbs is ignored: #$($resume.number) already carries [$($resume.wbs)]." -ForegroundColor DarkGray }
    }
}

# 5. WBS code.
if ($resume) { $newWbs = if ($resume.wbs) { $resume.wbs } else { $row.wbs } }
elseif ($Wbs) {
    if ($Wbs -cnotmatch ('^' + [regex]::Escape($parentWbs) + '\.\d{2,}$')) { throw "-Wbs [$Wbs] is not a direct child of the parent's code [$parentWbs]." }
    $existingWbs = @(Get-IssuesByWbs $Wbs)
    if ($existingWbs.Count -gt 0) { throw "-Wbs [$Wbs] is already used by #$($existingWbs[0].number) '$($existingWbs[0].title)'." }
    $newWbs = $Wbs
}
else { $newWbs = Get-NextChildWbs -ParentWbs $parentWbs }
$fullTitle = "[$newWbs] $Title"
Write-Host "$(if ($resume) { "Resumed $($issueType.ToLowerInvariant())" } else { "New $($issueType.ToLowerInvariant())" }) ($Origin): $fullTitle" -ForegroundColor Green

# 6. Board fields, each with its source.
$fieldPlan = [System.Collections.Generic.List[object]]::new()
function Add-FieldPlan { param([string] $Field, [string] $Value, [string] $Source, [bool] $Hard)
    if ($Value) { $fieldPlan.Add([pscustomobject]@{ field = $Field; value = $Value; source = $Source; hard = $Hard }) } }

# Category: -Category, else the parent is a category; required in a categorised area.
$cat = $null; $catSource = $null
if ($Category) {
    if ($Category -match '^\d+$') { $catNode = (Get-IssueChain -Number ([int]$Category)).nodes[0] }
    else {
        if ($Category -cnotmatch '^L\d{2}(?:\.\d{2,})*\.C\d{2}$') { throw "-Category '$Category' is neither a category code (L03.02.02.C04) nor an issue number." }
        $hit = @(Get-IssuesByWbs $Category)
        if ($hit.Count -eq 0) { throw "No category issue carries [$Category] yet. Categories are created by the realignment (W1B, W2) or the manual recipe in reference/project-schema.md; drop -Category until it exists." }
        if ($hit.Count -gt 1) { throw "[$Category] names $($hit.Count) issues ($((($hit | ForEach-Object { "#$($_.number)" }) -join ', '))). Pass -Category <issue number>." }
        $catNode = (Get-IssueChain -Number ([int]$hit[0].number)).nodes[0]
    }
    if ((Get-IssueKind $catNode) -ne 'Category') { throw "-Category #$($catNode.number) is not a category: its title has no [..Cxx] code ('$($catNode.title)')." }
    $cat = $catNode; $catSource = '-Category'
}
elseif ($parentKind -eq 'Category') { $cat = $parentNode; $catSource = "parent is category #$($parentNode.number)" }
$catCode = if ($cat) { $cat.wbs } else { $null }
$catName = if ($cat) { ($cat.title -replace '^\[[^\]]+\]\s*', '').Trim() } else { $null }
if ($cat -and $epicNode -and $epicNode.wbs -and -not $catCode.StartsWith($epicNode.wbs + '.')) {
    Write-Warning "Category [$catCode] belongs to another epic than the one this item sits under (#$($epicNode.number) [$($epicNode.wbs)]). File it under a category of its own epic, or under [$catCode] itself: tree and field then agree. While the board realignment runs, its executor halts on a cross-epic Category."
}
if (-not $cat -and $categorised) {
    $hint = $null
    foreach ($n in $chain) { if ((Get-IssueKind $n -NoThrow) -eq 'Category') { $hint = "-Category $($n.wbs) (#$($n.number), an ancestor)"; break } }
    if (-not $hint) {
        foreach ($n in $chain) {
            if ($n.category) {
                $m = @($epicCategories | Where-Object { ($_.title -replace '^\[[^\]]+\]\s*', '').Trim() -ceq $n.category })
                $hint = if ($m.Count) { "-Category $(Get-TitleWbs $m[0].title) ($($n.category), the Category of #$($n.number))" } else { "the Category of #$($n.number): $($n.category)" }
                break
            }
        }
    }
    throw "Epic #$($epicNode.number) [$($epicNode.wbs)] has categories, so this filing needs -Category <code|issue#>: the category of the code the work changes (taxonomy rule R2), not necessarily the parent's.$(if ($hint) { " Suggested: $hint." })"
}
if ($cat) { Add-FieldPlan 'Category' $catName $catSource $true }

# Tier: -Tier, else the nearest tiered ancestor; required in a language-surface category (never for a bug).
$tierVal = $null; $tierSource = $null
if ($Tier) { $tierVal = $Tier; $tierSource = '-Tier' }
else { foreach ($n in $chain) { if ($n.tier -and $InheritableTiers -ccontains $n.tier) { $tierVal = $n.tier; $tierSource = "from #$($n.number)"; break } } }
$langSurface = [bool]($catCode -and $LanguageSurfaceCategories -ccontains $catCode)
if (-not $tierVal -and $langSurface -and -not $isBug) {
    throw "[$catCode] $catName is a language-surface category: its work carries a Tier (realignment decision 19). Pass -Tier T1|T2|T3|Fix-first|Prerequisite, or file under the tiered feature it belongs to."
}
if ($tierVal) { Add-FieldPlan 'Tier' $tierVal $tierSource $true }

# Program: -Program, else the nearest ancestor that has one.
$programVal = $null; $programSource = $null
if ($Program) { $programVal = $Program; $programSource = '-Program' }
else { foreach ($n in $chain) { if ($n.program) { $programVal = $n.program; $programSource = "from #$($n.number)"; break } } }
if ($programVal) { Add-FieldPlan 'Program' $programVal $programSource $true }

# Area: the category's own Area, else the "Foundation - X" epic title.
$areaVal = $null; $areaSource = $null
if ($cat) {
    if (-not $cat.area) { throw "Category #$($cat.number) [$catCode] has no Area value, so this item's Area cannot be copied from it. Set Area on #$($cat.number) first." }
    $areaVal = $cat.area; $areaSource = "category #$($cat.number)"
}
elseif ($epicNode) {
    $am = [regex]::Match($epicNode.title, 'Foundation - (.+)$')
    if ($am.Success) { $areaVal = $am.Groups[1].Value.Trim(); $areaSource = "epic #$($epicNode.number) title" }
}
if ($areaVal) { Add-FieldPlan 'Area' $areaVal $areaSource $isDataPlatform }

# Wave / Priority: as passed. In Data Platform, untiered work needs one of them, or the roadmap leaves it Unplaced.
if ($isDataPlatform -and -not $tierVal -and -not $Wave -and -not $Priority) {
    $hintW = $null; $hintP = $null
    foreach ($n in $chain) { if (-not $hintW -and $n.wave) { $hintW = "Wave $($n.wave) on #$($n.number)" }; if (-not $hintP -and $n.priority) { $hintP = "Priority $($n.priority) on #$($n.number)" } }
    $hint = @($hintW, $hintP) | Where-Object { $_ }
    throw "This Data Platform item has no Tier of its own and no tiered ancestor, so it needs -Wave or -Priority (realignment decision 6): the roadmap places untiered work by its Wave window, else its Priority window, and leaves it Unplaced without either.$(if ($hint) { ' Nearest ancestor values: ' + ($hint -join ', ') + '.' })"
}

# Family (E19): the plan-family line for T1-T3 work; T2 and T3 must name one ("none" for out-of-plan work).
$langCode = if ($catCode) { $catCode } elseif ($epicNode) { $epicNode.wbs } else { $parentWbs }
$planTier = $null
if ($tierVal) { $planTier = if ($tierData -and $tierData.options.Contains($tierVal)) { $tierData.options[$tierVal] } else { $tierVal } }
$planLine = $null
if ($Family) {
    if ($Family -ceq 'none') {
        if ($planTier -eq 'T2') { Write-Host "Note: -Family none: a T2 item without a plan family raises the roadmap warning t2-familyless and sits at the front of the T2 band." -ForegroundColor Yellow }
        elseif ($planTier -eq 'T3') { Write-Host "Note: -Family none: this T3 item takes no plan slot." -ForegroundColor Yellow }
    }
    else {
        if (-not $tierData) { throw "-Family needs the roadmap skill's tiers.json ($RoadmapDataDir), which is missing." }
        $fam = @($tierData.families | Where-Object { $_.id -ceq $Family -or @(Get-Prop $_ 'bodySlugs') -ccontains $Family }) | Select-Object -First 1
        $lang = Get-PlanLanguage $langCode
        $listFor = { param($T) $l = @($tierData.families | Where-Object { @($_.tiers) -ccontains $T -and (-not $lang -or $_.language -eq $lang) } | ForEach-Object { $_.id }); if ($l.Count) { $l -join ', ' } else { '(none)' } }
        if (-not $fam) { throw "-Family '$Family' is not a plan family in tiers.json.$(if ($planTier) { " $planTier families$(if ($lang) { " for $lang" }): $(& $listFor $planTier)." })" }
        # A language family belongs to its own language's categories. Cross-cutting families may sit anywhere
        # (#1068 under SQL C01), and Core may hold a language family's kernel work (#1012, SQL, in Core C04).
        $languages = @('SQL', 'OQL', 'GQL')
        if ($lang -in $languages -and [string]$fam.language -in $languages -and [string]$fam.language -ne $lang) {
            throw "Family '$Family' is $($fam.language) work, but this item's category [$langCode] is $lang work: a language family must match its category's language.$(if ($planTier) { " $planTier families for ${lang}: $(& $listFor $planTier)." })"
        }
        if (-not $planTier) { throw "-Family '$Family' needs a tier: pass -Tier, or file under tiered work." }
        if ($planTier -notin @('T1', 'T2', 'T3')) { throw "-Family applies to T1, T2 and T3 work; this item's tier is $planTier." }
        if (@($fam.tiers) -cnotcontains $planTier) { throw "Family '$Family' has no $planTier row (its tiers: $(@($fam.tiers) -join ', ')). $planTier families$(if ($lang) { " for $lang" }): $(& $listFor $planTier)." }
        $planLine = "- Tier $($planTier.Substring(1)) family ``$Family`` of the owner-approved query-language plan (2026-09-30)."
    }
}
elseif ($planTier -in @('T2', 'T3') -and -not $isBug) {
    $lang = Get-PlanLanguage $langCode
    $l = if ($tierData) { @($tierData.families | Where-Object { @($_.tiers) -ccontains $planTier -and (-not $lang -or $_.language -eq $lang) } | ForEach-Object { $_.id }) -join ', ' } else { '' }
    throw "A $planTier item must name its plan family so the roadmap can put it in its slot: -Family <slug>, or -Family none for out-of-plan work.$(if ($l) { " $planTier families$(if ($lang) { " for $lang" }): $l." })"
}

# 7. Reuse before creating: look for an existing item that may already cover this. A resumed filing skips it:
# the issue that would match is its own.
if (-not $resume) {
    $similar = @(Find-SimilarIssues -TitleText $Title)
    if ($isBug) {
        # The parent shares every significant word with its defect by construction; its other children are the
        # real duplicate risk, so they rank first.
        $similar = @($similar | Where-Object { $_.Number -ne $parentNode.number })
        $siblingRx = '^\[' + [regex]::Escape($parentWbs) + '\.\d{2,}\]'
        $siblingNums = @((Get-AllIssues) | Where-Object { $_.title -cmatch $siblingRx } | ForEach-Object { $_.number })
        $similar = @($similar | Sort-Object -Property @{Expression={ [int]($siblingNums -contains $_.Number) }; Descending=$true}, @{Expression='Shared'; Descending=$true})
    }
    $strong = @($similar | Where-Object { $_.State -eq 'OPEN' -and ($_.Jaccard -ge 0.5 -or $_.Shared -ge 3) })
    if ($strong.Count -gt 0 -and -not $Force) {
        Write-Warning "A likely-duplicate existing item was found for '$Title':"
        foreach ($c in $strong) { Write-Host "  #$($c.Number) [$($c.State)] $($c.Title)  (shared=$($c.Shared), jaccard=$($c.Jaccard); terms: $($c.Terms))" -ForegroundColor Yellow }
        Write-Host "Reuse one of the above (add work to it), or re-run with -Force to create a new item anyway." -ForegroundColor Yellow
        return
    }
    elseif ($similar.Count -gt 0) {
        Write-Host "Similar existing items (not blocking; confirm this is genuinely new):" -ForegroundColor DarkGray
        foreach ($c in ($similar | Select-Object -First 3)) { Write-Host "  #$($c.Number) [$($c.State)] $($c.Title)" -ForegroundColor DarkGray }
    }
}

# 8. Body: template or file, the plan-family line, and the provenance stamp (only for a new issue).
$body = $null; $labels = @()
if (-not $resume) {
    $parentRef = "[$parentWbs] (#$($parentNode.number))"
    if ($BodyFile) {
        if (-not (Test-Path $BodyFile)) { throw "BodyFile not found: $BodyFile" }
        $body = Get-Content $BodyFile -Raw
        if ($planLine) {
            $fm = [regex]::Match($body, 'Tier ([123]) family `([a-z0-9-]+)`')
            if ($fm.Success) {
                if ($fm.Groups[2].Value -cne $Family -or "T$($fm.Groups[1].Value)" -ne $planTier) { throw "The body already names Tier $($fm.Groups[1].Value) family '$($fm.Groups[2].Value)', which disagrees with -Family $Family at $planTier." }
            }
            else { $body = $body.TrimEnd() + "`n`n" + $planLine + "`n" }
        }
    }
    else { $body = New-BodyTemplate -Sum $Summary -Acc $Acceptance -Std $Standards -Bug:$isBug -ParentRef $parentRef -PlanLine $planLine }
    if ($Origin -eq 'Defect') {
        $when = if ($closedParent -and $parentNode.closedAt) { 'closed ' + ([datetime]$parentNode.closedAt).ToUniversalTime().ToString('yyyy-MM-dd') } else { 'which is still open' }
        $body = "> Defect in [$parentWbs] (#$($parentNode.number)), $when.`n`n" + $body
    }
    elseif ($Origin -like 'Discovered*' -and $branchWbs) {
        $stampItem = $branchItem
        if (-not $stampItem) { $h = @(Get-IssuesByWbs $branchWbs); if ($h.Count -eq 1) { $stampItem = $h[0] } }
        $featRef = if ($stampItem) { "[$branchWbs] (#$($stampItem.number))" } else { "[$branchWbs]" }
        $body = "> Discovered while implementing $featRef.`n`n" + $body
    }
    # Discovered work is labeled scope-creep and a defect `bug`, so both are countable from the issues CLI.
    $labels = @($Label | Where-Object { $_ })
    if ($Origin -like 'Discovered*' -and $labels -notcontains 'scope-creep') { $labels += 'scope-creep' }
    if ($Origin -eq 'Defect' -and $labels -notcontains 'bug') { $labels += 'bug' }
}

# 9. The full field plan, then the preflight: every option about to be written must exist.
$fieldPlan.Insert(0, [pscustomobject]@{ field = 'Status'; value = $Status; source = '-Status'; hard = $false })
$kindPhrase = switch ($parentKind) { 'AreaEpic' { 'an area epic' } 'Category' { 'a category' } default { "a $parentKind" } }
$fieldPlan.Insert(1, [pscustomobject]@{ field = 'Kind'; value = $kindOpt; source = $(if ($isBug) { "defect in #$($parentNode.number)" } else { "child of $kindPhrase" }); hard = $true })
$fieldPlan.Insert(2, [pscustomobject]@{ field = 'Origin'; value = $Origin; source = $(if ($isBug) { 'a defect' } elseif ($inferred) { 'inferred from the branch' } else { 'explicit parent' }); hard = $true })
if ($Priority) { Add-FieldPlan 'Priority' $Priority '-Priority' $false }
if ($Wave)     { Add-FieldPlan 'Wave' $Wave '-Wave' $false }

$fieldMap = Get-FieldMap
$preflight = [System.Collections.Generic.List[string]]::new()
foreach ($f in $fieldPlan) {
    $def = $fieldMap[$f.field]
    $problem = if (-not $def) { "Project #$ProjectNum has no $($f.field) field$(if ($f.field -in @('Category', 'Tier', 'Program')) { ' yet (realignment Stage 0, window W1A, creates Category, Tier and Program)' })." }
               elseif (-not $def.options.ContainsKey($f.value)) { "Field $($f.field) has no option '$($f.value)'$(if ($f.field -in @('Kind', 'Origin', 'Area')) { ' (realignment Stage 0 adds Kind Bug, Origin Defect and the Database Area options)' })." }
               else { $null }
    $f | Add-Member -NotePropertyName problem -NotePropertyValue $problem
    if ($problem -and $f.hard) { $preflight.Add($problem) }
}

Write-Host "  type      $issueType" -ForegroundColor Gray
foreach ($f in $fieldPlan) {
    $flag = if ($f.problem) { if ($f.hard) { '  << refused: ' + $f.problem } else { '  (skipped: ' + $f.problem + ')' } } else { '' }
    Write-Host ("  {0,-9} {1,-38} ({2}){3}" -f $f.field, $f.value, $f.source, $flag) -ForegroundColor Gray
}
if ($planLine) { Write-Host "  family    $Family (body line)" -ForegroundColor Gray }
Write-Host "  level     $childLevel of $MaxNestingLevel; #$($parentNode.number) has $($parentNode.subTotal)/$SubIssueCap sub-issues$(if ($isDataPlatform) { '; Data Platform' })$(if ($categorised) { "; epic #$($epicNode.number) has $($epicCategories.Count) categories" })" -ForegroundColor Gray

$tmp = $null; $createArgs = $null
if (-not $resume) {
    $tmp = New-TemporaryFile
    Set-Content -Path $tmp -Value $body -Encoding UTF8
    $createArgs = @('issue', 'create', '--repo', $Repo, '--title', $fullTitle, '--body-file', "$tmp", '--type', $issueType)
    foreach ($lbl in $labels) { $createArgs += @('--label', $lbl) }
}

if ($DryRun) {
    if ($resume) {
        Write-Host "DRYRUN would resume #$($resume.number) $($resume.url): no create." -ForegroundColor DarkYellow
    }
    else {
        Write-Host "DRYRUN gh $($createArgs -join ' ')" -ForegroundColor DarkYellow
        Write-Host "DRYRUN body:`n$body" -ForegroundColor DarkGray
        Remove-Item $tmp -Force
    }
    Write-Host "DRYRUN would: add to Project #$ProjectNum, set the fields above, read them back, re-check #$($parentNode.number)'s room, link as its sub-issue$(if ($alreadyLinked) { ' (already linked)' }), record in the branch manifest." -ForegroundColor DarkYellow
    if ($preflight.Count) { throw ("Preflight refused (nothing would be created):`n  " + ($preflight -join "`n  ")) }
    Write-Host "Preflight: every option exists." -ForegroundColor Green
    return
}
if ($preflight.Count) { if ($tmp) { Remove-Item $tmp -Force }; throw ("Preflight refused (nothing was created):`n  " + ($preflight -join "`n  ")) }

# 10. Create, typed in the same call; or take the issue an earlier run created.
$failures = [System.Collections.Generic.List[string]]::new()
$newNumber = $null; $issueUrl = $null; $linked = $alreadyLinked; $incomplete = $null
if ($resume) {
    $newNumber = [int]$resume.number; $issueUrl = [string]$resume.url
    Write-Host "Resuming #$newNumber ($issueType) -> $issueUrl" -ForegroundColor Green
}
else {
    $createOut = $null; $createError = $null
    try { $createOut = Invoke-GhRetry -GhArgs $createArgs }
    catch { $createError = $_ }
    finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    $hit = @($createOut | Select-String -Pattern 'https://github\.com/\S+/issues/\d+') | Select-Object -Last 1
    if ($hit) { $issueUrl = $hit.Matches[0].Value; $newNumber = [int]($issueUrl -split '/')[-1] }
    else {
        # The create failed or answered without a URL, yet it may have created the issue: look for it by title.
        $jq = '.[] | select(has("pull_request") | not) | {number, title, url: .html_url}'
        $recent = @(Invoke-GhRetry -AllowFailure -GhArgs @('api', "repos/$Repo/issues?state=all&sort=created&direction=desc&per_page=50", '--jq', $jq) | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
        $found = @($recent | Where-Object { [string](Get-Prop $_ 'title') -ceq $fullTitle }) | Select-Object -First 1
        if (-not $found) {
            if ($createError) { throw $createError }
            throw "Issue creation returned no issue URL, and no issue is titled '$fullTitle':`n$createOut"
        }
        $newNumber = [int]$found.number
        $issueUrl = if (Get-Prop $found 'url') { [string]$found.url } else { "https://github.com/$Repo/issues/$newNumber" }
        $incomplete = 'create'
        $failures.Add("The create reported $(if ($createError) { 'an error: ' + (("$createError" -split "`n") | Where-Object { $_.Trim() } | Select-Object -Last 1) } else { 'no issue URL' }); #$newNumber exists with this title, so the filing stopped there.")
    }
    Write-Host "Created #$newNumber ($issueType) -> $issueUrl" -ForegroundColor Green
}

# 11-13. Everything after the issue exists runs under try/finally: whatever fails, the manifest records the issue
# (UNLINKED unless it is linked, INCOMPLETE:<step> when a step failed), the number is printed, and the script
# exits 1. Re-running the same filing (same branch, parent and title) resumes it instead of creating another.
$step = 'project-add'
try {
    if (-not $incomplete) {
        # 11. Add to the project (idempotent: the project's auto-add can win the race) and set the fields.
        $itemId = $null
        try { $itemId = (ConvertFrom-GhJson (Invoke-GhRetry -GhArgs @('project', 'item-add', "$ProjectNum", '--owner', $Owner, '--url', $issueUrl, '--format', 'json'))).id }
        catch {
            if ("$_" -notmatch '(?i)content already exists') { throw }
            $q = 'query($n:Int!){ repository(owner:"assimalign", name:"cohesion"){ issue(number:$n){ projectItems(first:10){ nodes{ id project{ number } } } } } }'
            $r = ConvertFrom-GhJson (Invoke-GhRetry -GraphQL -GhArgs @('api', 'graphql', '-f', "query=$q", '-F', "n=$newNumber"))
            $nodes = @(Get-Prop (Get-Prop (Get-Prop (Get-Prop (Get-Prop $r 'data') 'repository') 'issue') 'projectItems') 'nodes')
            $itemId = @($nodes | Where-Object { $_ -and [int](Get-Prop (Get-Prop $_ 'project') 'number') -eq $ProjectNum } | ForEach-Object { $_.id }) | Select-Object -First 1
            if (-not $itemId) { throw "#$newNumber is reported as already on Project #$ProjectNum, but no item was found for it." }
        }
        Write-Host "Added to Project #$ProjectNum (item $itemId)" -ForegroundColor Green
        $step = 'fields'
        $written = @{}
        foreach ($f in $fieldPlan) {
            if (Set-ProjectField -FieldMap $fieldMap -ProjectId $script:ProjectId -ItemId $itemId -FieldName $f.field -OptionName $f.value) { $written[$f.field] = $f.value }
        }

        # 12. Read the fields back: a throttled item-edit can print success and leave the field unset.
        $step = 'read-back'
        $rq = 'query($id:ID!){ node(id:$id){ ... on ProjectV2Item { fieldValues(first:50){ nodes{ ... on ProjectV2ItemFieldSingleSelectValue { name field { ... on ProjectV2SingleSelectField { name } } } } } } } }'
        $rb = ConvertFrom-GhJson (Invoke-GhRetry -GraphQL -GhArgs @('api', 'graphql', '-f', "query=$rq", '-F', "id=$itemId"))
        $actual = @{}
        foreach ($v in @(Get-Prop (Get-Prop (Get-Prop (Get-Prop $rb 'data') 'node') 'fieldValues') 'nodes')) {
            $fn = Get-Prop (Get-Prop $v 'field') 'name'
            if ($fn) { $actual[[string]$fn] = [string](Get-Prop $v 'name') }
        }
        foreach ($k in $written.Keys) {
            if ($actual[$k] -cne $written[$k]) { $failures.Add("Field $k reads '$($actual[$k])' after the write of '$($written[$k])'."); $incomplete = 'fields' }
        }

        # 13. Re-check the parent's room (another session may have filed meanwhile), then link.
        $step = 'link'
        if (-not $linked) {
            try {
                Assert-SubIssueRoom -ParentNumber $parentNode.number | Out-Null
                $childNodeId = (ConvertFrom-GhJson (Invoke-GhRetry -GhArgs @('issue', 'view', "$newNumber", '--repo', $Repo, '--json', 'id'))).id
                $linkOut = Invoke-GhRetry -GraphQL -AllowFailure -GhArgs @('api', 'graphql',
                    '-f', "query=mutation(`$p:ID!,`$c:ID!){ addSubIssue(input:{issueId:`$p, subIssueId:`$c}){ subIssue { number } } }",
                    '-F', "p=$($parentNode.nodeId)", '-F', "c=$childNodeId")
                $linkResp = ConvertFrom-GhJson $linkOut
                if ($linkResp -and (Get-Prop (Get-Prop $linkResp 'data') 'addSubIssue')) { $linked = $true }
                else { $failures.Add("The sub-issue link to #$($parentNode.number) failed:$(if ($linkOut) { "`n$($linkOut -join "`n")" })") }
            }
            catch { $failures.Add("The sub-issue link to #$($parentNode.number) was not made: $_") }
        }
        if ($linked) { Write-Host "Linked #$newNumber as sub-issue of #$($parentNode.number)" -ForegroundColor Green }
    }
}
catch {
    $incomplete = $step
    $failures.Add("The filing stopped at '$step': $((("$_" -split "`n") | Where-Object { $_.Trim() } | Select-Object -First 2) -join ' ')")
}
finally {
    # 14. Record in the per-branch manifest, always: an issue that exists is never left untracked.
    $flags = @($Origin)
    if (-not $linked) { $flags += 'UNLINKED' }
    if ($incomplete) { $flags += "INCOMPLETE:$incomplete" }
    $mf = Write-ManifestRow -Branch (Get-BranchName) -Number $newNumber -Code $newWbs -RowOrigin ($flags -join '|') -RowTitle $Title -ParentNumber $parentNode.number
    if ($mf) { Write-Host "Recorded in branch manifest as $($flags -join '|') ($mf)" -ForegroundColor DarkGray }
}

if ($failures.Count) {
    Write-Host ""
    foreach ($f in $failures) { Write-Host "FAILED: $f" -ForegroundColor Red }
    Write-Host "Issue #$newNumber ($issueUrl) exists, but the filing is unfinished$(if (-not $linked) { ": it is not linked under #$($parentNode.number)" }). Re-run the same command to resume it (#$newNumber is reused, not created again), or finish it by hand (reference/project-schema.md, manual recipe)." -ForegroundColor Red
    exit 1
}
Write-Host ""
Write-Host "Done [$Origin]. Add to your PR body:  Closes #$newNumber" -ForegroundColor Cyan
