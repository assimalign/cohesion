#Requires -Version 7.0
<#
.SYNOPSIS
    Reads the assimalign/cohesion Data Platform board (read-only) and emits the waterfall roadmap
    dataset `roadmap-data.json` (schema `cohesion.database.roadmap/2`) and, with -Html, its page.

.DESCRIPTION
    Two phases, either of which can run alone:

      collect  GitHub reads only, nothing reused across runs by default. Paginates
               `repository.issues` (title, state, stateReason, updatedAt, issue type, parent,
               labels, sub-issue and dependency summaries, and the Project #13 fields Status,
               Priority, Wave, Category, Tier, Program, Area, Kind and Origin), selects the L03.02
               subtree (sub-issue closure from #1067 union title-WBS match), re-reads every body
               whose updatedAt changed, reads the native blocked-by list of every item that has
               one, reads the project's single-select field options, and writes
               `board-snapshot.json` (schema `cohesion.database.board-snapshot/2`).

      build    Pure transform. Joins the snapshot with the carried files in data/ (tiers.json,
               programs.json, taxonomy.json, overlay.json, bug-policy.json, realign-plan.json and,
               when the executor has exported it, realign-target.json) and emits
               `roadmap-data.json`: nodes with one placement each, edges, stages, lanes,
               categories, windows, programs, milestones, the critical path, next-up,
               contradictions, hygiene, the taxonomy / bug-policy / realign tabs, counts and
               warnings. With -Previous it also explains what moved. With -Html it renders
               `roadmap.html` from assets/roadmap-template.html.

    READ-ONLY. The script issues GraphQL *queries* and gh reads only. It contains no mutation, no
    `gh issue create/edit/close`, no `gh project item-*`, and no git command. It writes nothing
    outside -OutDir.

    FAILS LOUDLY. `$ErrorActionPreference = 'Stop'`, every GitHub response is shape-checked, and
    output files are written to a temp path and moved into place only after the whole document
    validates. A failed run leaves the previous outputs untouched.

.PARAMETER OutDir
    Directory for the cache and the outputs. Required. Nothing is written anywhere else.

.PARAMETER Stage
    collect | build | all (default all).

.PARAMETER BoardSnapshot
    Build from this snapshot instead of the one in -OutDir (implies -Stage build).

.PARAMETER Refresh
    No-op, kept so older command lines keep working: every run already re-reads the board.

.PARAMETER MaxAgeHours
    Reuse cached issue pages up to this many hours old (default 0: re-read every page on every
    run). For repeated local builds only. Bodies are always re-read when their updatedAt changed,
    and native blocked-by lists are never reused.

.PARAMETER Html
    Also render roadmap.html from assets/roadmap-template.html.

.PARAMETER AsOf
    Override the `generated` date stamp (yyyy-MM-dd). Defaults to today, local time.

.PARAMETER Previous
    A prior roadmap-data.json (schema /2). The new document gains a `changes` block: every node
    that moved, appeared or disappeared, with reason codes.

.PARAMETER RealignStateDir
    The realign executor's state directory (default %LOCALAPPDATA%\cohesion-board-realign, used
    only when it exists). Its journal.jsonl adds lastApplied timestamps to the Changes tab; it
    never decides whether an operation is done.

.EXAMPLE
    ./Get-CohesionRoadmapData.ps1 -OutDir C:\temp\roadmap -Html

.EXAMPLE
    # Rebuild only, from a snapshot collected earlier, and explain what moved since last time:
    ./Get-CohesionRoadmapData.ps1 -OutDir C:\temp\roadmap -Stage build -Html -Previous C:\temp\roadmap-last\roadmap-data.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutDir,
    [ValidateSet('collect', 'build', 'all')][string]$Stage = 'all',
    [string]$BoardSnapshot,
    [switch]$Refresh,
    [int]$MaxAgeHours = 0,
    [switch]$Html,
    [string]$AsOf,
    [string]$Previous,
    [string]$RealignStateDir
)

Set-StrictMode -Version 1.0
$ErrorActionPreference = 'Stop'

$script:Repo = 'assimalign/cohesion'
$script:Owner = 'assimalign'
$script:RepoName = 'cohesion'
$script:ProjectNumber = 13
$script:Root = 1067
$script:RequestCount = 0

$SkillRoot = Split-Path -Parent $PSScriptRoot
$DataDir = Join-Path $SkillRoot 'data'
$AssetDir = Join-Path $SkillRoot 'assets'

if ($BoardSnapshot) { $Stage = 'build' }
if (-not $AsOf) { $AsOf = (Get-Date).ToString('yyyy-MM-dd') }
if ($AsOf -notmatch '^\d{4}-\d{2}-\d{2}$') { throw "AsOf must be yyyy-MM-dd, got '$AsOf'." }
if (-not $RealignStateDir -and $env:LOCALAPPDATA) {
    $d = Join-Path $env:LOCALAPPDATA 'cohesion-board-realign'
    if (Test-Path -LiteralPath $d) { $RealignStateDir = $d }
}

function Write-Step { param([string]$Message) Write-Host "  $Message" }
function Write-Phase { param([string]$Message) Write-Host "`n== $Message" -ForegroundColor Cyan }

# ---------------------------------------------------------------- json helpers

function Read-JsonFile {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Required input not found: $Path" }
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding utf8
    if ([string]::IsNullOrWhiteSpace($raw)) { throw "Input file is empty: $Path" }
    try { return $raw | ConvertFrom-Json -AsHashtable -Depth 64 -DateKind String }
    catch { throw "Input file is not valid JSON: $Path -- $($_.Exception.Message)" }
}

# Atomic: serialize, re-parse to prove the bytes are valid JSON, then move into place.
function Write-JsonFileAtomic {
    param([Parameter(Mandatory)]$Object, [Parameter(Mandatory)][string]$Path, [int]$Depth = 32)
    $tmp = "$Path.$PID.tmp"
    $json = $Object | ConvertTo-Json -Depth $Depth
    Set-Content -LiteralPath $tmp -Value $json -Encoding utf8 -NoNewline
    Add-Content -LiteralPath $tmp -Value "`n" -Encoding utf8 -NoNewline
    try { [void]((Get-Content -LiteralPath $tmp -Raw -Encoding utf8) | ConvertFrom-Json -Depth 64) }
    catch { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue; throw "Refusing to emit $Path - the serialized document does not re-parse: $($_.Exception.Message)" }
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

function Format-IsoDate {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [datetime]) {
        $d = [datetime]$Value
        if ($d.Kind -eq [System.DateTimeKind]::Local) { $d = $d.ToUniversalTime() }
        return $d.ToString('yyyy-MM-ddTHH:mm:ssZ')
    }
    return [string]$Value
}

function Get-Sha256 {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256 {
    param([string]$Text)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $h = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return ([System.BitConverter]::ToString($h) -replace '-', '').ToLowerInvariant()
}

function Sort-Ordinal {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)]$Value)
    begin { $acc = [System.Collections.Generic.List[string]]::new() }
    process { foreach ($x in @($Value)) { if ($null -ne $x) { $acc.Add([string]$x) } } }
    end {
        $acc.Sort([System.StringComparer]::Ordinal)
        foreach ($x in $acc) { $x }
    }
}

function Add-Tally { param($Map, $Key, [int]$By = 1) $k = [string]$Key; if ($Map.Contains($k)) { $Map[$k] = $Map[$k] + $By } else { $Map[$k] = $By } }

# An ordered tally whose keys follow $Order first, then any other key in ordinal order.
function Get-OrderedTally {
    param($Map, [string[]]$Order = @())
    $o = [ordered]@{}
    foreach ($k in $Order) { if ($Map.Contains($k)) { $o[$k] = $Map[$k] } }
    foreach ($k in @($Map.Keys | Sort-Ordinal)) { if (-not $o.Contains($k)) { $o[$k] = $Map[$k] } }
    return $o
}

# ------------------------------------------------------------------ gh client

function Assert-GhReady {
    $gh = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue
    if (-not $gh) { throw "The GitHub CLI 'gh' is not on PATH. This script reads the board through gh; install it and run 'gh auth login'." }
    $null = & gh auth status 2>&1
    if ($LASTEXITCODE -ne 0) { throw "'gh auth status' failed. Run 'gh auth login' (the 'project' read scope is required for the board fields)." }
}

# Read-only GraphQL. Retries on rate limit / transient failure with backoff; never mutates.
function Invoke-GhQuery {
    param(
        [Parameter(Mandatory)][string]$Query,
        [hashtable]$Variables = @{},
        [int]$MaxAttempts = 5
    )
    if ($Query -match '(?im)^\s*mutation\b') { throw "Refusing to send a mutation. This script is read-only." }

    $argv = @('api', 'graphql', '-f', "query=$Query")
    foreach ($k in ($Variables.Keys | Sort-Object)) {
        $v = $Variables[$k]
        if ($null -eq $v) { continue }
        $argv += @('-f', "$k=$v")
    }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $stdout = & gh @argv 2>$script:GhErrFile
        $code = $LASTEXITCODE
        $stderr = if (Test-Path -LiteralPath $script:GhErrFile) { Get-Content -LiteralPath $script:GhErrFile -Raw } else { '' }
        $script:RequestCount++

        if ($code -eq 0 -and $stdout) {
            $parsed = $null
            try { $parsed = ($stdout -join "`n") | ConvertFrom-Json -AsHashtable -Depth 64 -DateKind String }
            catch { throw "GitHub returned a response that is not JSON: $($_.Exception.Message)" }
            if ($parsed.ContainsKey('errors') -and $parsed.errors) {
                $msg = ($parsed.errors | ForEach-Object { $_.message }) -join '; '
                if ($msg -match 'rate limit|secondary rate|timeout|was submitted too quickly') {
                    $wait = [Math]::Min(120, [Math]::Pow(2, $attempt) * 5)
                    Write-Warning "GraphQL rate limited (attempt $attempt/$MaxAttempts): $msg. Waiting $wait s."
                    Start-Sleep -Seconds $wait
                    continue
                }
                throw "GraphQL error: $msg"
            }
            if (-not $parsed.ContainsKey('data') -or $null -eq $parsed.data) { throw "GraphQL response carried no data block." }
            return $parsed.data
        }

        if ($stderr -match 'rate limit|secondary rate|HTTP 5\d\d|timeout') {
            $wait = [Math]::Min(120, [Math]::Pow(2, $attempt) * 5)
            Write-Warning "gh failed (attempt $attempt/$MaxAttempts): $($stderr.Trim()). Waiting $wait s."
            Start-Sleep -Seconds $wait
            continue
        }
        throw "gh api graphql failed (exit $code): $($stderr.Trim())"
    }
    throw "gh api graphql did not succeed after $MaxAttempts attempts."
}

# ------------------------------------------------------------------- collect

# 2 points per page, measured 2026-10-16. A field that does not exist yet returns null.
$IssuePageQuery = @'
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
'@

# 1 point. The option lists say which fields exist and which Category options name which category.
$ProjectFieldsQuery = @'
query {
  organization(login: "assimalign") {
    projectV2(number: 13) {
      fields(first: 50) { nodes { ... on ProjectV2SingleSelectField { name options { name } } } }
    }
  }
}
'@

# The dependency grammar (shared with tooling E1 for the WBS part).
$script:WBS_RE = '^\[(L\d{2}(?:\.(?:\d{2,}|C\d{2}))+)\]\s*(.*)$'
$script:DEP_HEADER = '(?i)^#{1,6}\s*(?:Dependencies|Depends on|Blocked by|Prerequisites)\b'
$script:ANY_HEADER = '^#{1,6}\s'
$script:DEP_LINE = '(?i)^\s*[-*]?\s*(?:\*\*)?(?:Blocked by|Depends on)\b(.*)$'
$script:RELATED = '(?i)^Related work items:(.*)$'
$script:ISSUE_REF = '(?<![A-Za-z0-9])#(\d{1,5})\b'
$script:WBS_REF = '\[(L\d{2}(?:\.(?:\d{2,}|C\d{2}))+)\]'
$script:FAMILY_LINE = 'Tier ([123]) family `([a-z0-9-]+)`'
$script:FORM_RANK = @{ 'native' = 0; 'section' = 1; 'section-wbs' = 2; 'line' = 3; 'related' = 4 }

function Get-BodyDependencies {
    param([int]$Number, [string]$Body, $All, $WbsIndex, $Drops)
    $found = [ordered]@{}   # "<n>/<form>" -> entry
    $add = {
        param([int]$To, [string]$Form)
        if ($To -eq $Number) { $Drops.self++; return }
        if (-not $All.ContainsKey($To)) { $Drops.notIssue++; return }
        $k = "$To/$Form"
        if (-not $found.Contains($k)) { $found[$k] = [ordered]@{ number = $To; form = $Form } }
    }
    $lines = $Body -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match $script:DEP_HEADER) {
            for ($j = $i + 1; $j -lt $lines.Count -and $lines[$j] -notmatch $script:ANY_HEADER; $j++) {
                $l = $lines[$j]
                foreach ($m in [regex]::Matches($l, $script:ISSUE_REF)) {
                    if ($l.Substring(0, $m.Index) -match '\bPR\s*$') { continue }
                    & $add ([int]$m.Groups[1].Value) 'section'
                }
                foreach ($m in [regex]::Matches($l, $script:WBS_REF)) {
                    $code = $m.Groups[1].Value
                    $hits = if ($WbsIndex.ContainsKey($code)) { @($WbsIndex[$code]) } else { @() }
                    if ($hits.Count -eq 1) { & $add ([int]$hits[0]) 'section-wbs' } else { $Drops.ambiguousWbs++ }
                }
            }
            continue
        }
        $lm = [regex]::Match($line, $script:DEP_LINE)
        if ($lm.Success) {
            foreach ($m in [regex]::Matches($lm.Groups[1].Value, $script:ISSUE_REF)) { & $add ([int]$m.Groups[1].Value) 'line' }
        }
        $rm = [regex]::Match($line, $script:RELATED)
        if ($rm.Success) {
            foreach ($m in [regex]::Matches($rm.Groups[1].Value, '(?i)blocked-by\s+#(\d{1,5})\b')) { & $add ([int]$m.Groups[1].Value) 'related' }
        }
    }
    return @($found.Values)
}

function Invoke-Collect {
    param([string]$CacheDir, [string]$SnapshotPath)

    Assert-GhReady
    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null
    $script:GhErrFile = Join-Path $CacheDir 'gh.stderr.txt'

    # ---- pass 1: every repository issue, 100 per page. Reused only when -MaxAgeHours opts in.
    $statePath = Join-Path $CacheDir 'pages.state.json'
    $state = $null
    if ($MaxAgeHours -gt 0 -and (Test-Path -LiteralPath $statePath)) {
        $state = Read-JsonFile $statePath
        $age = (Get-Date) - [datetime]::Parse([string]$state.fetchedAt)
        if (-not $state.completed -or $age.TotalHours -gt $MaxAgeHours) { $state = $null }
        else { Write-Step "Reusing $(@($state.pages).Count) cached issue pages ($([Math]::Round($age.TotalHours, 1)) h old, -MaxAgeHours $MaxAgeHours)." }
    }
    if (-not $state) {
        Get-ChildItem -LiteralPath $CacheDir -Filter 'issues-page-*.json' -ErrorAction SilentlyContinue | Remove-Item -Force
        $state = @{ pages = @(); completed = $false; endCursor = $null; fetchedAt = (Get-Date).ToString('o') }
        $pageIndex = 0
        $cursor = $null
        while ($true) {
            $pageIndex++
            $data = Invoke-GhQuery -Query $IssuePageQuery -Variables @{ endCursor = $cursor }
            $conn = $data.repository.issues
            if ($null -eq $conn) { throw "repository.issues came back null - check that the token can read $script:Repo." }
            $file = Join-Path $CacheDir ('issues-page-{0:d2}.json' -f $pageIndex)
            Write-JsonFileAtomic -Object @($conn.nodes) -Path $file -Depth 24
            $state.pages = @($state.pages) + @([ordered]@{ index = $pageIndex; file = Split-Path -Leaf $file; count = @($conn.nodes).Count })
            $cursor = $conn.pageInfo.endCursor
            $state.endCursor = $cursor
            $state.completed = -not $conn.pageInfo.hasNextPage
            Write-JsonFileAtomic -Object $state -Path $statePath -Depth 8
            Write-Step ("page {0}: {1} issues (total {2})" -f $pageIndex, @($conn.nodes).Count, (@($state.pages) | Measure-Object -Property count -Sum).Sum)
            if ($state.completed) { break }
        }
    }

    $all = [System.Collections.Generic.Dictionary[int, hashtable]]::new()
    foreach ($p in @($state.pages)) {
        $nodes = Read-JsonFile (Join-Path $CacheDir $p.file)
        foreach ($n in @($nodes)) { $all[[int]$n.number] = $n }
    }
    if ($all.Count -lt 100) { throw "Only $($all.Count) issues came back from $script:Repo - that is too few to be the whole repository. Refusing to build a partial roadmap." }
    Write-Step "$($all.Count) issues in the repository snapshot."

    if (-not $all.ContainsKey($script:Root)) { throw "Root #$($script:Root) is not in the repository snapshot." }
    if ([string]$all[$script:Root].title -notmatch '^\[L03\.02\.00\]') { throw "Root #$($script:Root) no longer parses as [L03.02.00]: '$($all[$script:Root].title)'. The script hard-codes this one issue number; fix it before regenerating." }

    # ---- subtree: sub-issue closure from the root, union title-WBS match
    $kids = @{}
    foreach ($n in $all.Values) {
        if ($n.parent) {
            $p = [int]$n.parent.number
            if (-not $kids.ContainsKey($p)) { $kids[$p] = [System.Collections.Generic.List[int]]::new() }
            $kids[$p].Add([int]$n.number)
        }
    }
    $reach = [System.Collections.Generic.HashSet[int]]::new()
    [void]$reach.Add($script:Root)
    $queue = [System.Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($script:Root)
    while ($queue.Count) {
        $n = $queue.Dequeue()
        if ($kids.ContainsKey($n)) { foreach ($c in $kids[$n]) { if ($reach.Add($c)) { $queue.Enqueue($c) } } }
    }
    $titled = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($n in $all.Values) { if ($n.title -match '^\[L03\.02') { [void]$titled.Add([int]$n.number) } }
    $subtree = [System.Collections.Generic.HashSet[int]]::new($reach)
    foreach ($t in $titled) { [void]$subtree.Add($t) }
    if ($subtree.Count -lt 50) { throw "The L03.02 subtree resolved to only $($subtree.Count) items. Expected several hundred; refusing to emit." }
    Write-Step "$($subtree.Count) subtree items ($($reach.Count) by sub-issue link, $($titled.Count) by title WBS)."

    # ---- pass 2: bodies, 50 aliases per request. A cached body is reused only when its
    #      updatedAt equals the page's, so a dependency added to an existing body is seen.
    $bodyPath = Join-Path $CacheDir 'bodies.json'
    $bodies = @{}
    if (Test-Path -LiteralPath $bodyPath) {
        foreach ($kv in (Read-JsonFile $bodyPath).GetEnumerator()) {
            if ($kv.Value -is [System.Collections.IDictionary] -and $kv.Value.Contains('updatedAt')) { $bodies[[string]$kv.Key] = $kv.Value }
        }
    }
    $want = [System.Collections.Generic.List[int]]::new()
    foreach ($n in ($subtree | Sort-Object)) {
        $cached = $bodies["$n"]
        if (-not $cached -or [string]$cached.updatedAt -ne [string]$all[$n].updatedAt) { $want.Add($n) }
    }
    Write-Step "bodies: $($subtree.Count - $want.Count) unchanged since the last read, $($want.Count) to read."
    $batchSize = 50
    for ($i = 0; $i -lt $want.Count; $i += $batchSize) {
        $batch = $want.GetRange($i, [Math]::Min($batchSize, $want.Count - $i))
        $sb = [System.Text.StringBuilder]::new()
        [void]$sb.AppendLine('query { repository(owner: "assimalign", name: "cohesion") {')
        foreach ($n in $batch) { [void]$sb.AppendLine("  i${n}: issue(number: $n) { number body }") }
        [void]$sb.AppendLine('} }')
        $data = Invoke-GhQuery -Query $sb.ToString()
        foreach ($n in $batch) {
            $node = $data.repository["i$n"]
            if ($null -eq $node) { throw "Body batch returned no issue #$n." }
            $bodies["$n"] = [ordered]@{ updatedAt = [string]$all[$n].updatedAt; body = $(if ($node.body) { [string]$node.body } else { '' }) }
        }
        Write-JsonFileAtomic -Object $bodies -Path $bodyPath -Depth 4
        Write-Step ("bodies {0}/{1}" -f [Math]::Min($i + $batchSize, $want.Count), $want.Count)
    }

    # ---- pass 3: native blocked-by, read on every run for every item that has one. A link
    #      change may not bump updatedAt, so this list is never cached.
    $native = @{}
    $needBlocked = @($subtree | Where-Object { $all[$_].issueDependenciesSummary -and [int]$all[$_].issueDependenciesSummary.totalBlockedBy -gt 0 } | Sort-Object)
    for ($i = 0; $i -lt $needBlocked.Count; $i += $batchSize) {
        $batch = $needBlocked[$i..([Math]::Min($i + $batchSize - 1, $needBlocked.Count - 1))]
        $sb = [System.Text.StringBuilder]::new()
        [void]$sb.AppendLine('query { repository(owner: "assimalign", name: "cohesion") {')
        foreach ($n in $batch) { [void]$sb.AppendLine("  i${n}: issue(number: $n) { number blockedBy(first: 50) { totalCount nodes { number } } }") }
        [void]$sb.AppendLine('} }')
        $data = Invoke-GhQuery -Query $sb.ToString()
        foreach ($n in $batch) {
            $node = $data.repository["i$n"]
            if ($null -eq $node -or $null -eq $node.blockedBy) { throw "Blocked-by batch returned nothing for #$n." }
            $nums = @(@($node.blockedBy.nodes) | ForEach-Object { [int]$_.number })
            if ([int]$node.blockedBy.totalCount -gt $nums.Count) { throw "#$n reports $($node.blockedBy.totalCount) blocked-by links but returned $($nums.Count). GitHub caps a relationship at 50; refusing to emit a partial dependency list." }
            $native[$n] = $nums
        }
    }
    Write-Step "native blocked-by: $($needBlocked.Count) items with links."

    # ---- pass 4: the project's single-select fields and options (1 point)
    $fieldsData = Invoke-GhQuery -Query $ProjectFieldsQuery
    $projectFields = [ordered]@{}
    foreach ($f in @($fieldsData.organization.projectV2.fields.nodes)) {
        if ($f -and $f.ContainsKey('name') -and $f.ContainsKey('options')) { $projectFields[[string]$f.name] = @(@($f.options) | ForEach-Object { [string]$_.name }) }
    }
    if (-not $projectFields.Contains('Status')) { throw "Project #$script:ProjectNumber returned no Status field - check the token's project read scope." }

    # ---- shape the snapshot
    $wbsIndex = @{}
    foreach ($n in $all.Values) {
        $m = [regex]::Match([string]$n.title, $script:WBS_RE)
        if ($m.Success) { $code = $m.Groups[1].Value; if (-not $wbsIndex.ContainsKey($code)) { $wbsIndex[$code] = [System.Collections.Generic.List[int]]::new() }; $wbsIndex[$code].Add([int]$n.number) }
    }
    $drops = @{ self = 0; notIssue = 0; ambiguousWbs = 0 }
    $bySource = [ordered]@{ section = 0; 'section-wbs' = 0; line = 0; related = 0; native = 0 }
    $external = [ordered]@{}
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($n in ($subtree | Sort-Object)) {
        if (-not $all.ContainsKey($n)) { throw "Issue #$n is in the subtree but missing from the paginated set - the collection is inconsistent; re-run." }
        $i = $all[$n]
        $body = if ($bodies.ContainsKey("$n")) { [string]$bodies["$n"].body } else { '' }
        $m = [regex]::Match([string]$i.title, $script:WBS_RE)
        $wbs = if ($m.Success) { $m.Groups[1].Value } else { $null }
        $titleNoWbs = if ($m.Success) { $m.Groups[2].Value } else { [string]$i.title }
        $isCategory = [bool]($wbs -and $wbs -cmatch '\.C\d{2}$')

        $bi = $null
        foreach ($pi in @($i.projectItems.nodes)) { if ($pi -and $pi.project -and [int]$pi.project.number -eq $script:ProjectNumber) { $bi = $pi; break } }
        $fv = { param($k) if ($bi -and $bi.ContainsKey($k) -and $bi[$k] -and $bi[$k].ContainsKey('name')) { [string]$bi[$k].name } else { $null } }

        $deps = [System.Collections.Generic.List[object]]::new()
        foreach ($d in (Get-BodyDependencies -Number $n -Body $body -All $all -WbsIndex $wbsIndex -Drops $drops)) { $deps.Add($d) }
        if ($native.ContainsKey($n)) { foreach ($b in $native[$n]) { if ($b -ne $n) { $deps.Add([ordered]@{ number = [int]$b; form = 'native' }) } } }
        $seenDep = [System.Collections.Generic.HashSet[string]]::new()
        $depList = [System.Collections.Generic.List[object]]::new()
        foreach ($d in ($deps | Sort-Object @{ Expression = { [int]$_.number } }, @{ Expression = { $script:FORM_RANK[[string]$_.form] } })) {
            if (-not $seenDep.Add("$($d.number)/$($d.form)")) { continue }
            $depList.Add($d)
            $bySource[[string]$d.form] = $bySource[[string]$d.form] + 1
            $t = [int]$d.number
            if (-not $subtree.Contains($t) -and -not $external.Contains("$t") -and $all.ContainsKey($t)) {
                $x = $all[$t]
                $xb = $null
                foreach ($pi in @($x.projectItems.nodes)) { if ($pi -and $pi.project -and [int]$pi.project.number -eq $script:ProjectNumber) { $xb = $pi; break } }
                $external["$t"] = [ordered]@{
                    number = $t; title = [string]$x.title; state = [string]$x.state
                    stateReason = $(if ($x.stateReason) { [string]$x.stateReason } else { $null })
                    boardStatus = $(if ($xb -and $xb.status) { [string]$xb.status.name } else { $null })
                    url = "https://github.com/$script:Repo/issues/$t"
                }
            }
        }

        $famLine = $null
        $fm = [regex]::Match($body, $script:FAMILY_LINE)
        if ($fm.Success) { $famLine = [ordered]@{ tier = "T$($fm.Groups[1].Value)"; slug = $fm.Groups[2].Value } }
        $tierTokens = [System.Collections.Generic.List[string]]::new()
        foreach ($tm in [regex]::Matches($body, '\b(?:Tier\s*([123])|T([123]))\b')) {
            $t = 'T' + $(if ($tm.Groups[1].Success) { $tm.Groups[1].Value } else { $tm.Groups[2].Value })
            if (-not $tierTokens.Contains($t)) { $tierTokens.Add($t) }
        }
        $headings = [System.Collections.Generic.List[string]]::new()
        foreach ($hm in [regex]::Matches($body, '(?m)^#{1,6}\s+(.+?)\s*$')) { if ($headings.Count -lt 40) { $headings.Add($hm.Groups[1].Value) } }
        $docs = [System.Collections.Generic.List[string]]::new()
        foreach ($dm in [regex]::Matches($body, 'docs/[A-Za-z0-9_/.\-]+\.md')) { if (-not $docs.Contains($dm.Value)) { $docs.Add($dm.Value) } }
        $childNums = @()
        if ($kids.ContainsKey($n)) { $childNums = @($kids[$n] | Sort-Object) }
        $firstLine = ''
        foreach ($line in ($body -split "`r?`n")) { if ($line.Trim() -and $line -notmatch '^#') { $firstLine = $line.Trim(); break } }
        if ($firstLine.Length -gt 240) { $firstLine = $firstLine.Substring(0, 240) }

        $items.Add([ordered]@{
                number                    = $n
                title                     = [string]$i.title
                url                       = "https://github.com/$script:Repo/issues/$n"
                wbs                       = $wbs
                titleWithoutWbs           = $titleNoWbs
                isCategory                = $isCategory
                categoryCode              = $(if ($isCategory) { $wbs } else { $null })
                categoryName              = $(if ($isCategory) { $titleNoWbs } else { $null })
                issueType                 = $(if ($i.issueType) { [string]$i.issueType.name } else { $null })
                state                     = [string]$i.state
                stateReason               = $(if ($i.stateReason) { [string]$i.stateReason } else { $null })
                updatedAt                 = [string]$i.updatedAt
                boardStatus               = (& $fv 'status')
                boardPriority             = (& $fv 'priority')
                boardWave                 = (& $fv 'wave')
                boardCategory             = (& $fv 'category')
                boardCategoryOptionId     = $(if ($bi -and $bi.category -and $bi.category.ContainsKey('optionId')) { [string]$bi.category.optionId } else { $null })
                boardTier                 = (& $fv 'tier')
                boardProgram              = (& $fv 'program')
                boardArea                 = (& $fv 'area')
                boardKind                 = (& $fv 'kind')
                boardOrigin               = (& $fv 'origin')
                onBoard                   = [bool]$bi
                parent                    = $(if ($i.parent) { [int]$i.parent.number } else { $null })
                parentTitle               = $(if ($i.parent) { [string]$i.parent.title } else { $null })
                children                  = @($childNums)
                childCount                = @($childNums).Count
                subIssuesSummaryTotal     = $(if ($i.subIssuesSummary) { [int]$i.subIssuesSummary.total } else { $null })
                subIssuesSummaryCompleted = $(if ($i.subIssuesSummary) { [int]$i.subIssuesSummary.completed } else { $null })
                totalBlockedBy            = $(if ($i.issueDependenciesSummary) { [int]$i.issueDependenciesSummary.totalBlockedBy } else { 0 })
                totalBlocking             = $(if ($i.issueDependenciesSummary) { [int]$i.issueDependenciesSummary.totalBlocking } else { 0 })
                labels                    = @(@($i.labels.nodes) | ForEach-Object { [string]$_.name })
                createdAt                 = (Format-IsoDate $i.createdAt)
                closedAt                  = (Format-IsoDate $i.closedAt)
                bodyLength                = $body.Length
                bodySha256                = (Get-TextSha256 $body)
                bodyHeadings              = @($headings)
                bodyProgramDocs           = @($docs)
                bodyTierTokens            = @($tierTokens)
                planFamilyLine            = $famLine
                dependencies              = @($depList)
                bodyFirstLine             = $firstLine
            })
    }

    $byStatus = [ordered]@{}
    foreach ($it in $items) { Add-Tally $byStatus $(if ($it.boardStatus) { $it.boardStatus } else { '(none)' }) }
    $present = [ordered]@{ Category = 0; Tier = 0; Program = 0; Area = 0 }
    foreach ($it in $items) {
        if ($it.boardCategory) { $present.Category++ }
        if ($it.boardTier) { $present.Tier++ }
        if ($it.boardProgram) { $present.Program++ }
        if ($it.boardArea) { $present.Area++ }
    }

    $snapshot = [ordered]@{
        schema        = 'cohesion.database.board-snapshot/2'
        generatedAt   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        source        = [ordered]@{
            repo    = $script:Repo
            project = $script:ProjectNumber
            root    = $script:Root
            method  = "GraphQL repository.issues full pagination ($($all.Count) issues, board fields inline) + body batches keyed by updatedAt + native blocked-by batch + project field options; sub-issue parent inversion + title WBS match"
        }
        projectFields = $projectFields
        counts        = [ordered]@{
            totalItems              = $items.Count
            repositoryIssues        = $all.Count
            reachableBySubIssueLink = $reach.Count
            matchedByTitleWbs       = $titled.Count
            open                    = @($items | Where-Object { $_.state -eq 'OPEN' }).Count
            closed                  = @($items | Where-Object { $_.state -eq 'CLOSED' }).Count
            byBoardStatus           = $byStatus
            notOnBoard              = @($items | Where-Object { -not $_.onBoard }).Count
            fieldsPresent           = $present
            blockedByItems          = $needBlocked.Count
            dependencies            = $bySource
            dependencyDrops         = [ordered]@{ self = $drops.self; notIssue = $drops.notIssue; ambiguousWbs = $drops.ambiguousWbs }
            bodiesRead              = $want.Count
            requests                = $script:RequestCount
        }
        items         = @($items)
        externalItems = @($external.Values)
    }
    Write-JsonFileAtomic -Object $snapshot -Path $SnapshotPath -Depth 24
    Write-Step "snapshot -> $SnapshotPath ($($items.Count) items, $($external.Count) external dependency targets, $script:RequestCount GraphQL requests)"
    return (Read-JsonFile $SnapshotPath)
}

# --------------------------------------------------------------------- build

function Get-ShortTitle {
    param([string]$Text, [int]$Max = 48)
    if ($null -eq $Text) { return '' }
    if ($Text.Length -le $Max) { return $Text }
    $cut = $Text.Substring(0, $Max)
    $sp = $cut.LastIndexOf(' ')
    $head = if ($sp -gt 20) { $cut.Substring(0, $sp) } else { $cut }
    return ($head -replace '[\s,.;:\-]+$', '') + [char]0x2026
}

function Join-Labels { param($Ids, $Nodes, [int]$Max = 8)
    $l = @(@($Ids) | Select-Object -First $Max | ForEach-Object { if ($Nodes.Contains([string]$_) -and $null -ne $Nodes[[string]$_].number) { "#$($Nodes[[string]$_].number)" } elseif ($Nodes.Contains([string]$_) -and $Nodes[[string]$_].kind -eq 'slot') { "the '$($Nodes[[string]$_].title)' slot" } else { [string]$_ } })
    $s = $l -join ', '
    if (@($Ids).Count -gt $Max) { $s += ", +$(@($Ids).Count - $Max) more" }
    return $s
}

function Invoke-Build {
    param([hashtable]$Snapshot, [string]$SnapshotPath, [string]$OutPath, $PreviousDoc)

    if ([string]$Snapshot.schema -ne 'cohesion.database.board-snapshot/2') { throw "Snapshot schema is '$($Snapshot.schema)'. re-collect: /1 has no board fields or blocked-by." }

    $tiers = Read-JsonFile (Join-Path $DataDir 'tiers.json')
    $programs = Read-JsonFile (Join-Path $DataDir 'programs.json')
    $tax = Read-JsonFile (Join-Path $DataDir 'taxonomy.json')
    $overlay = Read-JsonFile (Join-Path $DataDir 'overlay.json')
    $bugPolicyData = Read-JsonFile (Join-Path $DataDir 'bug-policy.json')
    $realignPlanPath = Join-Path $DataDir 'realign-plan.json'
    $realignPlan = if (Test-Path -LiteralPath $realignPlanPath) { Read-JsonFile $realignPlanPath } else { $null }
    $realignTargetPath = Join-Path $DataDir 'realign-target.json'
    $realignTarget = if (Test-Path -LiteralPath $realignTargetPath) { Read-JsonFile $realignTargetPath } else { $null }
    if ([string]$tiers.schema -ne 'cohesion.database.tiers/2') { throw "data/tiers.json is '$($tiers.schema)', expected cohesion.database.tiers/2." }
    if ([string]$programs.schema -ne 'cohesion.database.programs/2') { throw "data/programs.json is '$($programs.schema)', expected cohesion.database.programs/2." }
    if ([string]$overlay.schema -ne 'cohesion.database.roadmap.overlay/2') { throw "data/overlay.json is '$($overlay.schema)', expected cohesion.database.roadmap.overlay/2." }

    $buildClock = [System.Diagnostics.Stopwatch]::StartNew()
    function Write-BuildMark { param([string]$What) Write-Verbose ("build {0,6:n1}s  {1}" -f $buildClock.Elapsed.TotalSeconds, $What) }
    $warnings = [System.Collections.Generic.List[object]]::new()
    function Add-Warn { param([string]$Code, [string]$Message, $Node, $Detail, $Nodes)
        $w = [ordered]@{ code = $Code; message = $Message }
        if ($null -ne $Node) { $w.node = $Node }
        if ($null -ne $Detail) { $w.detail = $Detail }
        if ($null -ne $Nodes) { $w.count = @($Nodes).Count; $w.nodes = @($Nodes) }
        $warnings.Add($w)
    }
    $agg = @{}   # aggregate warnings: code -> List[id]
    function Add-AggWarn { param([string]$Code, [string]$Id) if (-not $agg.ContainsKey($Code)) { $agg[$Code] = [System.Collections.Generic.List[string]]::new() }; $agg[$Code].Add($Id) }

    Write-BuildMark 'indexes'
    # ------------------------------------------------------------- indexes
    $snapItems = @($Snapshot.items)
    $byNum = @{}; foreach ($i in $snapItems) { $byNum[[int]$i.number] = $i }
    $externalByNum = @{}; foreach ($x in @($Snapshot.externalItems)) { $externalByNum[[int]$x.number] = $x }
    $projectFields = if ($Snapshot.projectFields) { $Snapshot.projectFields } else { @{} }
    $tierItemByNum = @{}; foreach ($i in @($tiers.items)) { $tierItemByNum[[int]$i.number] = $i }
    $taxByNum = @{}; foreach ($m in @($tax.mapping)) { $taxByNum[[int]$m.number] = $m }
    $catDef = [ordered]@{}; foreach ($c in @($tax.categories)) { $catDef[[string]$c.code] = $c }
    $catByName = @{}; foreach ($c in @($tax.categories)) { $catByName[[string]$c.name] = [string]$c.code }
    $famById = [ordered]@{}; foreach ($f in @($tiers.families)) { $famById[[string]$f.id] = $f }
    $programList = @($programs.programs)
    $programById = [ordered]@{}; foreach ($pr in $programList) { $programById[[string]$pr.id] = $pr }
    $programByOption = @{}; foreach ($pr in $programList) { $programByOption[[string]$pr.boardOption] = [string]$pr.id }
    $programOrder = @($programList | ForEach-Object { [string]$_.id })

    $FAMILY_ALIAS = @{ 'sql-parameters' = 'x-parameters'; 'sql-explain' = 'x-explain' }
    function Get-CanonFamily { param([string]$Id) if ($FAMILY_ALIAS.ContainsKey($Id)) { $FAMILY_ALIAS[$Id] } else { $Id } }
    $familyOf = @{}
    foreach ($f in @($tiers.families)) {
        if ($f.items) { foreach ($k in $f.items.Keys) { foreach ($n in @($f.items[$k])) { $familyOf[[int]$n] = (Get-CanonFamily ([string]$f.id)) } } }
    }
    foreach ($it in @($tiers.items)) { if ($it.ContainsKey('family') -and $it.family) { $familyOf[[int]$it.number] = (Get-CanonFamily ([string]$it.family)) } }
    $familyBySlug = @{}
    foreach ($f in @($tiers.families)) {
        $familyBySlug[[string]$f.id] = (Get-CanonFamily ([string]$f.id))
        if ($f.ContainsKey('bodySlugs')) { foreach ($s in @($f.bodySlugs)) { $familyBySlug[[string]$s] = (Get-CanonFamily ([string]$f.id)) } }
    }
    $LANG_OF_FAMILY = { param($f) $l = [string]$f.language; if ($l -eq 'Cross-cutting') { 'Shared' } else { $l } }

    # ---------------------------------------------------------- containers
    $rootItem = $byNum[$script:Root]
    if (-not $rootItem) { throw "Root #$($script:Root) is missing from the snapshot." }
    if ([string]$rootItem.wbs -ne 'L03.02.00') { throw "Root #$($script:Root) does not parse as [L03.02.00] (wbs '$($rootItem.wbs)')." }
    $gateIssue = @{}   # gate issue number -> program id
    foreach ($pr in $programList) { if ([string]$pr.shape -eq 'gate') { $gateIssue[[int]$pr.gate.issue] = [string]$pr.id } }
    $epicByWbs = [ordered]@{}
    foreach ($i in ($snapItems | Sort-Object { [int]$_.number })) {
        if ($null -ne $i.parent -and [int]$i.parent -eq $script:Root -and $i.wbs -and ([string]$i.wbs).Split('.').Count -eq 3 -and -not $gateIssue.ContainsKey([int]$i.number)) {
            if ($epicByWbs.Contains([string]$i.wbs)) { Add-Warn 'duplicate-epic' 'Two direct sub-issues of the root carry the same epic code.' "i$($i.number)" ([string]$i.wbs) } else { $epicByWbs[[string]$i.wbs] = [int]$i.number }
        }
    }
    $epicWbsByIssue = @{}; foreach ($k in $epicByWbs.Keys) { $epicWbsByIssue[[int]$epicByWbs[$k]] = $k }
    $categoryByCode = [ordered]@{}
    foreach ($i in ($snapItems | Where-Object { $_.isCategory } | Sort-Object { [int]$_.number })) {
        $code = [string]$i.categoryCode
        $epicCode = ($code.Split('.')[0..2]) -join '.'
        $categoryByCode[$code] = [ordered]@{ code = $code; issue = [int]$i.number; name = [string]$i.categoryName; epic = $epicCode; parent = $i.parent }
        if (-not $epicByWbs.Contains($epicCode) -or $null -eq $i.parent -or [int]$i.parent -ne [int]$epicByWbs[$epicCode]) {
            Add-Warn 'category-misparented' "Category issue's tree parent is not its code's epic ($epicCode)." "i$($i.number)" "parent=#$($i.parent)"
        }
    }
    $categoryIssueNums = @{}; foreach ($c in $categoryByCode.Values) { $categoryIssueNums[[int]$c.issue] = [string]$c.code }
    $categoryByOptionName = @{}; foreach ($c in $categoryByCode.Values) { $categoryByOptionName[[string]$c.name] = [string]$c.code }
    $containerKind = @{}
    $containerKind[$script:Root] = 'root'
    foreach ($n in $epicByWbs.Values) { $containerKind[[int]$n] = 'epic' }
    foreach ($n in $categoryIssueNums.Keys) { $containerKind[[int]$n] = 'category' }
    foreach ($n in $gateIssue.Keys) { if ($byNum.ContainsKey([int]$n)) { $containerKind[[int]$n] = 'gate' } }
    $isContainer = { param([int]$n) $containerKind.ContainsKey($n) }

    # ------------------------------------------------------------- descendants
    $descCache = @{}
    function Get-Descendants { param([int]$N)
        if ($descCache.ContainsKey($N)) { return , $descCache[$N] }
        $acc = [System.Collections.Generic.List[int]]::new()
        $stack = [System.Collections.Generic.Stack[int]]::new()
        if ($byNum.ContainsKey($N)) { foreach ($c in @($byNum[$N].children)) { $stack.Push([int]$c) } }
        $seen = [System.Collections.Generic.HashSet[int]]::new()
        while ($stack.Count) { $c = $stack.Pop(); if (-not $seen.Add($c)) { continue }; $acc.Add($c); if ($byNum.ContainsKey($c)) { foreach ($g in @($byNum[$c].children)) { $stack.Push([int]$g) } } }
        $arr = @($acc | Sort-Object)
        $descCache[$N] = $arr
        return , $arr
    }

    Write-BuildMark 'categories'
    # -------------------------------------------------------------- categories
    $workNums = @($snapItems | Where-Object { -not (& $isContainer ([int]$_.number)) } | ForEach-Object { [int]$_.number } | Sort-Object)
    $catOf = @{}; $catSrc = @{}
    $optionUnmatched = [System.Collections.Generic.List[string]]::new()
    $catOptions = if ($projectFields.Contains('Category')) { @($projectFields['Category']) } else { $null }
    foreach ($n in $workNums) {
        $it = $byNum[$n]
        $code = $null; $src = $null
        if ($it.boardCategory) {
            $opt = [string]$it.boardCategory
            if ($categoryByOptionName.ContainsKey($opt)) { $code = $categoryByOptionName[$opt]; $src = 'field' }
            elseif ($catByName.ContainsKey($opt)) { $code = $catByName[$opt]; $src = 'field'; if (-not $optionUnmatched.Contains($opt)) { $optionUnmatched.Add($opt) } }
            else { Add-Warn 'category-option-unmatched' "Category option '$opt' matches no category issue title and no carried category name." "i$n" $opt }
        }
        if ($null -ne $it.parent -and $categoryIssueNums.ContainsKey([int]$it.parent)) {
            $treeCode = $categoryIssueNums[[int]$it.parent]
            if ($null -eq $code) { $code = $treeCode; $src = 'tree' }
            elseif ($code -ne $treeCode) { Add-Warn 'category-tree-mismatch' "The Category field says $code; the tree parent is category $treeCode. The field wins." "i$n" $null }
        }
        if ($null -eq $code -and $taxByNum.ContainsKey($n) -and $taxByNum[$n].category) { $code = [string]$taxByNum[$n].category; $src = 'carried' }
        if ($code) { $catOf[$n] = $code; $catSrc[$n] = $src }
    }
    foreach ($opt in $optionUnmatched) { Add-Warn 'category-option-unmatched' "Category option '$opt' matches no category issue title; matched by the carried category name instead. Rename the option and its category issue together." $null $opt }
    foreach ($n in $workNums) {
        if ($catOf.ContainsKey($n)) { continue }
        $cur = $byNum[$n].parent; $guard = 0
        while ($null -ne $cur -and $guard -lt 16) {
            if ($categoryIssueNums.ContainsKey([int]$cur)) { $catOf[$n] = $categoryIssueNums[[int]$cur]; $catSrc[$n] = 'inherited'; break }
            if ($catOf.ContainsKey([int]$cur) -and $catSrc[[int]$cur] -ne 'inherited') { $catOf[$n] = $catOf[[int]$cur]; $catSrc[$n] = 'inherited'; break }
            if (-not $byNum.ContainsKey([int]$cur)) { break }
            $cur = $byNum[[int]$cur].parent; $guard++
        }
        if (-not $catOf.ContainsKey($n)) { $catSrc[$n] = 'none' }
    }

    # -------------------------------------------------------------- epics
    $epicOf = @{}
    foreach ($n in $workNums) {
        $e = $null
        if ($catOf.ContainsKey($n)) { $e = ($catOf[$n].Split('.')[0..2]) -join '.' }
        if (-not $e) {
            $cur = $byNum[$n].parent; $guard = 0
            while ($null -ne $cur -and $guard -lt 16) { if ($epicWbsByIssue.ContainsKey([int]$cur)) { $e = $epicWbsByIssue[[int]$cur]; break }; if (-not $byNum.ContainsKey([int]$cur)) { break }; $cur = $byNum[[int]$cur].parent; $guard++ }
        }
        if (-not $e -and $byNum[$n].wbs) {
            $mm = [regex]::Match([string]$byNum[$n].wbs, '^(L03\.02\.\d{2})')
            if ($mm.Success) { $e = $mm.Groups[1].Value; Add-AggWarn 'epic-from-prefix' "i$n" }
        }
        $epicOf[$n] = $e
    }

    # -------------------------------------------------------------- tiers
    $tierOf = @{}; $tierSrc = @{}; $langOf = @{}; $famLineOf = @{}
    $tierOpts = $tiers.tierBoardOptions
    foreach ($n in $workNums) {
        $it = $byNum[$n]
        $t = $null; $src = 'none'
        if ($it.boardTier) {
            $opt = [string]$it.boardTier
            if ($tierOpts.Contains($opt)) { $t = $tierOpts[$opt]; if ($null -ne $t) { $src = 'field' } }
            else { Add-Warn 'tier-option-unmatched' "Tier option '$opt' is not in tiers.json tierBoardOptions." "i$n" $opt }
        }
        $planTier = $null
        if ($tierItemByNum.ContainsKey($n) -and $tierItemByNum[$n].tier) { $planTier = [string]$tierItemByNum[$n].tier }
        elseif ($familyOf.ContainsKey($n)) {
            $f = $famById[$familyOf[$n]]
            if ($f -and $f.items) { foreach ($tk in $f.items.Keys) { if (@($f.items[$tk]) -contains $n) { $planTier = [string]$tk } } }
        }
        if ($null -ne $t -and $null -ne $planTier -and $t -ne $planTier) { Add-Warn 'tier-field-vs-plan' "The Tier field says $t; the plan says $planTier. The field wins." "i$n" $null }
        if ($null -eq $t -and $null -ne $planTier) { $t = $planTier; $src = $(if ($tierItemByNum.ContainsKey($n) -and $tierItemByNum[$n].tier) { 'plan' } else { 'family' }) }
        if ($null -eq $t -and $it.planFamilyLine) {
            $slug = [string]$it.planFamilyLine.slug
            if ($familyBySlug.ContainsKey($slug)) {
                $t = [string]$it.planFamilyLine.tier; $src = 'family-line'
                if (-not $familyOf.ContainsKey($n)) { $familyOf[$n] = $familyBySlug[$slug] }
                $famLineOf[$n] = $familyBySlug[$slug]
            }
            else { Add-Warn 'unknown-family-line' "The body names Tier family '$slug', which no tiers.json family or bodySlug matches." "i$n" $slug }
        }
        if ($null -ne $t) { $tierOf[$n] = $t; $tierSrc[$n] = $src }
        else { $tierSrc[$n] = 'none' }
        # plan language
        if ($tierItemByNum.ContainsKey($n) -and $tierItemByNum[$n].language) { $langOf[$n] = [string]$tierItemByNum[$n].language }
        elseif ($familyOf.ContainsKey($n) -and $famById.Contains($familyOf[$n])) { $langOf[$n] = (& $LANG_OF_FAMILY $famById[$familyOf[$n]]) }
    }
    $INHERITABLE = @('T1', 'T2', 'T3', 'Fix first', 'Prerequisite')
    foreach ($n in $workNums) {
        if ($tierOf.ContainsKey($n)) { continue }
        $cur = $byNum[$n].parent; $guard = 0
        while ($null -ne $cur -and $guard -lt 16) {
            if (& $isContainer ([int]$cur)) { break }
            if ($tierOf.ContainsKey([int]$cur) -and $tierSrc[[int]$cur] -ne 'inherited' -and $INHERITABLE -contains $tierOf[[int]$cur]) {
                $tierOf[$n] = $tierOf[[int]$cur]; $tierSrc[$n] = 'inherited'
                if ($langOf.ContainsKey([int]$cur)) { $langOf[$n] = $langOf[[int]$cur] }
                Add-Warn 'tier-inherited' "No tier of its own; inherits $($tierOf[[int]$cur]) from #$cur." "i$n" "from=#$cur"
                break
            }
            if (-not $byNum.ContainsKey([int]$cur)) { break }
            $cur = $byNum[[int]$cur].parent; $guard++
        }
    }

    Write-BuildMark 'programs'
    # -------------------------------------------------------------- programs
    $fieldProgramItems = @($workNums | Where-Object { $byNum[$_].boardProgram })
    foreach ($g in $gateIssue.Keys) { if ($byNum.ContainsKey([int]$g) -and $byNum[[int]$g].boardProgram) { $fieldProgramItems += [int]$g } }
    $programFieldPresent = ($fieldProgramItems.Count -gt 0)
    $progSets = @{}   # progId -> @{ field; tree; links; carried } HashSet[int]
    foreach ($pr in $programList) { $progSets[[string]$pr.id] = @{ field = [System.Collections.Generic.HashSet[int]]::new(); tree = [System.Collections.Generic.HashSet[int]]::new(); links = [System.Collections.Generic.HashSet[int]]::new(); carried = [System.Collections.Generic.HashSet[int]]::new(); linkCount = 0 } }
    $allNums = @($snapItems | ForEach-Object { [int]$_.number })
    foreach ($n in $allNums) {
        $opt = $byNum[$n].boardProgram
        if (-not $opt) { continue }
        if ($programByOption.ContainsKey([string]$opt)) { [void]$progSets[$programByOption[[string]$opt]].field.Add($n) }
        else { Add-Warn 'program-option-unmatched' "Program option '$opt' names no program in programs.json." "i$n" ([string]$opt) }
    }
    foreach ($pr in $programList) {
        $s = $progSets[[string]$pr.id]
        if ([string]$pr.shape -eq 'tree' -and $pr.ContainsKey('trackingIssue') -and $null -ne $pr.trackingIssue) {
            $tn = [int]$pr.trackingIssue
            if (-not (& $isContainer $tn)) { [void]$s.tree.Add($tn) }
            foreach ($d in (Get-Descendants $tn)) { if (-not (& $isContainer ([int]$d))) { [void]$s.tree.Add([int]$d) } }
        }
        if ([string]$pr.shape -eq 'gate') {
            $g = [int]$pr.gate.issue
            [void]$s.links.Add($g)
            if ($byNum.ContainsKey($g)) {
                foreach ($d in @($byNum[$g].dependencies)) {
                    if ([string]$d.form -ne 'native') { continue }
                    $s.linkCount++
                    [void]$s.links.Add([int]$d.number)
                    foreach ($x in (Get-Descendants ([int]$d.number))) { [void]$s.links.Add([int]$x) }
                }
            }
        }
    }
    if (-not $programFieldPresent) {
        $mapped = [System.Collections.Generic.HashSet[int]]::new()
        foreach ($m in @($tax.mapping)) {
            $mapped.Add([int]$m.number) | Out-Null
            if ($m.ContainsKey('program') -and $m.program) {
                if ($programByOption.ContainsKey([string]$m.program)) { [void]$progSets[$programByOption[[string]$m.program]].carried.Add([int]$m.number) }
                else { Add-Warn 'program-option-unmatched' "Carried Program '$($m.program)' names no program in programs.json." "i$($m.number)" ([string]$m.program) }
            }
        }
        foreach ($pr in $programList) {
            $claim = [System.Collections.Generic.List[int]]::new()
            if ($pr.ContainsKey('trackingIssue') -and $null -ne $pr.trackingIssue) { $claim.Add([int]$pr.trackingIssue) }
            if ($pr.ContainsKey('gate')) { $claim.Add([int]$pr.gate.issue) }
            foreach ($k in 'phases', 'features', 'openFollowUps') {
                if (-not $pr.ContainsKey($k)) { continue }
                foreach ($x in @($pr[$k])) {
                    if ($x.ContainsKey('issue') -and $null -ne $x.issue) { $claim.Add([int]$x.issue) }
                    if ($x.ContainsKey('tasks')) { foreach ($t in @($x.tasks)) { $claim.Add([int]$t) } }
                }
            }
            foreach ($c in $claim) { if (-not $mapped.Contains($c) -and $byNum.ContainsKey($c) -and (-not (& $isContainer $c) -or $gateIssue.ContainsKey($c))) { [void]$progSets[[string]$pr.id].carried.Add($c) } }
        }
    }
    $programsOf = @{}; $programPrimary = @{}; $programSource = @{}
    foreach ($n in $allNums) {
        $list = [System.Collections.Generic.List[string]]::new()
        $primary = $null; $psrc = 'none'
        foreach ($progId in $programOrder) {
            $s = $progSets[$progId]
            if ($s.field.Contains($n)) { $list.Add($progId); if (-not $primary) { $primary = $progId; $psrc = 'field' } }
        }
        if (-not $programFieldPresent) {
            foreach ($progId in $programOrder) { if ($progSets[$progId].carried.Contains($n) -and -not $list.Contains($progId)) { $list.Add($progId); if (-not $primary) { $primary = $progId; $psrc = 'carried' } } }
        }
        foreach ($progId in $programOrder) {
            $s = $progSets[$progId]
            if ($s.tree.Contains($n) -and -not $list.Contains($progId)) { $list.Add($progId); if (-not $primary) { $primary = $progId; $psrc = 'tree' } }
            if ($s.linkCount -gt 0 -and $s.links.Contains($n) -and -not $list.Contains($progId)) { $list.Add($progId); if (-not $primary) { $primary = $progId; $psrc = 'gate-link' } }
        }
        $programsOf[$n] = @($list); $programPrimary[$n] = $primary; $programSource[$n] = $psrc
        if ($psrc -eq 'carried' -and (-not (& $isContainer $n) -or $gateIssue.ContainsKey($n))) { Add-AggWarn 'program-from-carried' "i$n" }
    }
    $programMembership = [ordered]@{}
    foreach ($pr in $programList) {
        $progId = [string]$pr.id; $s = $progSets[$progId]
        $members = [System.Collections.Generic.HashSet[int]]::new()
        foreach ($n in $allNums) { if ((-not (& $isContainer $n) -or $gateIssue.ContainsKey($n)) -and @($programsOf[$n]) -contains $progId) { [void]$members.Add($n) } }
        $entry = [ordered]@{
            id = $progId; name = [string]$pr.name; boardOption = [string]$pr.boardOption; shape = [string]$pr.shape
            source = $(if ($programFieldPresent) { 'field' } else { 'carried' })
            members = @($members | Sort-Object); memberCount = $members.Count
            fieldCount = $s.field.Count; treeCount = $s.tree.Count; carriedCount = $s.carried.Count
        }
        if ($pr.ContainsKey('trackingIssue') -and $null -ne $pr.trackingIssue) { $entry.trackingIssue = [int]$pr.trackingIssue }
        if ([string]$pr.shape -eq 'gate') {
            $g = [int]$pr.gate.issue
            $entry.gate = [ordered]@{ issue = $g; label = [string]$pr.gate.label; linkCount = $s.linkCount; linkSetCount = $s.links.Count }
            $basisSet = if ($programFieldPresent) { $s.field } else { $s.carried }
            if ($s.linkCount -gt 0 -or $programFieldPresent) {
                $onlyField = @($basisSet | Where-Object { -not $s.links.Contains($_) } | Sort-Object)
                $onlyLinks = @($s.links | Where-Object { -not $basisSet.Contains($_) } | Sort-Object)
                $entry.gate.membershipCheck = [ordered]@{ checked = $true; basis = $(if ($programFieldPresent) { 'Program field' } else { 'carried Program values' }); onlyField = $onlyField; onlyLinks = $onlyLinks }
                if ($onlyField.Count -or $onlyLinks.Count) {
                    Add-Warn 'program-membership-mismatch' "$($pr.boardOption): the $($entry.gate.membershipCheck.basis) and #$g's blocked-by links (with their descendants) disagree on $($onlyField.Count + $onlyLinks.Count) items." "i$g" ("only by field: " + (($onlyField | ForEach-Object { "#$_" }) -join ', ') + "; only by link: " + (($onlyLinks | ForEach-Object { "#$_" }) -join ', '))
                }
            }
            else {
                $entry.gate.membershipCheck = [ordered]@{ checked = $false; basis = 'none'; onlyField = @(); onlyLinks = @(); note = "Not checked: no item carries a Program value and #$g has no blocked-by links yet (realignment window W2 creates both). Membership is the carried Program values." }
            }
        }
        $programMembership[$progId] = $entry
    }

    Write-BuildMark 'nodes'
    # ---------------------------------------------------------------- nodes
    $STATUS_BY_BOARD = @{ 'Done' = 'delivered'; 'In review' = 'delivered-unmerged'; 'In progress' = 'in-progress'; 'Ready' = 'ready'; 'Backlog' = 'backlog' }
    $nodes = [ordered]@{}
    function Add-Node { param([hashtable]$N) if ($nodes.Contains($N.id)) { throw "duplicate node $($N.id)" }; $nodes[$N.id] = $N }
    $trackingOf = @{}; foreach ($pr in $programList) { if ([string]$pr.shape -eq 'tree' -and $pr.ContainsKey('trackingIssue') -and $null -ne $pr.trackingIssue) { $trackingOf[[int]$pr.trackingIssue] = [string]$pr.id } }

    foreach ($n in $workNums) {
        $it = $byNum[$n]
        $id = "i$n"
        $children = @(@($it.children) | Where-Object { -not (& $isContainer ([int]$_)) } | ForEach-Object { [int]$_ } | Sort-Object)
        $kind = if ($trackingOf.ContainsKey($n)) { 'program' } elseif ($children.Count -gt 0) { 'feature' } else { 'task' }
        $status = if ($it.boardStatus -and $STATUS_BY_BOARD.ContainsKey([string]$it.boardStatus)) { $STATUS_BY_BOARD[[string]$it.boardStatus] } elseif ($it.state -eq 'CLOSED' -and ($null -eq $it.stateReason -or $it.stateReason -eq 'COMPLETED')) { 'delivered' } else { 'backlog' }
        if ($it.state -eq 'CLOSED' -and $it.stateReason -in @('NOT_PLANNED', 'DUPLICATE')) { $status = 'withdrawn' }
        $delivered = ($status -eq 'delivered' -or $status -eq 'delivered-unmerged')
        $isBug = ((@($it.labels) -contains 'bug') -or [string]$it.issueType -eq 'Bug')
        $node = @{
            id = $id; kind = $kind; number = $n; url = [string]$it.url
            wbs = $(if ($it.wbs) { [string]$it.wbs } else { $null })
            title = $(if ($it.titleWithoutWbs) { [string]$it.titleWithoutWbs } else { [string]$it.title })
            lane = $null; band = $null
            category = $(if ($catOf.ContainsKey($n)) { $catOf[$n] } else { $null }); categorySource = $catSrc[$n]
            epic = $epicOf[$n]
            programs = @($programsOf[$n]); program = $programPrimary[$n]; programSource = $programSource[$n]
            family = $(if ($familyOf.ContainsKey($n)) { $familyOf[$n] } else { $null })
            tier = $(if ($tierOf.ContainsKey($n)) { $tierOf[$n] } else { $null }); tierSource = $tierSrc[$n]
            planLanguage = $(if ($langOf.ContainsKey($n)) { $langOf[$n] } else { $null })
            placement = $null; window = $null; unplacedReason = $null
            stage = -1; stageEnd = -1; stageSource = 'band-floor'; stageExplanation = $null
            status = $status; state = [string]$it.state; stateReason = $it.stateReason; delivered = $delivered
            blocked = $false; untracked = $false
            priority = $(if ("$($it.boardPriority)" -match '^P\d{3}$') { [string]$it.boardPriority } else { $null })
            wave = $(if ("$($it.boardWave)" -match '^W\d{2}$') { [string]$it.boardWave } else { $null })
            area = $(if ($it.boardArea) { [string]$it.boardArea } elseif ($taxByNum.ContainsKey($n) -and $taxByNum[$n].area) { [string]$taxByNum[$n].area } else { $null })
            areaSource = $(if ($it.boardArea) { 'field' } elseif ($taxByNum.ContainsKey($n) -and $taxByNum[$n].area) { 'carried' } else { 'none' })
            boardKind = $it.boardKind
            labels = @($it.labels); issueType = $it.issueType; isBug = $isBug
            parent = $(if ($null -ne $it.parent) { [int]$it.parent } else { $null })
            children = $children
            externalBlockers = @()
            downstreamOpen = 0; onCriticalPath = $false; conflict = $null; pinned = $false; hasOpenDefect = $false; openDefects = 0
            planRank = $null
        }
        $node.shortTitle = Get-ShortTitle $node.title
        if ($tierItemByNum.ContainsKey($n) -and $tierItemByNum[$n].ContainsKey('note') -and $tierItemByNum[$n].note) { $node.note = [string]$tierItemByNum[$n].note }
        if ($null -eq $node.parent) { Add-Warn 'unparented' 'No sub-issue parent: found by its title WBS only.' $id $null }
        if ($node.categorySource -eq 'carried') { Add-AggWarn 'category-from-carried' $id }
        elseif ($node.categorySource -eq 'inherited') { Add-AggWarn 'category-inherited' $id }
        elseif ($node.categorySource -eq 'none') { Add-AggWarn 'uncategorised' $id }
        $ext = [System.Collections.Generic.List[object]]::new()
        foreach ($d in @($it.dependencies)) {
            $t = [int]$d.number
            if ($externalByNum.ContainsKey($t)) {
                $x = $externalByNum[$t]
                if (-not @($ext | Where-Object { $_.number -eq $t })) { $ext.Add([ordered]@{ number = $t; title = [string]$x.title; state = [string]$x.state; stateReason = $x.stateReason; boardStatus = $x.boardStatus; url = [string]$x.url; form = [string]$d.form }) }
            }
        }
        $node.externalBlockers = @($ext)
        Add-Node $node
    }

    # a bug filed under a delivered parent marks that parent (B5: never reopen it)
    foreach ($nd in @($nodes.Values)) {
        if (-not $nd.isBug -or $nd.delivered -or $nd.status -eq 'withdrawn') { continue }
        if ($null -ne $nd.parent -and $nodes.Contains("i$($nd.parent)")) {
            $p = $nodes["i$($nd.parent)"]
            if ($p.status -eq 'delivered' -or $p.status -eq 'delivered-unmerged') { $p.hasOpenDefect = $true; $p.openDefects = $p.openDefects + 1 }
        }
    }

    # untracked program phases (delivered work with no issue)
    foreach ($pr in $programList) {
        if (-not $pr.ContainsKey('phases')) { continue }
        foreach ($ph in @($pr.phases)) {
            if ($ph.ContainsKey('issue') -and $null -ne $ph.issue) { continue }
            $delivered = ($ph.state -eq 'delivered')
            $node = @{
                id = "phase:$($pr.id):$($ph.key)"; kind = 'task'; number = $null; url = $null; wbs = $null
                title = [string]$ph.title; shortTitle = (Get-ShortTitle ([string]$ph.title))
                lane = $null; band = $null; category = $null; categorySource = 'none'; epic = $null
                programs = @([string]$pr.id); program = [string]$pr.id; programSource = 'carried'; family = $null
                tier = $null; tierSource = 'none'; planLanguage = $null
                placement = $null; window = $null; unplacedReason = $null
                stage = -1; stageEnd = -1; stageSource = 'band-floor'; stageExplanation = $null
                status = $(if ($delivered) { 'delivered' } else { 'backlog' }); state = $null; stateReason = $null; delivered = $delivered
                blocked = $false; untracked = $true; priority = $null; wave = $null; area = $null; areaSource = 'none'; boardKind = $null
                labels = @(); issueType = $null; isBug = $false
                parent = $(if ($pr.ContainsKey('trackingIssue') -and $null -ne $pr.trackingIssue) { [int]$pr.trackingIssue } else { $null })
                children = @(); externalBlockers = @()
                downstreamOpen = 0; onCriticalPath = $false; conflict = $null; pinned = $false; hasOpenDefect = $false; openDefects = 0; planRank = $null
            }
            if ($null -ne $node.parent -and $nodes.Contains("i$($node.parent)")) { $node.category = $nodes["i$($node.parent)"].category; $node.categorySource = 'inherited'; $node.epic = $nodes["i$($node.parent)"].epic }
            if ($ph.ContainsKey('evidence') -and $ph.evidence) { $node.evidence = $ph.evidence }
            if ($ph.ContainsKey('note') -and $ph.note) { $node.note = [string]$ph.note }
            Add-Node $node
        }
    }

    Write-BuildMark 'slots'
    # family-tier slots with no filed node. 'Filed' means a NODE carries this family at this tier.
    $LANG_SURFACE = @{ 'SQL' = 'L03.02.02.C01'; 'OQL' = 'L03.02.03.C01'; 'GQL' = 'L03.02.05.C01'; 'Shared' = 'L03.02.01.C07' }
    $slotsByFamilyTier = @{}
    $t2Rank = @{}   # family id -> @{ language; rank; of }
    $t2Families = [ordered]@{}   # language -> List[family id] in plan order
    foreach ($f in @($tiers.families)) {
        if ($FAMILY_ALIAS.ContainsKey([string]$f.id)) { continue }
        if (@($f.tiers) -notcontains 'T2') { continue }
        $lang = (& $LANG_OF_FAMILY $f)
        if (-not $t2Families.Contains($lang)) { $t2Families[$lang] = [System.Collections.Generic.List[string]]::new() }
        $t2Families[$lang].Add([string]$f.id)
    }
    foreach ($lang in $t2Families.Keys) { $i = 0; foreach ($fid in $t2Families[$lang]) { $i++; $t2Rank[$fid] = [ordered]@{ language = $lang; rank = $i; of = $t2Families[$lang].Count } } }
    foreach ($f in @($tiers.families)) {
        if ($FAMILY_ALIAS.ContainsKey([string]$f.id)) { continue }
        foreach ($tier in @($f.tiers)) {
            $filed = @(if ($f.items -and $f.items.ContainsKey($tier)) { $f.items[$tier] })
            $hasNode = $false
            foreach ($x in $nodes.Values) { if ($x.family -eq [string]$f.id -and $x.tier -eq [string]$tier -and $null -ne $x.number) { $hasNode = $true; break } }
            if ($hasNode) { continue }
            $id = "slot:$($f.id):$tier"
            $declaredUnfiled = (@($f.unfiled) -contains $tier)
            $status = if ($tier -eq 'Done') { 'delivered' } elseif ($tier -eq 'Out') { 'out' } else { 'unfiled' }
            $lang = (& $LANG_OF_FAMILY $f)
            $cats = [System.Collections.Generic.List[string]]::new()
            if ($f.items) { foreach ($k in $f.items.Keys) { foreach ($x in @($f.items[$k])) { if ($catOf.ContainsKey([int]$x) -and -not $cats.Contains($catOf[[int]$x])) { $cats.Add($catOf[[int]$x]) } } } }
            $slotCat = if ($cats.Count) { $cats[0] } elseif ($LANG_SURFACE.ContainsKey($lang)) { $LANG_SURFACE[$lang] } else { $null }
            $title = "$($f.name) ($tier)"
            $noteBits = [System.Collections.Generic.List[string]]::new()
            $otherFams = @(@($filed) | ForEach-Object { if ($familyOf.ContainsKey([int]$_)) { $familyOf[[int]$_] } } | Select-Object -Unique)
            $noteBits.Add($(if ($filed.Count) { "The plan files $((@($filed) | ForEach-Object { "#$_" }) -join ', ') here, but those items belong to another family on the board ($($otherFams -join ', ')), so this row has no node of its own. It is drawn as the family's baseline, not as filing backlog." } elseif ($declaredUnfiled) { "No issue exists for this family at $tier; tiers.json marks it unfiled. File it with the body line ``Tier $($tier.Substring(1)) family ``$($f.id)```` and it takes this slot." } else { "No issue exists for this family at $tier, and tiers.json does not list it as unfiled (tier $tier needs no new work)." }))
            if ($f.ContainsKey('add') -and $f.add) { $noteBits.Add("Plan: $($f.add).") }
            if ($f.ContainsKey('notes') -and $f.notes) { $noteBits.Add([string]$f.notes) }
            $node = @{
                id = $id; kind = 'slot'; number = $null; url = $null; wbs = $null
                title = $title; shortTitle = (Get-ShortTitle $title)
                lane = $null; band = $null; category = $slotCat; categorySource = $(if ($cats.Count) { 'family' } elseif ($slotCat) { 'language-surface' } else { 'none' })
                epic = $(if ($slotCat) { ($slotCat.Split('.')[0..2]) -join '.' } else { $null })
                programs = @(); program = $null; programSource = 'none'; family = [string]$f.id
                tier = [string]$tier; tierSource = 'family'; planLanguage = $lang
                placement = $null; window = $null; unplacedReason = $null
                stage = -1; stageEnd = -1; stageSource = 'band-floor'; stageExplanation = $null
                status = $status; state = $null; stateReason = $null; delivered = ($status -eq 'delivered')
                blocked = $false; untracked = $true; priority = $null; wave = $null; area = $null; areaSource = 'none'; boardKind = $null
                labels = @(); issueType = $null; isBug = $false
                parent = $null; children = @(); externalBlockers = @()
                downstreamOpen = 0; onCriticalPath = $false; conflict = $null; pinned = $false; hasOpenDefect = $false; openDefects = 0
                planRank = $null
                note = ($noteBits -join ' ')
            }
            if ($tier -eq 'T2' -and $t2Rank.ContainsKey([string]$f.id)) { $node.planRank = $t2Rank[[string]$f.id] }
            $slotsByFamilyTier["$($f.id)/$tier"] = $id
            Add-Node $node
        }
    }
    foreach ($nd in $nodes.Values) {
        if ($null -ne $nd.number -and $nd.tier -eq 'T2') {
            if ($nd.family -and $t2Rank.ContainsKey([string]$nd.family)) { $nd.planRank = $t2Rank[[string]$nd.family] }
            elseif (@($nd.children).Count -eq 0) { Add-Warn 't2-familyless' 'A T2 item with no plan family cannot take a place in the plan order; it sits at the front of the T2 band.' $nd.id $null }
        }
    }

    # ------------------------------------------------------------- overlay pins
    $applied = [System.Collections.Generic.List[object]]::new()
    $unmatched = [System.Collections.Generic.List[object]]::new()
    foreach ($p in @($overlay.pins)) {
        if (-not $nodes.Contains([string]$p.node)) { $unmatched.Add($p); Add-Warn 'stale-overlay-pin' 'Overlay pin names a node that does not exist.' ([string]$p.node) ([string]$p.field); continue }
        $nd = $nodes[[string]$p.node]
        $field = [string]$p.field
        if ($field -notin @('tier', 'lane', 'category', 'stage')) { Add-Warn 'stale-overlay-pin' 'Overlay pin names a field this script does not apply.' ([string]$p.node) $field; continue }
        if ($field -eq 'tier') { $nd.tier = [string]$p.value; $nd.tierSource = 'overlay' }
        elseif ($field -eq 'lane') { $nd.pinnedLane = [string]$p.value }
        elseif ($field -eq 'category') { $nd.category = [string]$p.value; $nd.categorySource = 'overlay' }
        else { $nd.pinnedStage = [int]$p.value }
        $nd.pinned = $true
        $applied.Add([ordered]@{ node = [string]$p.node; field = [string]$p.field; value = $p.value; reason = [string]$p.reason })
    }

    # ------------------------------------------------------------------ lanes
    $laneDefs = @($overlay.lanes)
    $LANE_ORDER = @($laneDefs | ForEach-Object { [string]$_.id })
    if ($LANE_ORDER -notcontains 'other') { throw "overlay.json lanes must include an 'other' lane." }
    $laneByLanguage = @{}; $laneByCategory = @{}; $laneByEpic = @{}
    foreach ($l in $laneDefs) {
        if ($l.ContainsKey('planLanguage') -and $l.planLanguage) { $laneByLanguage[[string]$l.planLanguage] = [string]$l.id }
        if ($l.ContainsKey('categories')) { foreach ($c in @($l.categories)) { $laneByCategory[[string]$c] = [string]$l.id } }
        if ($l.ContainsKey('epics')) { foreach ($e in @($l.epics)) { $laneByEpic[[string]$e] = [string]$l.id } }
    }
    $epicsWithoutLane = [System.Collections.Generic.HashSet[string]]::new()
    function Get-CategoryLane { param($Code)
        if (-not $Code) { return $null }
        if ($laneByCategory.ContainsKey([string]$Code)) { return $laneByCategory[[string]$Code] }
        $e = ([string]$Code).Split('.')[0..2] -join '.'
        if ($laneByEpic.ContainsKey($e)) { return $laneByEpic[$e] }
        return $null
    }
    foreach ($nd in @($nodes.Values)) {
        $lane = $null
        $tiered = ($nd.tierSource -in @('field', 'plan', 'family', 'family-line', 'inherited', 'overlay'))
        if ($nd.ContainsKey('pinnedLane')) { $lane = $nd.pinnedLane }
        elseif ($tiered -and $nd.planLanguage -and $laneByLanguage.ContainsKey([string]$nd.planLanguage)) { $lane = $laneByLanguage[[string]$nd.planLanguage] }
        elseif ($nd.kind -eq 'slot' -and $nd.planLanguage -and $laneByLanguage.ContainsKey([string]$nd.planLanguage)) { $lane = $laneByLanguage[[string]$nd.planLanguage] }
        if (-not $lane) { $lane = Get-CategoryLane $nd.category }
        if (-not $lane -and $nd.epic -and $laneByEpic.ContainsKey([string]$nd.epic)) { $lane = $laneByEpic[[string]$nd.epic] }
        if (-not $lane) {
            $lane = 'other'
            if ($nd.epic) { [void]$epicsWithoutLane.Add([string]$nd.epic) }
        }
        $nd.lane = $lane
    }
    foreach ($nd in @($nodes.Values)) { if ($nd.id -like 'phase:*' -and $null -ne $nd.parent -and $nodes.Contains("i$($nd.parent)")) { $nd.lane = $nodes["i$($nd.parent)"].lane } }
    foreach ($e in ($epicsWithoutLane | Sort-Ordinal)) { Add-Warn 'epic-without-lane' "Epic $e has no lane in overlay.json; its items are drawn in Other." $null $e }
    foreach ($e in $epicByWbs.Keys) { if (-not $laneByEpic.ContainsKey($e)) { Add-Warn 'epic-without-lane' "Epic $e (#$($epicByWbs[$e])) has no lane in overlay.json." $null $e } }

    Write-BuildMark 'edges'
    # ------------------------------------------------------------------ edges
    $edges = [System.Collections.Generic.List[hashtable]]::new()
    $edgeKeys = @{}
    $edgesIgnored = [ordered]@{ container = 0; external = 0; unresolved = 0; self = 0 }
    function Add-Edge {
        param([string]$From, [string]$To, [string]$Kind, [string]$Source, $Note, [string]$InheritedFrom)
        if ($From -eq $To) { $edgesIgnored.self++; return }
        if (-not $nodes.Contains($From) -or -not $nodes.Contains($To)) {
            Add-Warn 'unresolved-dependency' 'Edge dropped: an endpoint is not a node.' $null "$From -> $To ($Kind/$Source)"
            $edgesIgnored.unresolved++
            return
        }
        $k = "$From>$To>$Kind"
        if ($edgeKeys.ContainsKey($k)) {
            $e0 = $edgeKeys[$k]
            if ($e0.source -ne $Source -and @($e0.alsoFrom) -notcontains $Source) { $e0.alsoFrom = @(@($e0.alsoFrom) + $Source) }
            if ($Note -and -not $e0.ContainsKey('note')) { $e0.note = [string]$Note }
            return
        }
        $e = @{ from = $From; to = $To; kind = $Kind; source = $Source; alsoFrom = @() }
        if ($Note) { $e.note = [string]$Note }
        if ($InheritedFrom) { $e.inheritedFrom = $InheritedFrom }
        $edges.Add($e); $edgeKeys[$k] = $e
    }
    $xlangNote = @{}
    foreach ($e in @($tiers.crossLanguageEdges)) { $xlangNote["$($e.from)->$($e.to)"] = [string]$e.note }
    $FORM_SOURCE = @{ 'native' = 'native'; 'section' = 'body:section'; 'section-wbs' = 'body:section'; 'line' = 'body:line'; 'related' = 'body:related' }
    $gateLinkEdges = [System.Collections.Generic.List[object]]::new()
    foreach ($it in ($snapItems | Sort-Object { [int]$_.number })) {
        $from = [int]$it.number
        foreach ($d in (@($it.dependencies) | Sort-Object @{ Expression = { $script:FORM_RANK[[string]$_.form] } })) {
            $to = [int]$d.number
            if ($gateIssue.ContainsKey($from) -and [string]$d.form -eq 'native') { $gateLinkEdges.Add(@{ gate = $from; member = $to }); continue }
            if ($externalByNum.ContainsKey($to)) { $edgesIgnored.external++; continue }
            if ((& $isContainer $from) -or (& $isContainer $to)) { $edgesIgnored.container++; continue }
            Add-Edge "i$from" "i$to" 'depends-on' $FORM_SOURCE[[string]$d.form] $xlangNote["$from->$to"]
        }
    }
    $progByTracking = @{}; foreach ($pr in $programList) { if ($pr.ContainsKey('trackingIssue') -and $null -ne $pr.trackingIssue) { $progByTracking[[int]$pr.trackingIssue] = [string]$pr.id } }
    function Get-PhaseNodeId {
        param([string]$ProgId, [string]$Key)
        if (-not $ProgId -or -not $programById.Contains($ProgId)) { return $null }
        $pr = $programById[$ProgId]
        if (-not $pr.ContainsKey('phases')) { return $null }
        foreach ($ph in @($pr.phases)) {
            if ([string]$ph.key -eq $Key) {
                if ($ph.ContainsKey('issue') -and $null -ne $ph.issue) { return "i$($ph.issue)" }
                return "phase:${ProgId}:$($ph.key)"
            }
        }
        return $null
    }
    foreach ($pr in $programList) {
        if (-not $pr.ContainsKey('phases')) { continue }
        foreach ($ph in @($pr.phases)) {
            $fromId = if ($ph.ContainsKey('issue') -and $null -ne $ph.issue) { "i$($ph.issue)" } else { "phase:$($pr.id):$($ph.key)" }
            foreach ($dep in @($ph.dependsOn)) {
                $toId = $null
                if ($dep -match '^#(\d+)$') { $toId = "i$($Matches[1])" }
                elseif ($dep -match '^(\S+)\s+of\s+#(\d+)$') {
                    $k = $Matches[1]; $tn = [int]$Matches[2]
                    $toId = Get-PhaseNodeId -ProgId $(if ($progByTracking.ContainsKey($tn)) { $progByTracking[$tn] } else { $null }) -Key $k
                }
                else {
                    $toId = Get-PhaseNodeId -ProgId ([string]$pr.id) -Key ([string]$dep)
                    if (-not $toId) { foreach ($other in $programList) { $c = Get-PhaseNodeId -ProgId ([string]$other.id) -Key ([string]$dep); if ($c) { $toId = $c; break } } }
                }
                if (-not $toId) { Add-Warn 'unresolved-dependency' 'Program phase dependency could not be resolved.' $fromId "$($pr.id) $($ph.key) dependsOn `"$dep`""; continue }
                if (-not $nodes.Contains($fromId) -or -not $nodes.Contains($toId)) { $edgesIgnored.container++; continue }
                Add-Edge $fromId $toId 'depends-on' 'program-phase' $null
            }
        }
    }
    $nativePairs = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($e in $edges) { if ($e.source -eq 'native' -or @($e.alsoFrom) -contains 'native') { [void]$nativePairs.Add("$($e.from)>$($e.to)") } }
    foreach ($d in @($tiers.dependencies)) {
        Add-Edge "i$($d.from)" "i$($d.to)" 'plan-order' ([string]$d.source) $xlangNote["$($d.from)->$($d.to)"]
        if (([string]$d.source -like 'owner-*' -or [string]$d.source -eq 'plan-D5') -and $nativePairs.Contains("i$($d.from)>i$($d.to)")) {
            Add-Warn 'decision-edge-on-board' "tiers.json's $($d.source) edge #$($d.from) -> #$($d.to) now also exists as a native blocked-by link; the carried edge can be deleted." "i$($d.from)" "#$($d.from) blocked by #$($d.to)"
        }
    }
    # plan-sequence (D4 / D4a): positions T2 work, never blocks it.
    $t2Mode = if ($tiers.tierOrder -and $tiers.tierOrder.T2) { [string]$tiers.tierOrder.T2.mode } else { 'per-language' }
    $planSequenceCount = 0
    function Get-T2Anchors { param([string]$Fid)
        $filed = @($nodes.Values | Where-Object { $_.family -eq $Fid -and $_.tier -eq 'T2' -and $null -ne $_.number -and @($_.children).Count -eq 0 } | ForEach-Object { $_.id } | Sort-Ordinal)
        if ($filed.Count) { return , $filed }
        $slot = "slot:${Fid}:T2"
        if ($nodes.Contains($slot)) { return , @($slot) }
        return , @()
    }
    if ($t2Mode -ne 'none') {
        $sequences = [System.Collections.Generic.List[object]]::new()
        if ($t2Mode -eq 'global') { $g = [System.Collections.Generic.List[string]]::new(); foreach ($l in $t2Families.Keys) { foreach ($x in $t2Families[$l]) { $g.Add($x) } }; $sequences.Add($g) }
        else { foreach ($l in $t2Families.Keys) { $sequences.Add($t2Families[$l]) } }
        foreach ($seq in $sequences) {
            for ($i = 1; $i -lt $seq.Count; $i++) {
                foreach ($a in (Get-T2Anchors $seq[$i])) { foreach ($b in (Get-T2Anchors $seq[$i - 1])) { Add-Edge $a $b 'plan-sequence' 'plan-D4' $null; $planSequenceCount++ } }
            }
        }
    }
    foreach ($g in @($overlay.programGateEdges)) { Add-Edge "i$($g.from)" "i$($g.to)" 'program-gate' 'overlay:tierRelation' $g.note }

    # inherited: an edge from a node with children to a node without children applies to every descendant
    $INHERIT_KINDS = @('depends-on', 'plan-order', 'program-gate')
    $inheritedCount = 0
    foreach ($e in @($edges.ToArray())) {
        if ($INHERIT_KINDS -notcontains $e.kind) { continue }
        $f = $nodes[$e.from]; $t = $nodes[$e.to]
        if (@($f.children).Count -eq 0 -or @($t.children).Count -gt 0 -or $null -eq $f.number) { continue }
        foreach ($d in (Get-Descendants ([int]$f.number))) {
            $did = "i$d"
            if (-not $nodes.Contains($did) -or $did -eq $e.to) { continue }
            $before = $edges.Count
            Add-Edge $did $e.to $e.kind "inherited:$($e.source)" $null $e.from
            if ($edges.Count -gt $before) { $inheritedCount++ }
        }
    }
    foreach ($nd in @($nodes.Values)) { if ($null -ne $nd.parent -and $nodes.Contains("i$($nd.parent)")) { Add-Edge $nd.id "i$($nd.parent)" 'child-of' 'board-tree' $null } }

    $conflicts = [System.Collections.Generic.List[object]]::new()
    foreach ($c in @($tiers.conflicts)) {
        $cid = 'conflict:' + $(if ($c.ContainsKey('family') -and $c.family) { [string]$c.family } else { "issue-$($c.issue)" })
        $claimants = [System.Collections.Generic.List[string]]::new()
        $claimants.Add("i$($c.issue)")
        if ($c.ContainsKey('family') -and $c.family) {
            $f = $famById[[string]$c.family]
            if ($f -and $f.items) { foreach ($tk in $f.items.Keys) { foreach ($x in @($f.items[$tk])) { if ([int]$x -ne [int]$c.issue) { $claimants.Add("i$x") } } } }
            if ($slotsByFamilyTier.ContainsKey("$($c.family)/$($c.tier)")) { $claimants.Add($slotsByFamilyTier["$($c.family)/$($c.tier)"]) }
        }
        $uniq = @($claimants | Select-Object -Unique | Where-Object { $nodes.Contains($_) })
        $entry = [ordered]@{ id = $cid; family = $(if ($c.ContainsKey('family')) { $c.family } else { $null }); tier = [string]$c.tier; nodes = $uniq; resolution = $null }
        if ($c.ContainsKey('note') -and $c.note) { $entry.note = [string]$c.note }
        $conflicts.Add($entry)
        if ($nodes.Contains("i$($c.issue)")) { $nodes["i$($c.issue)"].conflict = $cid }
        foreach ($other in $uniq) { if ($other -ne "i$($c.issue)") { Add-Edge "i$($c.issue)" $other 'conflict' 'tiers.conflicts' $c.note } }
    }

    Write-BuildMark 'positioning graph'
    # ------------------------------------------------------ positioning graph
    $POSITIONING = @('depends-on', 'plan-order', 'plan-sequence')
    $BLOCKING = @('depends-on', 'plan-order', 'program-gate')
    $isLeaf = { param($nd) @($nd.children).Count -eq 0 }
    $leafDescCache = @{}
    function Get-LeafTargets { param([string]$Id)
        if ($leafDescCache.ContainsKey($Id)) { return , $leafDescCache[$Id] }
        $nd = $nodes[$Id]
        $out = @()
        if (@($nd.children).Count -eq 0 -or $null -eq $nd.number) { $out = @($Id) }
        else { $out = @((Get-Descendants ([int]$nd.number)) | ForEach-Object { "i$_" } | Where-Object { $nodes.Contains($_) -and @($nodes[$_].children).Count -eq 0 }) }
        $leafDescCache[$Id] = $out
        return , $out
    }
    # leaf-level positioning predecessors: an edge into a feature waits on all its leaves
    $posPreds = @{}; $posSuccs = @{}
    foreach ($k in $nodes.Keys) { $posPreds[$k] = [System.Collections.Generic.List[object]]::new(); $posSuccs[$k] = [System.Collections.Generic.List[object]]::new() }
    foreach ($e in $edges) {
        if ($POSITIONING -notcontains $e.kind) { continue }
        if (-not (& $isLeaf $nodes[$e.from])) { continue }
        foreach ($t in (Get-LeafTargets $e.to)) {
            if ($t -eq $e.from) { continue }
            $posPreds[$e.from].Add(@{ id = $t; kind = $e.kind; edge = $e })
            $posSuccs[$t].Add(@{ id = $e.from; kind = $e.kind; edge = $e })
        }
    }

    Write-BuildMark 'layout'
    # ---------------------------------------------------------------- layout
    $BAND_ORDER = @('landed', 'gate', 't1', 't2', 't3', 'unplaced')
    $POSITIONED_BANDS = @('gate', 't1', 't2', 't3')
    $NEXT_BAND = @{ landed = 'gate'; gate = 't1'; t1 = 't2'; t2 = 't3'; t3 = 't3' }
    $TIER_BAND = @{ 'T1' = 't1'; 'T2' = 't2'; 'T3' = 't3'; 'Fix first' = 'gate'; 'Prerequisite' = 'gate' }
    $PLAN_TIER_SOURCES = @('field', 'plan', 'family', 'family-line', 'overlay')
    $waveValues = if ($projectFields.Contains('Wave')) { @($projectFields['Wave']) } else { @('W01', 'W02', 'W03', 'W04', 'W05', 'W06') }
    $priorityValues = if ($projectFields.Contains('Priority')) { @($projectFields['Priority']) } else { @('P001', 'P002', 'P003', 'P004', 'P005', 'P006', 'P007') }
    $windowPins = if ($overlay.ContainsKey('waveWindows') -and $overlay.waveWindows) { $overlay.waveWindows } else { @{} }

    # base band: what the board and the plan say, before any window or pull
    $baseBand = @{}
    foreach ($nd in $nodes.Values) {
        $b = $null
        if ($nd.status -eq 'withdrawn') { $b = 'withdrawn' }
        elseif ($nd.tier -eq 'Out') { $b = 'out' }
        elseif ($nd.tier -eq 'Done') { $b = 'landed' }
        elseif ($nd.delivered -and ($PLAN_TIER_SOURCES -notcontains $nd.tierSource)) { $b = 'landed' }
        elseif ($nd.tier -and $TIER_BAND.ContainsKey([string]$nd.tier)) { $b = $TIER_BAND[[string]$nd.tier] }
        elseif ($nd.delivered) { $b = 'landed' }
        else { $b = 'untiered' }
        $baseBand[$nd.id] = $b
    }

    function Invoke-Layout {
        param([hashtable]$Pulled)
        $LAY = @{ band = @{}; depth = @{}; via = @{}; viaKind = @{}; span = @{}; offset = @{}; stage = @{}; stageEnd = @{}; placement = @{}; window = @{}; floorVia = @{}; unplacedReason = @{} }
        foreach ($nd in $nodes.Values) {
            $b = $baseBand[$nd.id]
            if ($Pulled.ContainsKey($nd.id)) { $b = $Pulled[$nd.id] }
            $LAY.band[$nd.id] = $b
        }
        # in-band local depth over leaves
        foreach ($band in $POSITIONED_BANDS) {
            $members = @($nodes.Values | Where-Object { $LAY.band[$_.id] -eq $band -and @($_.children).Count -eq 0 } | ForEach-Object { $_.id } | Sort-Ordinal)
            if (-not $members.Count) { continue }
            $set = [System.Collections.Generic.HashSet[string]]::new([string[]]$members)
            $preds = @{}; $succ = @{}
            foreach ($m in $members) { $preds[$m] = [System.Collections.Generic.List[object]]::new(); $succ[$m] = [System.Collections.Generic.List[string]]::new() }
            foreach ($m in $members) {
                foreach ($p in $posPreds[$m]) {
                    if (-not $set.Contains($p.id)) { continue }
                    $preds[$m].Add($p); $succ[$p.id].Add($m)
                }
            }
            $indeg = @{}; foreach ($m in $members) { $indeg[$m] = $preds[$m].Count }
            $queue = [System.Collections.Generic.List[string]]::new()
            foreach ($m in $members) { if ($indeg[$m] -eq 0) { $queue.Add($m) } }
            $queue.Sort([System.StringComparer]::Ordinal)
            $order = [System.Collections.Generic.List[string]]::new()
            while ($queue.Count) {
                $m = $queue[0]; $queue.RemoveAt(0)
                $order.Add($m)
                foreach ($s in @($succ[$m] | Sort-Ordinal)) { $indeg[$s] = $indeg[$s] - 1; if ($indeg[$s] -eq 0) { $queue.Add($s) } }
                $queue.Sort([System.StringComparer]::Ordinal)
            }
            if ($order.Count -ne $members.Count) {
                $stuck = @($members | Where-Object { -not $order.Contains($_) })
                if (-not $LAY.ContainsKey('cycles')) { $LAY.cycles = @{} }
                $LAY.cycles[$band] = $stuck
                foreach ($m in $stuck) { $order.Add($m) }
            }
            foreach ($m in $order) {
                $d = 0; $via = $null; $viaKind = $null
                foreach ($p in ($preds[$m] | Sort-Object @{ Expression = { $_.id } })) {
                    if (-not $LAY.depth.ContainsKey($p.id)) { continue }
                    $cand = $LAY.depth[$p.id] + 1
                    if ($cand -gt $d -or ($cand -eq $d -and $viaKind -eq 'plan-sequence' -and $p.kind -ne 'plan-sequence')) { $d = $cand; $via = $p.id; $viaKind = $p.kind }
                }
                $LAY.depth[$m] = $d
                if ($via) { $LAY.via[$m] = $via; $LAY.viaKind[$m] = $viaKind }
            }
        }
        $LAY.span.landed = 1
        foreach ($band in $POSITIONED_BANDS) {
            $max = -1
            foreach ($k in $LAY.depth.Keys) { if ($LAY.band[$k] -eq $band -and $LAY.depth[$k] -gt $max) { $max = $LAY.depth[$k] } }
            $LAY.span[$band] = $max + 1
        }
        $LAY.span.unplaced = 0
        $off = 0
        foreach ($b in $BAND_ORDER) { $LAY.offset[$b] = $off; $off += $LAY.span[$b] }
        $lastPositioned = $LAY.offset.unplaced - 1
        $LAY.lastPositioned = $lastPositioned
        $bandOfStage = { param([int]$s) $r = 'landed'; foreach ($b in $BAND_ORDER) { if ($b -eq 'unplaced') { continue }; if ($LAY.span[$b] -gt 0 -and $s -ge $LAY.offset[$b]) { $r = $b } }; return $r }
        $LAY.bandOfStage = $bandOfStage

        # tiered leaves, pulled leaves and landed nodes
        foreach ($nd in $nodes.Values) {
            $b = $LAY.band[$nd.id]
            if ($b -eq 'landed') { $LAY.stage[$nd.id] = 0; $LAY.stageEnd[$nd.id] = 0; $LAY.placement[$nd.id] = 'landed'; continue }
            if ($b -eq 'withdrawn') { $LAY.placement[$nd.id] = 'withdrawn'; continue }
            if ($b -eq 'out') { $LAY.placement[$nd.id] = 'out'; continue }
            if ($POSITIONED_BANDS -contains $b -and $LAY.depth.ContainsKey($nd.id)) {
                $LAY.stage[$nd.id] = $LAY.offset[$b] + $LAY.depth[$nd.id]; $LAY.stageEnd[$nd.id] = $LAY.stage[$nd.id]
                $LAY.placement[$nd.id] = $(if ($Pulled.ContainsKey($nd.id)) { 'prerequisite' } elseif ($nd.tierSource -eq 'inherited') { 'tier-inherited' } else { 'tier' })
            }
        }

        # windows, calibrated on open, undelivered, tiered leaves (gate..t3); pulled items excluded
        $LAY.windows = [ordered]@{ wave = [ordered]@{}; priority = [ordered]@{} }
        foreach ($spec in @(@{ field = 'wave'; values = $waveValues }, @{ field = 'priority'; values = $priorityValues })) {
            $prev = $null
            foreach ($v in $spec.values) {
                $pts = [System.Collections.Generic.List[int]]::new()
                foreach ($nd in $nodes.Values) {
                    if ($nd[$spec.field] -ne $v -or $null -eq $nd.number -or @($nd.children).Count -gt 0) { continue }
                    if ($nd.delivered -or $nd.state -ne 'OPEN' -or $Pulled.ContainsKey($nd.id)) { continue }
                    if ($POSITIONED_BANDS -notcontains $LAY.band[$nd.id] -or -not $LAY.stage.ContainsKey($nd.id)) { continue }
                    $pts.Add([int]$LAY.stage[$nd.id])
                }
                $lo = 0; $hi = 0; $basis = ''; $dropped = @(); $pinned = $false
                if ($windowPins.Contains($v)) {
                    $bands = @($windowPins[$v].bands | Where-Object { $LAY.span[$_] -gt 0 })
                    if ($bands.Count) {
                        $lo = (@($bands | ForEach-Object { $LAY.offset[$_] }) | Measure-Object -Minimum).Minimum
                        $hi = (@($bands | ForEach-Object { $LAY.offset[$_] + $LAY.span[$_] - 1 }) | Measure-Object -Maximum).Maximum
                        $basis = "pinned by overlay.waveWindows ($($bands -join ', ')): $($windowPins[$v].reason)"; $pinned = $true
                    }
                }
                if (-not $pinned) {
                    if ($pts.Count -ge 3) {
                        $byBand = [ordered]@{}
                        foreach ($p in $pts) { $bb = & $bandOfStage $p; if (-not $byBand.Contains($bb)) { $byBand[$bb] = [System.Collections.Generic.List[int]]::new() }; $byBand[$bb].Add($p) }
                        $kept = [System.Collections.Generic.List[int]]::new(); $dl = [System.Collections.Generic.List[string]]::new()
                        foreach ($bb in $byBand.Keys) { if ($byBand[$bb].Count / $pts.Count -ge 0.10) { foreach ($p in $byBand[$bb]) { $kept.Add($p) } } else { $dl.Add("$($bb):$($byBand[$bb].Count)") } }
                        $lo = ($kept | Measure-Object -Minimum).Minimum; $hi = ($kept | Measure-Object -Maximum).Maximum
                        $dropped = @($dl)
                        $basis = "$($kept.Count) of $($pts.Count) open tiered $v items" + $(if ($dl.Count) { "; dropped $($dl -join ', ')" } else { '' })
                    }
                    elseif ($pts.Count -ge 1) {
                        $bs = @($pts | ForEach-Object { & $bandOfStage $_ })
                        $b0 = @($BAND_ORDER | Where-Object { $bs -contains $_ })[0]
                        $b1 = @($BAND_ORDER | Where-Object { $bs -contains $_ })[-1]
                        $lo = $LAY.offset[$b0]; $hi = $LAY.offset[$b1] + $LAY.span[$b1] - 1
                        $basis = "the whole of the band(s) its $($pts.Count) open tiered $v item(s) sit in"
                    }
                    else {
                        $pb = if ($prev) { & $bandOfStage ([int]$prev.hi) } else { 'landed' }
                        $nb = $NEXT_BAND[$pb]
                        $guard = 0
                        while ($LAY.span[$nb] -eq 0 -and $nb -ne 't3' -and $guard -lt 5) { $nb = $NEXT_BAND[$nb]; $guard++ }
                        if ($LAY.span[$nb] -gt 0) { $lo = $LAY.offset[$nb]; $hi = $LAY.offset[$nb] + $LAY.span[$nb] - 1 } else { $lo = $lastPositioned; $hi = $lastPositioned }
                        $basis = "no open tiered $v items: the band after " + $(if ($prev) { "$($prev.value)'s window" } else { 'Landed' })
                    }
                }
                if ($prev -and $lo -lt [int]$prev.lo) { $lo = [int]$prev.lo; $basis += "; starts no earlier than $($prev.value)" }
                if ($hi -gt $lastPositioned) { $hi = $lastPositioned }
                if ($lo -gt $lastPositioned) { $lo = $lastPositioned }
                if ($hi -lt $lo) { $hi = $lo }
                $w = [ordered]@{ value = $v; field = $spec.field; lo = [int]$lo; hi = [int]$hi; basis = $basis; n = $pts.Count; droppedBands = @($dropped); pinned = $pinned }
                $LAY.windows[$spec.field][$v] = $w
                $prev = $w
            }
        }

        # the placement ladder for untiered, undelivered, not withdrawn leaves, predecessors first
        $cands = @($nodes.Values | Where-Object { $LAY.band[$_.id] -eq 'untiered' -and @($_.children).Count -eq 0 } | ForEach-Object { $_.id } | Sort-Ordinal)
        $candSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$cands)
        $visited = @{}; $topo = [System.Collections.Generic.List[string]]::new()
        $visit = $null
        $visit = {
            param([string]$Id, [int]$Depth)
            if ($visited.ContainsKey($Id) -or $Depth -gt 400) { return }
            $visited[$Id] = $true
            foreach ($p in ($posPreds[$Id] | Sort-Object @{ Expression = { $_.id } })) { if ($candSet.Contains($p.id)) { & $visit $p.id ($Depth + 1) } }
            $topo.Add($Id)
        }
        foreach ($c in $cands) { & $visit $c 0 }
        foreach ($id in $topo) {
            $nd = $nodes[$id]
            $floor = -1; $floorVia = $null
            foreach ($p in $posPreds[$id]) {
                if ($p.kind -eq 'plan-sequence') { continue }
                $pn = $nodes[$p.id]
                if ($pn.delivered -or $pn.status -eq 'withdrawn') { continue }
                if (-not $LAY.stageEnd.ContainsKey($p.id) -or $LAY.placement[$p.id] -eq 'unplaced') { continue }
                if ($LAY.stageEnd[$p.id] + 1 -gt $floor) { $floor = $LAY.stageEnd[$p.id] + 1; $floorVia = $p.id }
            }
            if ($floor -gt $lastPositioned) { $floor = $lastPositioned }
            $win = $null
            if ($nd.wave -and $LAY.windows.wave.Contains([string]$nd.wave)) { $win = $LAY.windows.wave[[string]$nd.wave] }
            elseif ($nd.priority -and $LAY.windows.priority.Contains([string]$nd.priority)) { $win = $LAY.windows.priority[[string]$nd.priority] }
            if ($win) {
                $lo = [int]$win.lo; $hi = [int]$win.hi
                if ($floor -ge 0 -and $lo -lt $floor) { $lo = $floor }
                if ($hi -lt $lo) { $hi = $lo }
                $LAY.stage[$id] = $lo; $LAY.stageEnd[$id] = $hi; $LAY.placement[$id] = [string]$win.field; $LAY.window[$id] = $win
                if ($floorVia) { $LAY.floorVia[$id] = $floorVia }
            }
            elseif ($floor -ge 0) {
                $LAY.stage[$id] = $floor; $LAY.stageEnd[$id] = $floor; $LAY.placement[$id] = 'dependency'; $LAY.floorVia[$id] = $floorVia
            }
            else {
                $LAY.placement[$id] = 'unplaced'; $LAY.unplacedReason[$id] = 'no-tier-wave-priority-or-dependency'
            }
        }
        return $LAY
    }

    $pulled = @{}
    $LAY = Invoke-Layout -Pulled $pulled
    # Prerequisite pull: an untiered open item that open tiered work waits on (directly, or through
    # other untiered open items) and that its own window would draw at or after that work joins the
    # band of the earliest such work, ahead of it. Decided once, from the first layout.
    $pullWhy = @{}
    foreach ($nd in ($nodes.Values | Sort-Object { $_.id })) {
        if ($LAY.band[$nd.id] -ne 'untiered' -or @($nd.children).Count -gt 0) { continue }
        $tieredSuccs = [System.Collections.Generic.List[string]]::new()
        $seen = [System.Collections.Generic.HashSet[string]]::new()
        $stack = [System.Collections.Generic.Stack[string]]::new()
        foreach ($s in $posSuccs[$nd.id]) { if ($s.kind -ne 'plan-sequence') { $stack.Push($s.id) } }
        while ($stack.Count) {
            $x = $stack.Pop()
            if (-not $seen.Add($x)) { continue }
            $xn = $nodes[$x]
            if ($xn.delivered -or $xn.status -eq 'withdrawn') { continue }
            if ($POSITIONED_BANDS -contains $LAY.band[$x] -and $LAY.stage.ContainsKey($x)) { $tieredSuccs.Add($x); continue }
            if ($LAY.band[$x] -eq 'untiered') { foreach ($s in $posSuccs[$x]) { if ($s.kind -ne 'plan-sequence') { $stack.Push($s.id) } } }
        }
        if (-not $tieredSuccs.Count) { continue }
        $minStage = (@($tieredSuccs | ForEach-Object { $LAY.stage[$_] }) | Measure-Object -Minimum).Minimum
        $own = if ($LAY.placement[$nd.id] -eq 'unplaced' -or -not $LAY.stage.ContainsKey($nd.id)) { [int]::MaxValue } else { [int]$LAY.stage[$nd.id] }
        if ($own -lt $minStage) { continue }
        $earliest = @($tieredSuccs | Sort-Object @{ Expression = { $BAND_ORDER.IndexOf($LAY.band[$_]) } }, @{ Expression = { $LAY.stage[$_] } }, @{ Expression = { $_ } })[0]
        $pulled[$nd.id] = $LAY.band[$earliest]
        $pullWhy[$nd.id] = @{ succs = @($tieredSuccs | Sort-Object @{ Expression = { $LAY.stage[$_] } }, @{ Expression = { $_ } }); own = $(if ($own -eq [int]::MaxValue) { $null } else { $own }); ownPlacement = $LAY.placement[$nd.id]; ownWindow = $LAY.window[$nd.id]; minStage = $minStage }
    }
    if ($pulled.Count) { $LAY = Invoke-Layout -Pulled $pulled }
    if ($LAY.ContainsKey('cycles')) { foreach ($b in $LAY.cycles.Keys) { Add-Warn 'cycle' "Dependency cycle inside band $b; the cycle members were positioned at the band floor." $null (($LAY.cycles[$b]) -join ', ') } }

    Write-BuildMark 'write layout'
    # write the layout onto the leaves
    function Get-Label { param([string]$Id) $x = $nodes[$Id]; if ($x -and $null -ne $x.number) { return "#$($x.number)" }; if ($x -and $x.kind -eq 'slot') { return "the unfiled '$($x.title)' slot" }; return $Id }
    $directBlockers = @{}
    foreach ($e in $edges) {
        if ($BLOCKING -notcontains $e.kind) { continue }
        if (-not $directBlockers.ContainsKey($e.from)) { $directBlockers[$e.from] = [System.Collections.Generic.List[string]]::new() }
        if (-not $directBlockers[$e.from].Contains($e.to)) { $directBlockers[$e.from].Add($e.to) }
    }
    foreach ($nd in $nodes.Values) {
        if (@($nd.children).Count -gt 0) { continue }
        $id = $nd.id
        $nd.band = $LAY.band[$id]
        $nd.placement = $LAY.placement[$id]
        if ($LAY.stage.ContainsKey($id)) { $nd.stage = [int]$LAY.stage[$id]; $nd.stageEnd = [int]$LAY.stageEnd[$id] }
        switch ($nd.placement) {
            'withdrawn' { $nd.band = 'withdrawn'; $nd.stageSource = 'none'; $nd.stageExplanation = "Closed as $(if ($nd.stateReason) { ([string]$nd.stateReason).ToLowerInvariant().Replace('_', ' ') } else { 'not planned' }): it delivered nothing, so it is drawn in the footer and never counted as Done." }
            'out' { $nd.band = 'out'; $nd.stageSource = 'none'; $nd.stageExplanation = 'Rejected by a recorded decision; drawn in the footer so it cannot be rediscovered and refiled.' }
            'landed' { $nd.stageSource = 'band-floor'; $nd.stageExplanation = 'Landed: delivered work, drawn in one column. Ordering inside this column is history.' }
            'unplaced' { $nd.band = 'unplaced'; $nd.stageSource = 'none'; $nd.unplacedReason = $LAY.unplacedReason[$id] }
            { $_ -in @('wave', 'priority') } {
                $w = $LAY.window[$id]; $nd.window = [ordered]@{ field = [string]$w.field; value = [string]$w.value; lo = [int]$w.lo; hi = [int]$w.hi }
                $nd.band = & $LAY.bandOfStage $nd.stage
                $nd.stageSource = 'window'
                $what = if ($nd.placement -eq 'wave') { "$($w.value) window" } else { "$($w.value) window (an estimate: no Wave, so Priority places it)" }
                $txt = "$what -> stages $($w.lo)-$($w.hi) ($($w.basis))."
                if ($LAY.floorVia.ContainsKey($id)) {
                    $fv = $LAY.floorVia[$id]
                    if ($nd.stage -gt [int]$w.lo) { $txt += " Starts at $($nd.stage), one column after its open dependency $(Get-Label $fv)." }
                    else { $txt += " Its open dependency $(Get-Label $fv) ends at stage $($LAY.stageEnd[$fv]), before the window starts." }
                }
                else { $txt += ' No dependency orders it inside the window.' }
                $nd.stageExplanation = $txt
            }
            'dependency' {
                $nd.band = & $LAY.bandOfStage $nd.stage
                $nd.stageSource = 'dependency'
                $nd.stageExplanation = "No Tier, Wave or Priority: placed one column after its open dependency $(Get-Label $LAY.floorVia[$id]) (stage $($nd.stage))."
            }
            default {
                # tier, tier-inherited, prerequisite
                $b = $nd.band; $depth = $LAY.depth[$id]
                $nd.stageSource = if ($LAY.via.ContainsKey($id)) { $(if ($LAY.viaKind[$id] -eq 'plan-sequence') { 'sequence' } else { 'dependency' }) } else { 'band-floor' }
                $others = @(@(if ($directBlockers.ContainsKey($id)) { $directBlockers[$id] }) | Sort-Ordinal | Where-Object { $_ -ne $(if ($LAY.via.ContainsKey($id)) { $LAY.via[$id] } else { '' }) })
                $alsoWaits = if ($others.Count) { ' Also waits on ' + (Join-Labels $others $nodes 10) + '.' } else { '' }
                if ($nd.stageSource -eq 'sequence') {
                    $rk = $nd.planRank
                    $nd.stageExplanation = "$b+$depth by plan order (D4a): $(if ($rk) { "$($rk.language) T2 step $($rk.rank) of $($rk.of)" } else { 'T2' }), after $(Get-Label $LAY.via[$id]). The order positions; it does not block.$alsoWaits"
                }
                elseif ($nd.stageSource -eq 'dependency') {
                    $chain = [System.Collections.Generic.List[string]]::new()
                    $cur = $id; $guard = 0
                    while ($cur -and $guard -lt 24) { $chain.Insert(0, (Get-Label $cur)); $cur = $(if ($LAY.via.ContainsKey($cur)) { $LAY.via[$cur] } else { $null }); $guard++ }
                    $nd.stageExplanation = "$b+$depth via " + ($chain -join ' -> ') + ".$alsoWaits"
                }
                else {
                    $rk = if ($nd.planRank) { " Plan order: $($nd.planRank.language) T2 step $($nd.planRank.rank) of $($nd.planRank.of)." } else { '' }
                    $nd.stageExplanation = "$b+0: no same-band dependency is recorded, so it sits at the front of its band. That is unknown ordering, not readiness.$rk$alsoWaits"
                }
                if ($nd.placement -eq 'prerequisite') {
                    $pw = $pullWhy[$id]
                    $own = if ($pw.ownPlacement -in @('wave', 'priority') -and $pw.ownWindow) { "without the pull its own $($pw.ownWindow.value) window would start no earlier than that work" } elseif ($pw.ownPlacement -eq 'unplaced') { 'it has no Tier, Wave or Priority of its own' } else { 'without the pull its own placement would start no earlier than that work' }
                    $nd.stageExplanation = "Pulled into $b ahead of the tiered work that waits on it ($(Join-Labels $pw.succs $nodes 8)): $own. " + $nd.stageExplanation
                }
                elseif ($nd.placement -eq 'tier-inherited') { $nd.stageExplanation = "Tier $($nd.tier) inherited from an ancestor. " + $nd.stageExplanation }
            }
        }
        if ($nd.ContainsKey('pinnedStage')) { $nd.stage = [int]$nd.pinnedStage; $nd.stageEnd = $nd.stage; $nd.stageSource = 'overlay'; $nd.stageExplanation = "Pinned by overlay.json. $($nd.stageExplanation)" }
    }

    Write-BuildMark 'collapse'
    # collapse bars: a feature or program spans its placed, non-withdrawn children
    $collapsed = [System.Collections.Generic.HashSet[string]]::new()
    $BAR_EXCLUDED = @('unplaced', 'withdrawn', 'out')
    function Invoke-Collapse {
        param([hashtable]$N, [System.Collections.Generic.HashSet[string]]$Guard)
        if ($collapsed.Contains($N.id)) { return }
        if (-not $Guard.Add($N.id)) { return }
        $kids = @(@($N.children) | ForEach-Object { "i$_" } | Where-Object { $nodes.Contains($_) } | ForEach-Object { $nodes[$_] })
        foreach ($k in $kids) { if (@($k.children).Count -gt 0) { Invoke-Collapse -N $k -Guard $Guard } }
        # Landed children are history: they count toward completion, never toward the span, so a
        # scaffold feature with one open task does not stretch from column 0 across the chart.
        $placedAll = @($kids | Where-Object { $BAR_EXCLUDED -notcontains $_.placement -and $_.stage -ge 0 })
        $placed = @($placedAll | Where-Object { $_.placement -ne 'landed' })
        $desc = @((Get-Descendants ([int]$N.number)) | ForEach-Object { "i$_" } | Where-Object { $nodes.Contains($_) } | ForEach-Object { $nodes[$_] } | Where-Object { @($_.children).Count -eq 0 -and $BAR_EXCLUDED -notcontains $_.placement })
        $N.complete = ($desc.Count -gt 0 -and -not @($desc | Where-Object { -not $_.delivered }).Count)
        if ($N.status -eq 'withdrawn') { $N.placement = 'withdrawn'; $N.band = 'withdrawn'; $N.stageSource = 'none'; $N.stageExplanation = 'Closed as not planned or duplicate: it delivered nothing.' }
        elseif ($N.delivered -and ($PLAN_TIER_SOURCES -notcontains $N.tierSource)) {
            $N.placement = 'landed'; $N.band = 'landed'; $N.stage = 0; $N.stageEnd = 0; $N.stageSource = 'band-floor'
            $openKids = @($kids | Where-Object { -not $_.delivered -and $_.status -ne 'withdrawn' })
            $N.stageExplanation = 'Landed: the item itself is delivered.' + $(if ($openKids.Count) { " $($openKids.Count) open sub-issue(s) are drawn at their own positions; the bar is never stretched over them." } else { '' })
        }
        elseif ($placed.Count) {
            $lo = (@($placed | ForEach-Object { $_.stage }) | Measure-Object -Minimum).Minimum
            $hi = (@($placed | ForEach-Object { $_.stageEnd }) | Measure-Object -Maximum).Maximum
            $N.stage = [int]$lo; $N.stageEnd = [int]$hi; $N.stageSource = 'children'; $N.placement = 'bars'
            $N.band = & $LAY.bandOfStage $N.stage
            $unplacedKids = @($kids | Where-Object { $_.placement -eq 'unplaced' })
            $suffix = if ($unplacedKids.Count) { " $($unplacedKids.Count) of them are Unplaced and not in the span (" + ((@($unplacedKids | Select-Object -First 6) | ForEach-Object { "#$($_.number)" }) -join ', ') + ')' } else { '' }
            $landedKids = @($placedAll | Where-Object { $_.placement -eq 'landed' }).Count
            $N.stageExplanation = "Spans its open sub-issues: $lo to $hi over $($placed.Count) of $($kids.Count)" + $(if ($landedKids) { "; $landedKids delivered ones count toward completion, not the span" } else { '' }) + "$suffix." + $(if ($N.complete) { ' Every placed descendant is delivered.' } else { '' })
        }
        elseif ($placedAll.Count) {
            $N.stage = 0; $N.stageEnd = 0; $N.stageSource = 'children'; $N.placement = 'bars'; $N.band = 'landed'
            $N.stageExplanation = "Every placed sub-issue ($($placedAll.Count)) is delivered; the item itself is still $($N.status), so it sits in Landed as a bar with nothing left to span."
        }
        else {
            $win = $null
            if ($N.wave -and $LAY.windows.wave.Contains([string]$N.wave)) { $win = $LAY.windows.wave[[string]$N.wave] }
            elseif ($N.priority -and $LAY.windows.priority.Contains([string]$N.priority)) { $win = $LAY.windows.priority[[string]$N.priority] }
            if ($win) {
                $N.stage = [int]$win.lo; $N.stageEnd = [int]$win.hi; $N.stageSource = 'window'; $N.placement = 'bars'
                $N.window = [ordered]@{ field = [string]$win.field; value = [string]$win.value; lo = [int]$win.lo; hi = [int]$win.hi }
                $N.band = & $LAY.bandOfStage $N.stage
                $N.stageExplanation = "No placed sub-issue, so its own $($win.value) window places it: stages $($win.lo)-$($win.hi) ($($win.basis))."
            }
            else {
                $N.placement = 'unplaced'; $N.band = 'unplaced'; $N.stageSource = 'none'; $N.unplacedReason = 'feature-without-placed-children'
            }
        }
        [void]$collapsed.Add($N.id)
    }
    foreach ($nd in @($nodes.Values | Where-Object { @($_.children).Count -gt 0 } | Sort-Object { $_.id })) { Invoke-Collapse -N $nd -Guard ([System.Collections.Generic.HashSet[string]]::new()) }

    # the Unplaced band is real: one column when anything is unplaced
    $unplacedIds = @($nodes.Values | Where-Object { $_.placement -eq 'unplaced' } | ForEach-Object { $_.id } | Sort-Ordinal)
    $LAY.span.unplaced = $(if ($unplacedIds.Count) { 1 } else { 0 })
    $UNPLACED_STAGE = $LAY.offset.unplaced
    $stageCount = $LAY.offset.unplaced + $LAY.span.unplaced
    $REASON_TEXT = @{
        'no-tier-wave-priority-or-dependency' = 'No Tier, Wave, Priority or recorded dependency, and no tiered ancestor: the board has not said when.'
        'feature-without-placed-children'     = 'A feature with no placed sub-issue and no Wave or Priority of its own: nothing says when.'
    }
    $REASON_FIX = @{
        'no-tier-wave-priority-or-dependency' = 'Set Wave (when it is worked) or Priority (how much it matters) on the board; a dependency section or a Tier also places it.'
        'feature-without-placed-children'     = 'Set Wave or Priority on the feature, or place one of its sub-issues.'
    }
    foreach ($id in $unplacedIds) {
        $nd = $nodes[$id]
        $nd.stage = $UNPLACED_STAGE; $nd.stageEnd = $UNPLACED_STAGE
        $nd.stageExplanation = "Unplaced: $($REASON_TEXT[[string]$nd.unplacedReason]) Fix: $($REASON_FIX[[string]$nd.unplacedReason])"
    }

    Write-BuildMark 'derived'
    # ---------------------------------------------------------------- derived
    $isOpenWork = { param($x) if ($null -ne $x.number) { ($x.state -eq 'OPEN' -and -not $x.delivered) } else { (-not $x.delivered -and $x.status -ne 'out' -and $x.status -ne 'withdrawn' -and $x.kind -ne 'milestone' -and $x.kind -ne 'gate') } }
    $dependsOnMap = @{}; $dependentsMap = @{}
    foreach ($k in @($nodes.Keys)) { $dependsOnMap[$k] = [System.Collections.Generic.List[string]]::new(); $dependentsMap[$k] = [System.Collections.Generic.List[string]]::new() }
    foreach ($e in $edges) {
        if ($BLOCKING -notcontains $e.kind) { continue }
        if (-not $dependsOnMap[$e.from].Contains($e.to)) { $dependsOnMap[$e.from].Add($e.to) }
        if (-not $dependentsMap[$e.to].Contains($e.from)) { $dependentsMap[$e.to].Add($e.from) }
    }
    foreach ($nd in @($nodes.Values)) {
        $b = $false
        foreach ($t in $dependsOnMap[$nd.id]) { $tn = $nodes[$t]; if (-not $tn.delivered -and $tn.status -ne 'withdrawn') { $b = $true; break } }
        if (-not $b) { foreach ($x in @($nd.externalBlockers)) { if ([string]$x.state -eq 'OPEN') { $b = $true; break } } }
        $nd.blocked = $b
    }
    foreach ($nd in @($nodes.Values)) {
        $seen = [System.Collections.Generic.HashSet[string]]::new()
        $stack = [System.Collections.Generic.Stack[string]]::new()
        foreach ($d in $dependentsMap[$nd.id]) { $stack.Push($d) }
        while ($stack.Count) { $m = $stack.Pop(); if (-not $seen.Add($m)) { continue }; foreach ($d in $dependentsMap[$m]) { $stack.Push($d) } }
        $open = 0
        foreach ($m in $seen) { if (& $isOpenWork $nodes[$m]) { $open++ } }
        $nd.downstreamOpen = $open
    }

    Write-BuildMark 'critical path'
    # critical path: every maximal chain of depends-on / plan-order edges (non-inherited) between
    # leaves in the given bands. len[id] = 1 + max(len[pred]) keeps every predecessor that reaches
    # the maximum, so a second route into a sink is never lost.
    $CHAIN_CAP = 24
    function Get-MaximalChains {
        param([string[]]$Bands)
        $pred = @{}
        foreach ($k in @($nodes.Keys)) { $pred[$k] = [System.Collections.Generic.List[string]]::new() }
        foreach ($e in $edges) {
            if ($e.kind -notin @('depends-on', 'plan-order')) { continue }
            if ([string]$e.source -like 'inherited:*') { continue }
            if (($Bands -notcontains $nodes[$e.from].band) -or ($Bands -notcontains $nodes[$e.to].band)) { continue }
            if (@($nodes[$e.from].children).Count -gt 0 -or @($nodes[$e.to].children).Count -gt 0) { continue }
            if (-not $pred[$e.from].Contains($e.to)) { $pred[$e.from].Add($e.to) }
        }
        $len = @{}
        $ids = @($nodes.Keys | Where-Object { $Bands -contains $nodes[$_].band -and @($nodes[$_].children).Count -eq 0 } | Sort-Ordinal)
        # iterative longest-path over the (acyclic) predecessor graph
        $pending = [System.Collections.Generic.Stack[string]]::new()
        $state = @{}
        foreach ($root in $ids) {
            if ($len.ContainsKey($root)) { continue }
            $pending.Push($root)
            while ($pending.Count) {
                $id = $pending.Peek()
                if (-not $state.ContainsKey($id)) {
                    $state[$id] = 1
                    foreach ($p in $pred[$id]) { if (-not $len.ContainsKey($p) -and -not $state.ContainsKey($p)) { $pending.Push($p) } }
                    continue
                }
                [void]$pending.Pop()
                if ($len.ContainsKey($id)) { continue }
                $m = 1
                foreach ($p in $pred[$id]) { if ($len.ContainsKey($p) -and $len[$p] + 1 -gt $m) { $m = $len[$p] + 1 } }
                $len[$id] = $m
            }
        }
        $best = 0
        foreach ($id in $ids) { if ($len[$id] -gt $best) { $best = $len[$id] } }
        $keys = [System.Collections.Generic.List[string]]::new()
        $cut = $false
        if ($best -gt 1) {
            $work = [System.Collections.Generic.Stack[object]]::new()
            $sinksAsc = @($ids | Where-Object { $len[$_] -eq $best } | Sort-Ordinal)
            for ($i = $sinksAsc.Count - 1; $i -ge 0; $i--) { $work.Push(@{ id = $sinksAsc[$i]; suffix = '' }) }
            while ($work.Count) {
                $w = $work.Pop()
                $chain = if ($w.suffix) { "$($w.id)|$($w.suffix)" } else { [string]$w.id }
                if ($len[$w.id] -le 1) {
                    if ($keys.Count -ge $CHAIN_CAP) { $cut = $true; break }
                    $keys.Add($chain); continue
                }
                $ps = @($pred[$w.id] | Where-Object { $len[$_] -eq $len[$w.id] - 1 } | Sort-Ordinal)
                for ($i = $ps.Count - 1; $i -ge 0; $i--) { $work.Push(@{ id = $ps[$i]; suffix = $chain }) }
            }
        }
        $keys.Sort([System.StringComparer]::Ordinal)
        $sorted = [System.Collections.Generic.List[object]]::new()
        foreach ($k in $keys) { $sorted.Add([string[]]($k -split '\|')) }
        return @{ chains = $sorted; best = $best; truncated = $cut }
    }
    $cpResult = Get-MaximalChains -Bands $POSITIONED_BANDS
    $bestLen = [int]$cpResult.best
    $truncated = [bool]$cpResult.truncated
    if ($truncated) { Add-Warn 'critical-path-truncated' "More than $CHAIN_CAP maximal chains; the list is cut at $CHAIN_CAP." $null $null }
    $chainList = $cpResult.chains
    $t1Result = Get-MaximalChains -Bands @('gate', 't1')
    $t1Sinks = @($t1Result.chains | ForEach-Object { $_[-1] } | Select-Object -Unique | Sort-Ordinal)
    $onCp = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($c in $chainList) { foreach ($id in $c) { [void]$onCp.Add($id) } }
    foreach ($id in $onCp) { $nodes[$id].onCriticalPath = $true }
    $cpPairs = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($c in $chainList) { for ($i = 1; $i -lt $c.Count; $i++) { [void]$cpPairs.Add("$($c[$i])>$($c[$i-1])") } }
    $sinks = @($chainList | ForEach-Object { $_[-1] } | Select-Object -Unique | Sort-Ordinal)
    foreach ($e in $edges) {
        if ($e.kind -in @('depends-on', 'plan-order') -and $cpPairs.Contains("$($e.from)>$($e.to)")) { $e.critical = $true }
        $e.crossLane = ($nodes[$e.from].lane -ne $nodes[$e.to].lane)
        $e.crossBand = ($nodes[$e.from].band -ne $nodes[$e.to].band)
    }
    $blockers = @(
        @($nodes.Values) |
        Where-Object { $_.downstreamOpen -gt 0 -and $_.band -ne 'landed' -and @($_.children).Count -eq 0 -and $_.kind -notin @('milestone', 'gate') } |
        Sort-Object -Property @{ Expression = { $_.downstreamOpen }; Descending = $true }, @{ Expression = { if ($null -ne $_.number) { $_.number } else { 0 } } }, @{ Expression = { $_.id } } |
        Select-Object -First 12 |
        ForEach-Object {
            $o = [ordered]@{ node = $_.id; downstreamOpen = $_.downstreamOpen; status = $_.status }
            if ($_.status -eq 'delivered-unmerged') { $o.note = "Delivered on integration branch PR #$($programs.integrationPr); the blocker is a merge, not a task." }
            elseif ($_.status -eq 'in-progress') { $o.note = "In progress, and $($_.downstreamOpen) open items wait on it." }
            elseif ($_.status -in @('backlog', 'ready')) { $o.note = "Not started, and $($_.downstreamOpen) open items wait on it." }
            $o
        }
    )

    Write-BuildMark 'milestones'
    # the T1 -> T2 milestone
    $milestones = [ordered]@{}
    if ($LAY.span.t1 -gt 0) {
        $t1Leaves = @($nodes.Values | Where-Object { $_.tier -eq 'T1' -and $null -ne $_.number -and @($_.children).Count -eq 0 -and $_.status -ne 'withdrawn' })
        $openT1 = @($t1Leaves | Where-Object { -not $_.delivered } | ForEach-Object { $_.id } | Sort-Ordinal)
        $t1Items = @($nodes.Values | Where-Object { $_.tier -eq 'T1' -and $null -ne $_.number })
        $openBugs = [System.Collections.Generic.List[string]]::new()
        foreach ($t in $t1Items) { foreach ($d in (Get-Descendants ([int]$t.number))) { $dn = $nodes["i$d"]; if ($dn -and $dn.isBug -and -not $dn.delivered -and $dn.status -ne 'withdrawn' -and -not $openBugs.Contains($dn.id)) { $openBugs.Add($dn.id) } } }
        $remaining = @(@($openT1) + @($openBugs) | Select-Object -Unique | Sort-Ordinal)
        $sinkText = if ($t1Sinks.Count) { 'T1 ends at ' + (Join-Labels $t1Sinks $nodes 8) + ", the end(s) of the $($t1Result.chains.Count) longest chain(s) through the gate and T1 bands ($($t1Result.best) nodes each)" } else { 'No chain was found in the gate and T1 bands' }
        $ms = @{
            id = 'ms:t1-complete'; kind = 'milestone'; number = $null; url = $null; wbs = $null
            title = 'T1 complete in SQL, OQL and GQL'; shortTitle = 'T1 complete'
            lane = 'shared'; band = 't2'; category = $null; categorySource = 'none'; epic = $null; programs = @(); program = $null; programSource = 'none'; family = $null
            tier = 'T1'; tierSource = 'plan'; planLanguage = 'Shared'
            placement = 'milestone'; window = $null; unplacedReason = $null
            stage = $LAY.offset.t2; stageEnd = $LAY.offset.t2; stageSource = 'children'
            stageExplanation = "Drawn as the rule between the last T1 column ($($LAY.offset.t2 - 1)) and the T2 band. $sinkText. $($remaining.Count) open item(s) remain: $($openT1.Count) T1 leaves and $($openBugs.Count) open defect(s) under T1 items."
            status = $(if ($remaining.Count) { 'backlog' } else { 'delivered' }); state = $null; stateReason = $null; delivered = (-not $remaining.Count)
            blocked = [bool]$remaining.Count; untracked = $false; priority = $null; wave = $null; area = $null; areaSource = 'none'; boardKind = $null
            labels = @(); issueType = $null; isBug = $false; parent = $null; children = @(); externalBlockers = @()
            downstreamOpen = 0; onCriticalPath = $false; conflict = $null; pinned = $false; hasOpenDefect = $false; openDefects = 0; planRank = $null
            note = 'Not an issue. The marker exists so the T2 band reads as "after T1", which is the plan''s D5 ordering.'
        }
        Add-Node $ms
        $milestones.t1Complete = [ordered]@{ node = 'ms:t1-complete'; stage = $LAY.offset.t2; status = $(if ($remaining.Count) { 'open' } else { 'complete' }); remaining = $remaining; openT1Leaves = $openT1.Count; openDefects = $openBugs.Count; sinks = $t1Sinks; chains = $t1Result.chains.ToArray(); chainNodes = [int]$t1Result.best }
    }

    # gate nodes: one per gate program, drawn at the last column its members reach
    $gates = [System.Collections.Generic.List[object]]::new()
    foreach ($pr in @($programList | Where-Object { [string]$_.shape -eq 'gate' })) {
        $g = [int]$pr.gate.issue
        if (-not $byNum.ContainsKey($g)) { Add-Warn 'gate-missing' "Gate issue #$g of program $($pr.id) is not in the subtree." $null $null; continue }
        $it = $byNum[$g]
        $memberIds = @($programMembership[[string]$pr.id].members | Where-Object { $_ -ne $g } | ForEach-Object { "i$_" } | Where-Object { $nodes.Contains($_) })
        $placedMembers = @($memberIds | Where-Object { $nodes[$_].stage -ge 0 -and $nodes[$_].placement -notin @('unplaced', 'withdrawn', 'out') })
        $col = if ($placedMembers.Count) { (@($placedMembers | ForEach-Object { $nodes[$_].stageEnd }) | Measure-Object -Maximum).Maximum } else { 0 }
        $allClosed = ($memberIds.Count -gt 0 -and -not @($memberIds | Where-Object { $nodes[$_].state -ne 'CLOSED' }).Count)
        $gStatus = if ($it.state -eq 'CLOSED' -and ($null -eq $it.stateReason -or $it.stateReason -eq 'COMPLETED')) { 'delivered' } elseif ($it.state -eq 'OPEN' -and $allClosed) { 'awaiting-evidence' } elseif ($it.boardStatus -and $STATUS_BY_BOARD.ContainsKey([string]$it.boardStatus)) { $STATUS_BY_BOARD[[string]$it.boardStatus] } else { 'backlog' }
        $gCat = if ($catOf.ContainsKey($g)) { $catOf[$g] } elseif ($taxByNum.ContainsKey($g)) { [string]$taxByNum[$g].category } else { $null }
        $openMembers = @($memberIds | Where-Object { & $isOpenWork $nodes[$_] }).Count
        $gNode = @{
            id = "i$g"; kind = 'gate'; number = $g; url = [string]$it.url; wbs = $it.wbs
            title = [string]$it.titleWithoutWbs; shortTitle = (Get-ShortTitle ([string]$pr.gate.label))
            lane = $(if (Get-CategoryLane $gCat) { Get-CategoryLane $gCat } else { 'other' }); band = $(& $LAY.bandOfStage ([int]$col)); category = $gCat; categorySource = $(if ($catOf.ContainsKey($g)) { $catSrc[$g] } else { 'carried' })
            epic = $(if ($gCat) { ($gCat.Split('.')[0..2]) -join '.' } else { $null })
            programs = @([string]$pr.id); program = [string]$pr.id; programSource = $(if ($it.boardProgram) { 'field' } else { 'carried' }); family = $null
            tier = $null; tierSource = 'none'; planLanguage = $null
            placement = 'gate'; window = $null; unplacedReason = $null
            stage = [int]$col; stageEnd = [int]$col; stageSource = 'children'
            stageExplanation = "Gate milestone at the last column its $($placedMembers.Count) placed members reach ($col). It closes only against its own acceptance criteria, never because its members closed. $openMembers member(s) open."
            status = $gStatus; state = [string]$it.state; stateReason = $it.stateReason; delivered = ($gStatus -eq 'delivered')
            blocked = ($openMembers -gt 0); untracked = $false
            priority = $(if ("$($it.boardPriority)" -match '^P\d{3}$') { [string]$it.boardPriority } else { $null }); wave = $(if ("$($it.boardWave)" -match '^W\d{2}$') { [string]$it.boardWave } else { $null })
            area = $null; areaSource = 'none'; boardKind = $it.boardKind; labels = @($it.labels); issueType = $it.issueType; isBug = $false
            parent = $it.parent; children = @(); externalBlockers = @()
            downstreamOpen = 0; onCriticalPath = $false; conflict = $null; pinned = $false; hasOpenDefect = $false; openDefects = 0; planRank = $null
            label = [string]$pr.gate.label; gateMembers = @($memberIds | Sort-Object { [int]($_.Substring(1)) }); gateOpenMembers = $openMembers
        }
        Add-Node $gNode
        foreach ($m in $memberIds) { Add-Edge "i$g" $m 'gate-member' $(if ($programFieldPresent) { 'program-field' } else { 'carried-program' }) $null }
        foreach ($le in $gateLinkEdges) { if ([int]$le.gate -eq $g -and $nodes.Contains("i$($le.member)")) { $e0 = $edgeKeys["i$g>i$($le.member)>gate-member"]; if ($e0) { $e0.source = 'gate-link' } else { Add-Edge "i$g" "i$($le.member)" 'gate-member' 'gate-link' $null } } }
        $gates.Add([ordered]@{ node = "i$g"; program = [string]$pr.id; label = [string]$pr.gate.label; stage = [int]$col; status = $gStatus; members = $memberIds.Count; openMembers = $openMembers; placedMembers = $placedMembers.Count })
    }
    foreach ($e in $edges) { if (-not $e.ContainsKey('crossLane')) { $e.crossLane = ($nodes[$e.from].lane -ne $nodes[$e.to].lane); $e.crossBand = ($nodes[$e.from].band -ne $nodes[$e.to].band) } }

    Write-BuildMark 'next up'
    # next up: the workflow's own rule. Unblocked first, then Priority, then Wave.
    $pnum = { param($v) if ($v -and $v -match '^P(\d{3})$') { [int]$Matches[1] } else { 999 } }
    $wnum = { param($v) if ($v -and $v -match '^W(\d{2})$') { [int]$Matches[1] } else { 999 } }
    $nextCands = @($nodes.Values | Where-Object { $null -ne $_.number -and $_.kind -notin @('gate', 'milestone') -and $_.state -eq 'OPEN' -and -not $_.delivered -and $_.status -ne 'withdrawn' -and @($_.children).Count -eq 0 -and $_.placement -notin @('unplaced', 'withdrawn', 'out') -and -not $_.blocked } |
        Sort-Object @{ Expression = { & $pnum $_.priority } }, @{ Expression = { & $wnum $_.wave } }, @{ Expression = { $_.stage } }, @{ Expression = { $_.number } })
    $nextUp = [ordered]@{
        rule  = 'Open, undelivered leaves that are placed and not blocked, sorted by Priority, then Wave (missing last), then stage, then number: the workflow''s dependency -> Priority -> Wave.'
        total = $nextCands.Count
        top   = @($nextCands | Select-Object -First 25 | ForEach-Object {
                [ordered]@{ node = $_.id; number = $_.number; priority = $_.priority; wave = $_.wave; stage = $_.stage; placement = $_.placement; status = $_.status
                    why = (@(($(if ($_.priority) { $_.priority } else { 'no Priority' })), ($(if ($_.wave) { $_.wave } else { 'no Wave' })), "$($_.placement) at stage $($_.stage)") -join ' · ') }
            })
    }

    # contradictions (board facts the model exposes; listed, never fixed here)
    $contradictions = [System.Collections.Generic.List[object]]::new()
    foreach ($nd in @($nodes.Values | Sort-Object { $_.id })) {
        if ($null -eq $nd.number -or $nd.kind -in @('gate', 'milestone')) { continue }
        $leaf = (@($nd.children).Count -eq 0)
        if ($nd.status -eq 'ready' -and $leaf -and $nd.blocked) {
            $wait = @($dependsOnMap[$nd.id] | Where-Object { -not $nodes[$_].delivered -and $nodes[$_].status -ne 'withdrawn' } | Sort-Ordinal)
            $ext = @($nd.externalBlockers | Where-Object { $_.state -eq 'OPEN' } | ForEach-Object { "#$($_.number) (outside L03.02)" })
            $contradictions.Add([ordered]@{ code = 'ready-but-blocked'; node = $nd.id; detail = "Status Ready, but it waits on open " + (@((Join-Labels $wait $nodes 8)) + $ext | Where-Object { $_ }) -join ', '; fix = "Move #$($nd.number) to Backlog until its blockers land, or remove the dependency." })
        }
        if ($leaf -and $nd.placement -in @('tier', 'tier-inherited') -and $nd.wave -and -not $nd.delivered -and $LAY.windows.wave.Contains([string]$nd.wave)) {
            $w = $LAY.windows.wave[[string]$nd.wave]
            if ($nd.stage -lt $w.lo -or $nd.stage -gt $w.hi) { $contradictions.Add([ordered]@{ code = 'wave-tier-conflict'; node = $nd.id; detail = "$($nd.tier) puts it at stage $($nd.stage); its Wave $($nd.wave) is scheduled at stages $($w.lo)-$($w.hi)."; fix = "Owner call: move the Wave to its tier's window, or accept that the board schedules it ahead of its tier." }) }
        }
        if ($leaf -and $nd.placement -eq 'wave' -and $nd.priority -and $LAY.windows.priority.Contains([string]$nd.priority)) {
            $a = $LAY.windows.wave[[string]$nd.wave]; $b = $LAY.windows.priority[[string]$nd.priority]
            if ($a.hi -lt $b.lo -or $b.hi -lt $a.lo) { $contradictions.Add([ordered]@{ code = 'priority-wave-conflict'; node = $nd.id; detail = "$($nd.priority) work sits at stages $($b.lo)-$($b.hi); its Wave $($nd.wave) at stages $($a.lo)-$($a.hi). The windows do not overlap."; fix = "Owner call: align the Priority or the Wave." }) }
        }
        if ($nd.placement -in @('tier', 'tier-inherited', 'prerequisite') -and -not $nd.delivered) {
            foreach ($p in $posPreds[$nd.id]) {
                if ($p.kind -eq 'plan-sequence') { continue }
                $pn = $nodes[$p.id]
                if ($pn.delivered -or $pn.status -eq 'withdrawn' -or $pn.placement -in @('tier', 'tier-inherited', 'prerequisite', 'landed')) { continue }
                if ($pn.placement -eq 'unplaced' -or $pn.stage -ge $nd.stage) {
                    $contradictions.Add([ordered]@{ code = 'scheduled-after-dependent'; node = $nd.id; detail = "Waits on $(Get-Label $p.id), which is placed at $(if ($pn.placement -eq 'unplaced') { 'Unplaced' } else { "stage $($pn.stage)" }), not before it."; fix = "Give $(Get-Label $p.id) a Wave or Priority that precedes stage $($nd.stage), or remove the dependency." })
                    Add-Warn 'scheduled-after-dependent' "Tiered work waits on an untiered item placed after it." $nd.id (Get-Label $p.id)
                }
            }
        }
    }

    Write-BuildMark 'hygiene'
    # hygiene (tooling E15 parity; same snapshot, no extra reads)
    $hygiene = [System.Collections.Generic.List[object]]::new()
    $categorisedEpics = @{}; foreach ($c in $categoryByCode.Values) { if ($epicByWbs.Contains($c.epic)) { $categorisedEpics[[int]$epicByWbs[$c.epic]] = $c.epic } }
    $categoryFieldExists = $projectFields.Contains('Category')
    $areaHasDatabase = ($projectFields.Contains('Area') -and @($projectFields['Area'] | Where-Object { $_ -like 'Database*' }).Count -gt 0)
    $LANGUAGE_SURFACE = @('L03.02.01.C07', 'L03.02.02.C01', 'L03.02.03.C01', 'L03.02.05.C01')
    $wbsCount = @{}
    foreach ($i in $snapItems) { if ($i.wbs) { Add-Tally $wbsCount ([string]$i.wbs) } }
    foreach ($it in ($snapItems | Sort-Object { [int]$_.number })) {
        $n = [int]$it.number; $nd = $nodes["i$n"]
        if ($it.state -eq 'OPEN' -and -not $it.isCategory -and $null -ne $it.parent -and $categorisedEpics.ContainsKey([int]$it.parent) -and -not $gateIssue.ContainsKey($n)) {
            $hygiene.Add([ordered]@{ code = 'direct-under-categorised-epic'; issue = $n; detail = "Open, and a direct child of epic #$($it.parent), which has categories."; fix = "Re-parent #$n under its category$(if ($catOf.ContainsKey($n)) { " ($($catOf[$n]))" } else { '' })." })
        }
        if ($nd -and $nd.kind -notin @('gate')) {
            if ($categoryFieldExists -and -not $it.boardCategory) { $hygiene.Add([ordered]@{ code = 'missing-category'; issue = $n; detail = 'The Category field exists and is empty on this item.'; fix = "Set Category on #$n$(if ($catOf.ContainsKey($n)) { " (carried: $($catOf[$n]))" } else { '' })." }) }
            if ($areaHasDatabase -and -not $it.boardArea) { $hygiene.Add([ordered]@{ code = 'missing-area'; issue = $n; detail = 'The Area field has Database options and is empty on this item.'; fix = "Set Area on #$n$(if ($nd.area) { " (carried: $($nd.area))" } else { '' })." }) }
            if ($it.state -eq 'OPEN' -and -not $nd.delivered -and $nd.category -and $LANGUAGE_SURFACE -contains $nd.category -and -not $nd.tier) { $hygiene.Add([ordered]@{ code = 'language-surface-without-tier'; issue = $n; detail = "Open in a language-surface category ($($nd.category)) with no tier from the field, the plan or an ancestor."; fix = "Set Tier on #$n, or file it under its plan family." }) }
            if ($it.state -eq 'OPEN' -and -not $nd.delivered -and $nd.status -ne 'withdrawn' -and -not $nd.priority -and -not $nd.wave) { $hygiene.Add([ordered]@{ code = 'no-priority-or-wave'; issue = $n; detail = "Neither Priority nor Wave.$(if ($nd.placement -eq 'unplaced') { ' It is Unplaced.' } else { " Placed by $($nd.placement)." })"; fix = "Set Priority and Wave on #$n." }) }
        }
        if ($null -ne $it.subIssuesSummaryTotal -and [int]$it.subIssuesSummaryTotal -ge 95) { $hygiene.Add([ordered]@{ code = 'parent-near-cap'; issue = $n; detail = "$($it.subIssuesSummaryTotal) sub-issues; GitHub caps a parent at 100."; fix = "File new work under a category or a feature below #$n." }) }
        if ($it.wbs -and $wbsCount[[string]$it.wbs] -gt 1) { $hygiene.Add([ordered]@{ code = 'duplicate-wbs'; issue = $n; detail = "[$($it.wbs)] is carried by $($wbsCount[[string]$it.wbs]) issues."; fix = 'Renumber the later filing with the next free serial.' }) }
    }
    foreach ($progId in $programMembership.Keys) {
        $pm = $programMembership[$progId]
        if (-not $pm.Contains('gate') -or -not $pm.gate.membershipCheck.checked) { continue }
        foreach ($x in @($pm.gate.membershipCheck.onlyField)) { $hygiene.Add([ordered]@{ code = 'program-membership-mismatch'; issue = [int]$x; detail = "Program = $($pm.boardOption), but no blocked-by link from #$($pm.gate.issue) reaches it."; fix = "Link #$($pm.gate.issue) blocked-by #$x (or its feature), or clear its Program with the owner's agreement." }) }
        foreach ($x in @($pm.gate.membershipCheck.onlyLinks)) { $hygiene.Add([ordered]@{ code = 'program-membership-mismatch'; issue = [int]$x; detail = "#$($pm.gate.issue)'s blocked-by links reach it, but it does not carry Program = $($pm.boardOption)."; fix = "Set Program = $($pm.boardOption) on #$x, or remove the link with the owner's agreement." }) }
    }

    Write-BuildMark 'stages'
    # ------------------------------------------------------------- stages
    $stageMeta = @{}; $stagePattern = @{}
    foreach ($s in @($overlay.stages)) { if ([string]$s.localDepth -eq '*') { $stagePattern[[string]$s.band] = $s } else { $stageMeta["$($s.band)/$($s.localDepth)"] = $s } }
    $stages = [System.Collections.Generic.List[object]]::new()
    $NOT_COLUMN_MEMBERS = @('bars', 'gate', 'milestone', 'wave', 'priority', 'withdrawn', 'out')
    foreach ($band in $BAND_ORDER) {
        for ($d = 0; $d -lt $LAY.span[$band]; $d++) {
            $index = $LAY.offset[$band] + $d
            $meta = $stageMeta["$band/$d"]; $isPattern = $false
            if (-not $meta -and $stagePattern.ContainsKey($band)) { $meta = $stagePattern[$band]; $isPattern = $true }
            if (-not $meta) { Add-Warn 'no-stage-name' 'No overlay name for a computed column.' $null "band=$band localDepth=$d index=$index" }
            $fill = { param($s) if ($null -eq $s) { return $null }; ([string]$s).Replace('{n}', [string]($d + 1)) }
            $members = @($nodes.Values | Where-Object { $_.stage -eq $index -and $NOT_COLUMN_MEMBERS -notcontains $_.placement })
            $alongside = @($nodes.Values | Where-Object { $_.placement -in @('wave', 'priority') -and $_.stage -le $index -and $_.stageEnd -ge $index })
            $st = [ordered]@{
                id = $(if ($meta) { & $fill $meta.id } else { "s$index" })
                index = $index
                name = $(if ($meta) { & $fill $meta.name } else { "Stage $index" })
                band = $band; localDepth = $d
                nodeCount = $members.Count
                bandFloorCount = @($members | Where-Object { $_.stageSource -eq 'band-floor' -and $_.placement -ne 'landed' }).Count
                alongsideCount = $alongside.Count
            }
            if ($isPattern) { $st.named = 'pattern' }
            if ($meta -and $meta.ContainsKey('entryCondition')) { $st.entryCondition = & $fill $meta.entryCondition }
            if ($meta -and $meta.ContainsKey('exitCondition')) { $st.exitCondition = & $fill $meta.exitCondition }
            $stages.Add($st)
        }
    }

    # ------------------------------------------------------------- lanes
    $familyCount = @{}
    foreach ($f in @($tiers.families)) {
        if ($FAMILY_ALIAS.ContainsKey([string]$f.id)) { continue }
        $lang = (& $LANG_OF_FAMILY $f)
        $lane = if ($laneByLanguage.ContainsKey($lang)) { $laneByLanguage[$lang] } else { 'shared' }
        Add-Tally $familyCount $lane
    }
    $lanes = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $laneDefs.Count; $i++) {
        $ld = $laneDefs[$i]; $id = [string]$ld.id
        $l = [ordered]@{ id = $id; name = [string]$ld.name; order = $i }
        $ep = @(if ($ld.ContainsKey('epics')) { @($ld.epics) })
        if ($ep.Count) { $l.epics = $ep; $l.epicIssues = @($ep | ForEach-Object { if ($epicByWbs.Contains([string]$_)) { [int]$epicByWbs[[string]$_] } }) }
        if ($ld.ContainsKey('categories')) { $l.categories = @($ld.categories) }
        if ($ld.ContainsKey('planLanguage')) { $l.planLanguage = [string]$ld.planLanguage }
        if ($ld.ContainsKey('collapsedByDefault') -and $ld.collapsedByDefault) { $l.collapsedByDefault = $true }
        if ($familyCount.ContainsKey($id)) { $l.familyCount = $familyCount[$id] }
        $l.itemCount = @($nodes.Values | Where-Object { $_.lane -eq $id -and $null -ne $_.number -and $_.kind -notin @('gate') }).Count
        $lanes.Add($l)
    }

    Write-BuildMark 'categories block'
    # ------------------------------------------------------------- categories
    $categories = [System.Collections.Generic.List[object]]::new()
    $codes = [System.Collections.Generic.List[string]]::new()
    foreach ($k in $categoryByCode.Keys) { $codes.Add($k) }
    foreach ($k in $catDef.Keys) { if (-not $codes.Contains($k)) { $codes.Add($k) } }
    $codesSorted = @($codes | Sort-Object @{ Expression = { ($_ -split '\.')[2] } }, @{ Expression = { $_ } })
    $byCat = @{}
    foreach ($nd in $nodes.Values) {
        if (-not $nd.category) { continue }
        $k = [string]$nd.category
        if (-not $byCat.ContainsKey($k)) { $byCat[$k] = @{ work = [System.Collections.Generic.List[object]]::new(); gates = [System.Collections.Generic.List[object]]::new(); slots = [System.Collections.Generic.List[object]]::new() } }
        if ($nd.kind -eq 'gate') { $byCat[$k].gates.Add($nd) }
        elseif ($nd.kind -eq 'slot') { $byCat[$k].slots.Add($nd) }
        elseif ($null -ne $nd.number) { $byCat[$k].work.Add($nd) }
    }
    $childCountOf = @{}; foreach ($i in $snapItems) { if ($null -ne $i.parent) { Add-Tally $childCountOf ([string]$i.parent) } }
    foreach ($code in $codesSorted) {
        $def = if ($catDef.Contains($code)) { $catDef[$code] } else { $null }
        $live = if ($categoryByCode.Contains($code)) { $categoryByCode[$code] } else { $null }
        $grp = if ($byCat.ContainsKey($code)) { $byCat[$code] } else { @{ work = @(); gates = @(); slots = @() } }
        $work = @($grp.work); $gatesIn = @($grp.gates); $slots = @($grp.slots)
        $c = [ordered]@{
            id = $code
            name = $(if ($live) { $live.name } elseif ($def) { [string]$def.name } else { $code })
            epic = $(if ($live) { $live.epic } elseif ($def) { [string]$def.epic } else { ($code.Split('.')[0..2]) -join '.' })
            kind = $(if ($def) { [string]$def.kind } else { $null })
            precedence = $(if ($def -and $null -ne $def.precedence) { [int]$def.precedence } else { $null })
            source = $(if ($live) { 'live' } else { 'proposed' })
            issue = $(if ($live) { [int]$live.issue } else { $null })
            lane = (Get-CategoryLane $code)
            itemCount = $work.Count
            gateCount = $gatesIn.Count
            openCount = @($work | Where-Object { $_.state -eq 'OPEN' -and -not $_.delivered }).Count
            deliveredCount = @($work | Where-Object { $_.delivered }).Count
            withdrawnCount = @($work | Where-Object { $_.status -eq 'withdrawn' }).Count
            slotCount = $slots.Count
            inTreeCount = $(if ($live -and $childCountOf.Contains([string]$live.issue)) { [int]$childCountOf[[string]$live.issue] } else { 0 })
            items = @($work | ForEach-Object { [int]$_.number } | Sort-Object)
        }
        if ($def) { $c.definition = [string]$def.definition; $c.boundaries = @($def.boundaries); if ($def.ContainsKey('owningCode')) { $c.owningCode = [string]$def.owningCode } }
        if ($live -and $def -and [string]$def.name -ne $live.name) { $c.carriedName = [string]$def.name }
        $categories.Add($c)
    }

    Write-BuildMark 'counts'
    # ------------------------------------------------------------- counts
    $workNodes = @($nodes.Values | Where-Object { $null -ne $_.number -and $_.kind -ne 'gate' })
    $openWork = @($workNodes | Where-Object { $_.state -eq 'OPEN' -and -not $_.delivered -and $_.status -ne 'withdrawn' })
    $PLACEMENT_ORDER = @('tier', 'tier-inherited', 'prerequisite', 'dependency', 'wave', 'priority', 'bars', 'unplaced', 'landed', 'withdrawn', 'out')
    $openByPl = [ordered]@{}; foreach ($nd in $openWork) { Add-Tally $openByPl $nd.placement }
    $allByPl = [ordered]@{}; foreach ($nd in $workNodes) { Add-Tally $allByPl $nd.placement }
    $unplacedWork = @($workNodes | Where-Object { $_.placement -eq 'unplaced' })
    $unplacedByReason = [ordered]@{}; foreach ($nd in $unplacedWork) { Add-Tally $unplacedByReason $nd.unplacedReason }
    $famTierSeen = @{}
    foreach ($nd in $nodes.Values) {
        if (-not $nd.family -or -not $nd.tier -or $nd.kind -eq 'milestone') { continue }
        if (-not $famTierSeen.ContainsKey([string]$nd.family)) { $famTierSeen[[string]$nd.family] = [System.Collections.Generic.HashSet[string]]::new() }
        [void]$famTierSeen[[string]$nd.family].Add([string]$nd.tier)
    }
    $familyTierCounts = [ordered]@{}; $familyTierPairs = 0
    foreach ($k in @($famTierSeen.Keys | Sort-Ordinal)) { foreach ($t in @($famTierSeen[$k] | Sort-Ordinal)) { Add-Tally $familyTierCounts $t; $familyTierPairs++ } }
    $filedItems = [ordered]@{}
    foreach ($nd in $workNodes) { if ($nd.tier) { Add-Tally $filedItems $nd.tier } }
    $filedFamilies = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($nd in $workNodes) { if ($nd.family) { [void]$filedFamilies.Add([string]$nd.family) } }
    $slotNodes = @($nodes.Values | Where-Object { $_.kind -eq 'slot' })
    $unfiledSlotCount = @($slotNodes | Where-Object { $_.status -eq 'unfiled' }).Count
    $byStatus = [ordered]@{}; foreach ($nd in $workNodes) { Add-Tally $byStatus $nd.status }
    $byLane = [ordered]@{}; foreach ($nd in $workNodes) { Add-Tally $byLane $nd.lane }
    $srcTally = { param([string]$Prop) $m = [ordered]@{}; foreach ($nd in $workNodes) { Add-Tally $m ([string]$nd[$Prop]) }; return $m }
    $t2Steps = [ordered]@{}
    foreach ($lang in $t2Families.Keys) { $t2Steps[$lang] = $t2Families[$lang].Count }
    $containersCount = [ordered]@{ root = 1; epics = $epicByWbs.Count; categories = $categoryByCode.Count; gates = @($containerKind.Values | Where-Object { $_ -eq 'gate' }).Count }
    $containersCount.total = $containersCount.root + $containersCount.epics + $containersCount.categories + $containersCount.gates
    $edgeKinds = [ordered]@{}; foreach ($e in $edges) { Add-Tally $edgeKinds $e.kind }
    $counts = [ordered]@{
        boardItems        = [int]$Snapshot.counts.totalItems
        containers        = $containersCount
        workItems         = $workNodes.Count
        open              = [ordered]@{ total = $openWork.Count; byPlacement = (Get-OrderedTally $openByPl $PLACEMENT_ORDER) }
        byPlacement       = (Get-OrderedTally $allByPl $PLACEMENT_ORDER)
        delivered         = @($workNodes | Where-Object { $_.status -eq 'delivered' }).Count
        deliveredUnmerged = @($workNodes | Where-Object { $_.status -eq 'delivered-unmerged' }).Count
        withdrawn         = @($workNodes | Where-Object { $_.status -eq 'withdrawn' }).Count
        unplaced          = [ordered]@{ total = $unplacedWork.Count; byReason = $unplacedByReason; items = @($unplacedWork | ForEach-Object { $_.id } | Sort-Ordinal) }
        t2                = [ordered]@{ mode = $t2Mode; steps = $t2Steps; planSequenceEdges = $planSequenceCount }
        families          = $familyTierCounts
        familiesDistinct  = $famTierSeen.Keys.Count
        familyTierPairs   = $familyTierPairs
        familiesDeclared  = @($tiers.families).Count
        familyAliases     = $FAMILY_ALIAS
        filedItems        = $filedItems
        unfiledSlots      = $unfiledSlotCount
        slotNodes         = $slotNodes.Count
        familiesWithNothingFiled = $famTierSeen.Keys.Count - $filedFamilies.Count
        byStatus          = (Get-OrderedTally $byStatus @('delivered', 'delivered-unmerged', 'in-progress', 'ready', 'backlog', 'withdrawn'))
        byLane            = (Get-OrderedTally $byLane $LANE_ORDER)
        nextUpTotal       = $nextUp.total
        contradictions    = $contradictions.Count
        hygiene           = $hygiene.Count
        placementSources  = [ordered]@{ category = (& $srcTally 'categorySource'); tier = (& $srcTally 'tierSource'); program = (& $srcTally 'programSource'); area = (& $srcTally 'areaSource') }
        fieldsPresent     = $Snapshot.counts.fieldsPresent
        fieldsDefined     = [ordered]@{ Category = $projectFields.Contains('Category'); Tier = $projectFields.Contains('Tier'); Program = $projectFields.Contains('Program'); AreaDatabaseOptions = $areaHasDatabase }
        edges             = $edges.Count
        edgesByKind       = (Get-OrderedTally $edgeKinds @('depends-on', 'plan-order', 'plan-sequence', 'program-gate', 'gate-member', 'child-of', 'conflict'))
        inheritedEdges    = $inheritedCount
        edgesIgnored      = $edgesIgnored
        pulledPrerequisites = $pulled.Count
        stages            = $stages.Count
        nodes             = $nodes.Count
    }

    # ------------------------------------------------------- aggregate warnings
    $AGG_TEXT = @{
        'category-from-carried' = 'Category comes from the carried v2 mapping (data/taxonomy.json), because the board has no Category value for these items yet. Expected until realignment window W3 sets the field.'
        'program-from-carried'  = 'Program comes from the carried v2 mapping, because no item on the board carries a Program value yet. Expected until realignment window W2 sets the field.'
        'category-inherited'    = 'Not in the carried category mapping and no Category value: inherited the nearest categorised ancestor.'
        'uncategorised'         = 'No Category value, no category parent, no carried mapping row and no categorised ancestor.'
        'epic-from-prefix'      = 'No category and no epic ancestor: the epic falls back to the WBS prefix, which names where an item was filed, not where it belongs.'
    }
    foreach ($code in @('category-from-carried', 'program-from-carried', 'category-inherited', 'uncategorised', 'epic-from-prefix')) {
        if ($agg.ContainsKey($code) -and $agg[$code].Count) { Add-Warn $code "$($agg[$code].Count) item(s). $($AGG_TEXT[$code])" $null $null @($agg[$code] | Sort-Ordinal) }
    }
    $suggest = @($nodes.Values | Where-Object { $null -ne $_.number -and -not $_.tier -and @($_.children).Count -eq 0 -and $byNum.ContainsKey([int]$_.number) -and @($byNum[[int]$_.number].bodyTierTokens).Count -eq 1 -and -not $_.delivered } | ForEach-Object { $_.id } | Sort-Ordinal)
    if ($suggest.Count) { Add-Warn 'tier-suggested-by-body' "$($suggest.Count) untiered open item(s) name exactly one tier in their body. A suggestion only: body text never sets a tier." $null $null $suggest }
    if ($unplacedWork.Count) { Add-Warn 'unplaced' "$($unplacedWork.Count) work item(s) are Unplaced: $((@($unplacedByReason.Keys | ForEach-Object { "$($unplacedByReason[$_]) $_" })) -join '; '). The hygiene panel names each one and the board edit that places it." $null $null @($unplacedWork | ForEach-Object { $_.id } | Sort-Ordinal) }
    $snapAsOf = ([string](Format-IsoDate $Snapshot.generatedAt)).Substring(0, 10)
    if ($snapAsOf -ne $AsOf) { Add-Warn 'stale-asOf' 'The board snapshot predates generation.' $null "asOf=$snapAsOf generated=$AsOf" }

    # ------------------------------------------------------------- openCalls
    $openCalls = [System.Collections.Generic.List[object]]::new()
    $openCallTargets = if ($overlay.ContainsKey('openCallTargets')) { $overlay.openCallTargets } else { @{} }
    foreach ($text in @($tiers.openOwnerCalls)) {
        $t = if ($openCallTargets.Contains([string]$text)) { $openCallTargets[[string]$text] } else { $null }
        $openCalls.Add([ordered]@{ id = $null; source = 'tiers.json'; text = [string]$text; affects = @(if ($t) { @($t.affects) | ForEach-Object { "i$_" } | Where-Object { $nodes.Contains($_) } }); wouldChange = $(if ($t) { [string]$t.wouldChange } else { 'scope' }) })
    }
    foreach ($c in @($overlay.openCalls)) {
        $aff = if ([string]$c.affects -eq 'unplaced') { @($unplacedWork | ForEach-Object { $_.id } | Sort-Ordinal) } else { @(@($c.affects) | ForEach-Object { "i$_" } | Where-Object { $nodes.Contains($_) }) }
        $openCalls.Add([ordered]@{ id = [string]$c.id; source = 'overlay.json'; text = [string]$c.text; affects = $aff; wouldChange = [string]$c.wouldChange })
    }
    foreach ($x in @($bugPolicyData.pendingExtensions)) {
        $openCalls.Add([ordered]@{ id = [string]$x.openCall; source = 'bug-policy.json'; text = "$($x.id) ($($x.from)): $($x.text)"; affects = @(); wouldChange = [string]$x.wouldChange })
    }
    foreach ($s in @($programs.openOwnerSignOffs)) {
        $m = [regex]::Match([string]$s.topic, '#(\d+)')
        $openCalls.Add([ordered]@{ id = $null; source = 'programs.json'; text = "$($s.topic): $($s.one_liner)"; affects = @(if ($m.Success -and $nodes.Contains("i$($m.Groups[1].Value)")) { "i$($m.Groups[1].Value)" }); wouldChange = 'scope' })
    }
    if ($overlay.ContainsKey('programP8Call')) {
        $openCalls.Add([ordered]@{ id = $null; source = 'overlay.json'; text = [string]$overlay.programP8Call.text; affects = @(@($overlay.programP8Call.affects) | ForEach-Object { "i$_" } | Where-Object { $nodes.Contains($_) }); wouldChange = [string]$overlay.programP8Call.wouldChange })
    }

    Write-BuildMark 'tabs'
    # ------------------------------------------------------------- tabs
    $taxonomyBlock = Get-TaxonomyBlock -Tax $tax -Categories $categories -CategoryByCode $categoryByCode -EpicByWbs $epicByWbs -Nodes $nodes -SnapItems $snapItems -ProjectFields $projectFields
    $bugBlock = Get-BugPolicyBlock -Policy $bugPolicyData -Nodes $nodes -ContainerKind $containerKind
    $realignBlock = $null
    if ($realignPlan -or $realignTarget) {
        $realignBlock = Get-RealignBlock -Plan $realignPlan -Target $realignTarget -Tax $tax -Tiers $tiers -Programs $programs -ByNum $byNum -Nodes $nodes -CategoryByCode $categoryByCode -EpicByWbs $epicByWbs -ProjectFields $projectFields -UnplacedIds @($unplacedWork | ForEach-Object { $_.id }) -Contradictions $contradictions -StateDir $RealignStateDir -SnapAsOf ([string](Format-IsoDate $Snapshot.generatedAt))
    }

    Write-BuildMark 'emit'
    # ------------------------------------------------------------------ emit
    $laneIndex = @{}; for ($i = 0; $i -lt $LANE_ORDER.Count; $i++) { $laneIndex[$LANE_ORDER[$i]] = $i }
    foreach ($nd in $nodes.Values) { foreach ($k in @('pinnedStage', 'pinnedLane')) { if ($nd.ContainsKey($k)) { $nd.Remove($k) } } }
    $nodeList = @(@($nodes.Values) | Sort-Object `
        @{ Expression = { if ($_.stage -lt 0) { 9999 } else { $_.stage } } }, `
        @{ Expression = { $laneIndex[$_.lane] } }, `
        @{ Expression = { if ($null -eq $_.number) { 1 } else { 0 } } }, `
        @{ Expression = { if ($null -eq $_.number) { 0 } else { $_.number } } }, `
        @{ Expression = { $_.id } })
    $nodeOut = @($nodeList | ForEach-Object { ConvertTo-NodeRecord $_ })
    $edgeList = @($edges | Sort-Object @{ Expression = { $_.from } }, @{ Expression = { $_.to } }, @{ Expression = { $_.kind } } | ForEach-Object {
            $o = [ordered]@{ from = $_.from; to = $_.to; kind = $_.kind; source = $_.source }
            if (@($_.alsoFrom).Count) { $o.alsoFrom = @($_.alsoFrom | Sort-Ordinal) }
            if ($_.ContainsKey('inheritedFrom')) { $o.inheritedFrom = $_.inheritedFrom }
            if ($_.ContainsKey('note')) { $o.note = $_.note }
            $o.crossLane = [bool]$_.crossLane; $o.crossBand = [bool]$_.crossBand
            if ($_.ContainsKey('critical') -and $_.critical) { $o.critical = $true }
            $o
        })

    $statusLegend = [ordered]@{
        'delivered'          = 'Board Status Done: closed and merged to main.'
        'delivered-unmerged' = "Board Status `"In review`": the work landed on integration branch $($programs.integrationBranch) (draft PR #$($programs.integrationPr)) and the GitHub issue is still OPEN. Nothing on this branch has merged to main."
        'in-progress'        = 'Board Status "In progress": work has started.'
        'ready'              = 'Board Status Ready: scoped and unblocked, not started. A Ready item that waits on open work is listed under contradictions.'
        'backlog'            = 'Board Status Backlog: filed, not scheduled.'
        'withdrawn'          = 'Closed as not planned or as a duplicate: it delivered nothing, so it is never counted as Done and is drawn in the footer.'
        'unfiled'            = 'A family the plan names at this tier with no GitHub issue behind it. Ghost cell: the filing backlog, not work in progress.'
        'out'                = 'Explicitly rejected by a recorded decision. Drawn in the footer so it cannot be rediscovered and refiled.'
        'awaiting-evidence'  = 'A program gate whose members are all closed: it stays open until its own acceptance criteria are met.'
    }
    $placementLegend = [ordered]@{
        'tier'           = 'Its Tier (board field, else the plan) puts it in a band; dependency depth, and in T2 the plan order, orders it inside the band.'
        'tier-inherited' = 'No tier of its own; it takes the tier of its nearest tiered ancestor.'
        'prerequisite'   = 'Untiered, but tiered work waits on it and its own Wave or Priority would have drawn it at or after that work: it is pulled into that band, ahead of it.'
        'dependency'     = 'Untiered, with no Wave or Priority: one column after its open dependency.'
        'wave'           = "Untiered: drawn across its Wave's window, the span of columns that wave's tiered work occupies (computed every run)."
        'priority'       = 'Untiered with no Wave: drawn across its Priority window. An estimate: Priority orders work, Wave schedules it.'
        'bars'           = 'A feature or program: a bar spanning its placed sub-issues, or its own window when none is placed.'
        'unplaced'       = 'No Tier, Wave, Priority or recorded dependency: the board has not said when.'
        'landed'         = 'Delivered work outside the tier bands: column 0.'
        'withdrawn'      = 'Closed as not planned or duplicate: the footer, never Done.'
        'out'            = 'Rejected by a recorded decision: the footer.'
    }

    $inputs = [System.Collections.Generic.List[object]]::new()
    $inputs.Add([ordered]@{ path = (Split-Path -Leaf $SnapshotPath); schema = [string]$Snapshot.schema; sha256 = (Get-Sha256 $SnapshotPath); generated = (Format-IsoDate $Snapshot.generatedAt); query = [string]$Snapshot.source.method })
    foreach ($f in @('tiers.json', 'programs.json', 'taxonomy.json', 'overlay.json', 'bug-policy.json', 'realign-plan.json', 'realign-target.json')) {
        $p = Join-Path $DataDir $f
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $j = Read-JsonFile $p
        $inputs.Add([ordered]@{ path = "data/$f"; schema = $(if ($j.Contains('schema')) { [string]$j.schema } else { $null }); sha256 = (Get-Sha256 $p); generated = $(if ($j.Contains('generated')) { [string]$j.generated } elseif ($j.Contains('version')) { [string]$j.version } elseif ($j.Contains('approvedOn')) { [string]$j.approvedOn } else { $null }) })
    }

    $carriedNote = [ordered]@{
        categoryFieldExists = $projectFields.Contains('Category'); tierFieldExists = $projectFields.Contains('Tier'); programFieldExists = $projectFields.Contains('Program')
        categoryFromCarried = @($workNodes | Where-Object { $_.categorySource -eq 'carried' }).Count
        programFromCarried  = @($workNodes | Where-Object { $_.programSource -eq 'carried' }).Count
        tierFromField       = @($workNodes | Where-Object { $_.tierSource -eq 'field' }).Count
        tierFromPlan        = @($workNodes | Where-Object { $_.tierSource -in @('plan', 'family', 'family-line') }).Count
        workItems           = $workNodes.Count
    }
    $carriedNote.text = $(
        if (-not $carriedNote.categoryFieldExists -and -not $carriedNote.programFieldExists) { "The board has no Category, Tier or Program field yet. Categories ($($carriedNote.categoryFromCarried) of $($carriedNote.workItems) work items) and programs ($($carriedNote.programFromCarried)) come from the carried v2 mapping in data/taxonomy.json, approved 2026-10-16; tiers come from the query-language plan (data/tiers.json). Every carried value raises a *-from-carried warning, and the page switches to the board fields as the realignment sets them." }
        elseif ($carriedNote.categoryFromCarried -or $carriedNote.programFromCarried) { "The board fields are partly set: $($carriedNote.categoryFromCarried) work items still take Category and $($carriedNote.programFromCarried) take Program from the carried v2 mapping (data/taxonomy.json)." }
        else { 'Category, Tier and Program are read from the board fields; the carried mapping places nothing.' }
    )

    $doc = [ordered]@{
        schema         = 'cohesion.database.roadmap/2'
        generated      = $AsOf
        generatedBy    = 'Get-CohesionRoadmapData.ps1 (cohesion-roadmap skill, read-only; no board mutations)'
        asOf           = $snapAsOf
        snapshotAt     = (Format-IsoDate $Snapshot.generatedAt)
        sources        = [ordered]@{
            inputs            = @($inputs)
            plan              = [string]$tiers.source.plan
            planDump          = [string]$tiers.source.planDump
            repo              = [string]$programs.repo
            board             = [ordered]@{ project = [int]$programs.board.project; name = [string]$programs.board.name; owner = [string]$programs.board.owner; method = [string]$Snapshot.source.method }
            integrationBranch = [string]$programs.integrationBranch
            integrationPr     = [int]$programs.integrationPr
            codeBaseline      = [string]$tiers.source.codeBaseline
            carried           = $carriedNote
        }
        legend         = [ordered]@{
            tiers      = $tiers.tierDefinitions
            statuses   = $statusLegend
            placements = $placementLegend
            laneModes  = @('language', 'category')
            decisions  = @($tiers.decisions)
        }
        placement      = [ordered]@{ rule = 'A window is the span of stages a value''s open, undelivered, tiered leaf items occupy, after dropping any band holding under 10% of them. Fewer than 3 such items: the whole of their bands. None: the band after the previous value''s window. A window never starts before the previous value''s window. Wave places; Priority places only work with no Wave.'; windows = $LAY.windows; bandSpans = [ordered]@{ landed = $LAY.span.landed; gate = $LAY.span.gate; t1 = $LAY.span.t1; t2 = $LAY.span.t2; t3 = $LAY.span.t3; unplaced = $LAY.span.unplaced }; bandOffsets = [ordered]@{ landed = $LAY.offset.landed; gate = $LAY.offset.gate; t1 = $LAY.offset.t1; t2 = $LAY.offset.t2; t3 = $LAY.offset.t3; unplaced = $LAY.offset.unplaced } }
        stages         = @($stages)
        lanes          = @($lanes)
        categories     = @($categories)
        nodes          = $nodeOut
        edges          = $edgeList
        programs       = @($programMembership.Values)
        milestones     = [ordered]@{ t1Complete = $milestones.t1Complete; gates = @($gates) }
        criticalPath   = [ordered]@{
            chains    = $chainList.ToArray()
            length    = [Math]::Max(0, $bestLen - 1)
            nodeCount = $bestLen
            sinks     = $sinks
            truncated = [bool]$truncated
            edgeSets  = [ordered]@{
                chains  = @('depends-on', 'plan-order')
                counted = @('depends-on', 'plan-order', 'program-gate')
                ignored = @('plan-sequence', 'child-of', 'conflict', 'gate-member')
                note    = 'Chains run over depends-on and plan-order edges that are not inherited, between leaves in the gate, T1, T2 and T3 bands. Blocker fan-outs (downstreamOpen) also count program-gate edges and inherited edges. plan-sequence never blocks. Quote this with any blocker number.'
            }
            blockers  = @($blockers)
        }
        nextUp         = $nextUp
        conflicts      = @($conflicts)
        contradictions = @($contradictions)
        hygiene        = @($hygiene)
        openCalls      = @($openCalls)
        overlay        = [ordered]@{ path = 'data/overlay.json'; applied = @($applied); unmatched = @($unmatched) }
        taxonomy       = $taxonomyBlock
        bugPolicy      = $bugBlock
        counts         = $counts
        warnings       = @($warnings)
    }
    if ($realignBlock) { $doc.realign = $realignBlock }
    foreach ($v in $LAY.windows.wave.Values) { if ($v.pinned) { $applied.Add([ordered]@{ node = $null; field = "window:$($v.value)"; value = $v.basis; reason = 'overlay.waveWindows' }) } }
    foreach ($v in $LAY.windows.priority.Values) { if ($v.pinned) { $applied.Add([ordered]@{ node = $null; field = "window:$($v.value)"; value = $v.basis; reason = 'overlay.waveWindows' }) } }
    $doc.overlay.applied = @($applied)
    if ($PreviousDoc) { $doc.changes = Get-ChangesBlock -Previous $PreviousDoc -Doc $doc }

    Assert-RoadmapDocument -Doc $doc
    Write-JsonFileAtomic -Object $doc -Path $OutPath -Depth 32

    Write-Step ("bands gate=$($LAY.span.gate) t1=$($LAY.span.t1) t2=$($LAY.span.t2) t3=$($LAY.span.t3) unplaced=$($LAY.span.unplaced) -> $($stages.Count) stages")
    Write-Step ("work items $($workNodes.Count); open $($openWork.Count) by placement: " + ((@($counts.open.byPlacement.Keys | ForEach-Object { "$_ $($counts.open.byPlacement[$_])" })) -join ', '))
    Write-Step ("unplaced $($unplacedWork.Count); pulled prerequisites $($pulled.Count); next up $($nextUp.total); contradictions $($contradictions.Count); hygiene $($hygiene.Count)")
    Write-Step ("critical path: $($chainList.Count) chain(s) of $bestLen nodes; sinks " + (Join-Labels $sinks $nodes 8))
    Write-Step ("nodes $($nodeOut.Count), edges $($edgeList.Count), warnings $($warnings.Count)")
    return $doc
}

# Fixed key order for every node, so successive runs diff cleanly.
function ConvertTo-NodeRecord {
    param($N)
    $o = [ordered]@{}
    foreach ($k in @('id', 'kind', 'number', 'url', 'wbs', 'title', 'shortTitle', 'lane', 'band', 'category', 'categorySource', 'epic', 'programs', 'program', 'programSource', 'family', 'tier', 'tierSource', 'planLanguage', 'planRank', 'placement', 'window', 'unplacedReason', 'stage', 'stageEnd', 'stageSource', 'stageExplanation', 'status', 'state', 'stateReason', 'delivered', 'blocked', 'untracked', 'priority', 'wave', 'area', 'areaSource', 'boardKind', 'labels', 'issueType', 'isBug', 'parent', 'children', 'externalBlockers', 'downstreamOpen', 'onCriticalPath', 'conflict', 'pinned', 'hasOpenDefect', 'openDefects')) {
        $o[$k] = $N[$k]
    }
    foreach ($k in @('complete', 'label', 'gateMembers', 'gateOpenMembers', 'note', 'evidence')) { if ($N.ContainsKey($k)) { $o[$k] = $N[$k] } }
    return $o
}

# ------------------------------------------------------------------- tab blocks

function Get-TaxonomyBlock {
    param($Tax, $Categories, $CategoryByCode, $EpicByWbs, $Nodes, $SnapItems, $ProjectFields)
    $state = if ($CategoryByCode.Count -gt 0) { 'live' } else { 'proposed' }
    $epics = [System.Collections.Generic.List[object]]::new()
    foreach ($e in @($Tax.epics)) {
        $wbs = [string]$e.wbs
        $epics.Add([ordered]@{ wbs = $wbs; issue = $(if ($EpicByWbs.Contains($wbs)) { [int]$EpicByWbs[$wbs] } else { $null }); title = [string]$e.title; source = $(if ($EpicByWbs.Contains($wbs)) { 'live' } else { 'proposed' }); categories = @($Categories | Where-Object { $_.epic -eq $wbs } | ForEach-Object { $_.id }) })
    }
    # tree versus field: items whose direct parent is a category different from their Category field
    $tvf = [System.Collections.Generic.List[object]]::new()
    $catIssue = @{}; foreach ($c in $CategoryByCode.Values) { $catIssue[[int]$c.issue] = [string]$c.code }
    foreach ($i in $SnapItems) {
        if (-not $i.boardCategory -or $null -eq $i.parent -or -not $catIssue.ContainsKey([int]$i.parent)) { continue }
        $nd = $Nodes["i$($i.number)"]
        if ($nd -and $nd.category -and $nd.category -ne $catIssue[[int]$i.parent]) { $tvf.Add([ordered]@{ number = [int]$i.number; field = $nd.category; tree = $catIssue[[int]$i.parent] }) }
    }
    $carriedRows = @($Tax.mapping).Count
    $empty = @($Categories | Where-Object { $_.itemCount -eq 0 } | ForEach-Object { $_.id })
    return [ordered]@{
        state            = $state
        stateNote        = $(if ($state -eq 'live') { "$($CategoryByCode.Count) category issue(s) exist on the board." } else { 'No [L03.02.NN.Cxx] category issue exists yet: the 42 categories are the approved proposal, and membership comes from the carried mapping until the board carries it.' })
        source           = 'data/taxonomy.json (definitions; transition mapping only) + the live board'
        approvedOn       = '2026-10-16'
        categoryCount    = @($Categories).Count
        epics            = @($epics)
        rules            = $Tax.rules
        precedence       = @($Tax.rules.precedence)
        boundaries       = $Tax.rules.boundaries
        retired          = @($Tax.retiredCategories)
        optionCeiling    = $Tax.optionCeiling
        newFields        = @($Tax.newFields)
        fieldOptions     = [ordered]@{ Category = $(if ($ProjectFields.Contains('Category')) { @($ProjectFields['Category']).Count } else { $null }); Program = $(if ($ProjectFields.Contains('Program')) { @($ProjectFields['Program']) } else { $null }); Tier = $(if ($ProjectFields.Contains('Tier')) { @($ProjectFields['Tier']) } else { $null }) }
        hardening        = $Tax.hardening
        carried          = [ordered]@{ rows = $carriedRows; approvedRows = [int]$Tax.reconciliation.mapped; postApproval = @(if ($Tax.Contains('postApproval')) { $Tax.postApproval }); treeVersusFieldPlanned = $Tax.treeVersusField.divergentItems; note = [string]$Tax.carriedNote }
        treeVersusField  = [ordered]@{ count = $tvf.Count; items = @($tvf); note = $(if ($state -eq 'live') { 'Items whose direct parent is a category different from their Category field.' } else { "Not measurable until category issues exist. The approved plan expects $($Tax.treeVersusField.divergentItems) such items after the run ($($Tax.treeVersusField.divergentAcrossEpics) across epics), the price of keeping every requirement link." }) }
        emptyCategories  = $empty
    }
}

function Get-BugPolicyBlock {
    param($Policy, $Nodes, $ContainerKind)
    $openBugs = @($Nodes.Values | Where-Object { $_.isBug -and $null -ne $_.number -and $_.state -eq 'OPEN' -and -not $_.delivered })
    $byPl = [ordered]@{}; foreach ($b in $openBugs) { Add-Tally $byPl $b.placement }
    $underClosed = @($openBugs | Where-Object { $null -ne $_.parent -and $Nodes.Contains("i$($_.parent)") -and $Nodes["i$($_.parent)"].state -eq 'CLOSED' })
    $underContainer = @($openBugs | Where-Object { $null -ne $_.parent -and $ContainerKind.ContainsKey([int]$_.parent) })
    $defectParents = @($Nodes.Values | Where-Object { $_.hasOpenDefect })
    $underWithdrawn = @($openBugs | Where-Object { $null -ne $_.parent -and $Nodes.Contains("i$($_.parent)") -and $Nodes["i$($_.parent)"].status -eq 'withdrawn' })
    $o = [ordered]@{}
    foreach ($k in $Policy.Keys) { $o[$k] = $Policy[$k] }
    $o.stats = [ordered]@{
        openBugs                = $openBugs.Count
        bugChildrenOfClosed     = $underClosed.Count
        bugsUnderEpicOrCategory = $underContainer.Count
        bugsUnderWithdrawn      = $underWithdrawn.Count
        hasOpenDefectParents    = $defectParents.Count
        byPlacement             = $byPl
        items                   = [ordered]@{
            openBugs             = @($openBugs | ForEach-Object { $_.id } | Sort-Ordinal)
            bugChildrenOfClosed  = @($underClosed | ForEach-Object { $_.id } | Sort-Ordinal)
            underEpicOrCategory  = @($underContainer | ForEach-Object { $_.id } | Sort-Ordinal)
            underWithdrawn       = @($underWithdrawn | ForEach-Object { $_.id } | Sort-Ordinal)
            hasOpenDefectParents = @($defectParents | ForEach-Object { $_.id } | Sort-Ordinal)
        }
    }
    return $o
}

function Get-RealignBlock {
    param($Plan, $Target, $Tax, $Tiers, $Programs, $ByNum, $Nodes, $CategoryByCode, $EpicByWbs, $ProjectFields, [string[]]$UnplacedIds, $Contradictions, [string]$StateDir, [string]$SnapAsOf)
    $rows = [System.Collections.Generic.List[object]]::new()
    $fieldValue = { param($it, [string]$Field)
        switch ($Field) { 'Status' { $it.boardStatus } 'Priority' { $it.boardPriority } 'Wave' { $it.boardWave } 'Category' { $it.boardCategory } 'Tier' { $it.boardTier } 'Program' { $it.boardProgram } 'Area' { $it.boardArea } 'Kind' { $it.boardKind } default { $null } }
    }
    $hasNative = { param([int]$Issue, [int]$Blocker) $it = $ByNum[$Issue]; if (-not $it) { return $false }; return [bool](@($it.dependencies) | Where-Object { [string]$_.form -eq 'native' -and [int]$_.number -eq $Blocker }) }
    $addRow = { param([string]$Stage, [string]$Op, $Issue, [string]$Target, $Current, [string]$State, [string]$Note)
        $r = [ordered]@{ stage = $Stage; op = $Op; issue = $Issue; target = $Target; current = $(if ($null -eq $Current) { $null } else { [string]$Current }); state = $State }
        if ($Note) { $r.note = $Note }
        $rows.Add($r)
    }
    # done: the board holds the target. pending: empty, or still the value the plan was approved
    # against ($From). diverged: any other value.
    $fieldCheck = { param([string]$Stage, [string]$Op, [int]$Issue, [string]$Field, [string]$Value, [string]$Note, [string]$From)
        $it = $ByNum[$Issue]
        if (-not $it) { & $addRow $Stage $Op $Issue "$Field = $Value" $null 'pending' "#$Issue is not in the snapshot."; return }
        $cur = & $fieldValue $it $Field
        $st = if ([string]$cur -eq $Value) { 'done' } elseif ($null -eq $cur -or [string]$cur -eq '' -or ($From -and [string]$cur -eq $From)) { 'pending' } else { 'diverged' }
        & $addRow $Stage $Op $Issue "$Field = $Value" $cur $st $Note
    }

    if ($Plan) {
        # board corrections and the execution seam: explicit checks
        foreach ($c in @($Plan.corrections)) {
            $stage = [string]$c.id
            if ($c.ContainsKey('field') -and $c.field -eq 'PriorityAndWave') {
                $left = @($UnplacedIds | Where-Object { $Nodes.Contains($_) -and $null -ne $Nodes[$_].number })
                & $addRow $stage 'priority-and-wave' $null 'every Unplaced item has a Priority or Wave' "$($left.Count) Unplaced" $(if ($left.Count) { 'pending' } else { 'done' }) $c.note
                continue
            }
            if ($c.ContainsKey('blockedBy')) { $ok = & $hasNative ([int]$c.issue) ([int]$c.blockedBy); & $addRow $stage 'link' ([int]$c.issue) "blocked by #$($c.blockedBy)" $(if ($ok) { "blocked by #$($c.blockedBy)" } else { 'no link' }) $(if ($ok) { 'done' } else { 'pending' }) $c.note; continue }
            if ($c.ContainsKey('state')) { $it = $ByNum[[int]$c.issue]; $cur = if ($it) { [string]$it.state } else { $null }; & $addRow $stage 'state' ([int]$c.issue) "state $($c.state)" $cur $(if ($cur -eq [string]$c.state) { 'done' } else { 'diverged' }) $c.note; continue }
            & $fieldCheck $stage 'field' ([int]$c.issue) ([string]$c.field) ([string]$c.value) $c.note
        }
        if ($Plan.ContainsKey('seam')) {
            foreach ($c in @($Plan.seam.checks)) {
                $n = [int]$c.issue
                if ($c.ContainsKey('exists')) {
                    $it = $ByNum[$n]
                    $ok = [bool]$it; $parentOk = ($ok -and $c.ContainsKey('parent') -and $null -ne $it.parent -and [int]$it.parent -eq [int]$c.parent)
                    & $addRow 'seam' 'filed' $n "filed under #$($c.parent)" $(if ($ok) { "under #$($it.parent)" } else { 'not filed' }) $(if ($ok -and $parentOk) { 'done' } elseif ($ok) { 'diverged' } else { 'pending' }) $null
                    continue
                }
                if ($c.ContainsKey('blockedBy')) { $ok = & $hasNative $n ([int]$c.blockedBy); & $addRow 'seam' 'link' $n "blocked by #$($c.blockedBy)" $(if ($ok) { "blocked by #$($c.blockedBy)" } else { 'no link' }) $(if ($ok) { 'done' } else { 'pending' }) $null; continue }
                & $fieldCheck 'seam' 'field' $n ([string]$c.field) ([string]$c.value) $null
            }
        }
    }

    # the window operations: from realign-target.json when the executor exported it, else derived
    # from the approved carried data
    $targetSource = if ($Target) { 'data/realign-target.json' } else { 'derived from the approved carried data (data/taxonomy.json mapping, programs.json gate, tiers.json plan); the executor has not exported realign-target.json yet' }
    $catIssueByCode = @{}; foreach ($c in $CategoryByCode.Values) { $catIssueByCode[[string]$c.code] = [int]$c.issue }
    $catNameOf = @{}; foreach ($c in @($Tax.categories)) { $catNameOf[[string]$c.code] = [string]$c.name }
    if ($Plan) {
        foreach ($w in @($Plan.windows)) {
            $wid = [string]$w.id
            foreach ($op in @($w.ops)) {
                switch ([string]$op.type) {
                    'fields' {
                        foreach ($f in @($op.fields)) { & $addRow $wid 'create-field' $null "field $f exists" $(if ($ProjectFields.Contains([string]$f)) { 'exists' } else { 'missing' }) $(if ($ProjectFields.Contains([string]$f)) { 'done' } else { 'pending' }) $(if ($f -eq 'Category') { "with at least $(@($Tax.categories).Count) options (decision 20)" } else { $null }) }
                    }
                    'areaOptions' {
                        $has = ($ProjectFields.Contains('Area') -and @($ProjectFields['Area'] | Where-Object { $_ -like "$($op.prefix)*" }).Count -gt 0)
                        & $addRow $wid 'area-options' $null "Area has $($op.prefix)* options" $(if ($has) { 'present' } else { 'absent' }) $(if ($has) { 'done' } else { 'pending' }) $null
                    }
                    'epic' {
                        $wbs = [string]$op.wbs
                        $ok = $EpicByWbs.Contains($wbs)
                        & $addRow $wid 'create-epic' $(if ($ok) { [int]$EpicByWbs[$wbs] } else { $null }) "epic [$wbs] under #1067" $(if ($ok) { "#$($EpicByWbs[$wbs])" } else { 'missing' }) $(if ($ok) { 'done' } else { 'pending' }) $null
                    }
                    'create' {
                        foreach ($c in @($Tax.categories | Where-Object { @($op.epics) -contains [string]$_.epic })) {
                            $code = [string]$c.code
                            if ($CategoryByCode.Contains($code)) {
                                $live = $CategoryByCode[$code]
                                $under = ($EpicByWbs.Contains([string]$c.epic) -and $null -ne $live.parent -and [int]$live.parent -eq [int]$EpicByWbs[[string]$c.epic])
                                & $addRow $wid 'create-category' ([int]$live.issue) "[$code] $($c.name)" "#$($live.issue) under #$($live.parent)" $(if ($under) { 'done' } else { 'diverged' }) $null
                            }
                            else { & $addRow $wid 'create-category' $null "[$code] $($c.name)" 'missing' 'pending' $null }
                        }
                    }
                    'reparent' {
                        foreach ($m in @($Tax.mapping)) {
                            if ([string]$m.mutation -notin @('reparent-in-epic', 'reparent-cross-epic')) { continue }
                            $cross = ([string]$m.mutation -eq 'reparent-cross-epic')
                            if ($op.ContainsKey('crossEpic') -and [bool]$op.crossEpic -ne $cross) { continue }
                            $targetCode = [string]$m.plannedParent
                            $tEpic = ($targetCode.Split('.')[0..2]) -join '.'
                            if (-not $cross -and $op.ContainsKey('epics') -and @($op.epics) -notcontains $tEpic) { continue }
                            $it = $ByNum[[int]$m.number]
                            $cur = if ($it) { $it.parent } else { $null }
                            $want = if ($catIssueByCode.ContainsKey($targetCode)) { $catIssueByCode[$targetCode] } else { $null }
                            $st = if ($null -ne $want -and $null -ne $cur -and [int]$cur -eq $want) { 'done' } elseif ($null -ne $cur -and [int]$cur -eq [int]$m.currentParent) { 'pending' } elseif ($null -eq $want) { 'pending' } else { 'diverged' }
                            & $addRow $wid $(if ($cross) { 'reparent-cross-epic' } else { 'reparent' }) ([int]$m.number) "under $targetCode" $(if ($null -ne $cur) { "#$cur" } else { $null }) $st $null
                        }
                    }
                    'gateLinks' {
                        foreach ($b in @($op.blockers)) { $g = [int]$Programs.programs.Where({ $_.id -eq [string]$op.program })[0].gate.issue; $ok = & $hasNative $g ([int]$b); & $addRow $wid 'gate-link' $g "blocked by #$b" $(if ($ok) { 'linked' } else { 'no link' }) $(if ($ok) { 'done' } else { 'pending' }) $null }
                    }
                    'bodyAppend' {
                        $it = $ByNum[[int]$op.issue]
                        $ok = ($it -and @($it.bodyHeadings) -contains [string]$op.marker)
                        & $addRow $wid 'body-append' ([int]$op.issue) "section '$($op.marker)'" $(if ($ok) { 'present' } else { 'absent' }) $(if ($ok) { 'done' } else { 'pending' }) $null
                    }
                    'issueType' {
                        $untyped = @($Tax.mapping | Where-Object { $ByNum.ContainsKey([int]$_.number) -and -not $ByNum[[int]$_.number].issueType })
                        & $addRow $wid 'issue-type' $null 'every mapped item has an issue type' "$($untyped.Count) untyped" $(if ($untyped.Count) { 'pending' } else { 'done' }) $null
                    }
                    'field' {
                        $f = [string]$op.field
                        if ($op.ContainsKey('issue')) { & $fieldCheck $wid 'field' ([int]$op.issue) $f ([string]$op.value) $null $(if ($op.ContainsKey('from')) { [string]$op.from } else { $null }); continue }
                        if ($f -in @('Category', 'Area', 'Program')) {
                            foreach ($m in @($Tax.mapping)) {
                                $want = switch ($f) { 'Category' { $catNameOf[[string]$m.category] } 'Area' { [string]$m.area } 'Program' { if ($m.Contains('program')) { [string]$m.program } else { $null } } }
                                if (-not $want) { continue }
                                if ($m.Contains('mutation') -and [string]$m.mutation -eq 'none-closed-not-delivered' -and $f -ne 'Category' -and $f -ne 'Area') { continue }
                                & $fieldCheck $wid 'field' ([int]$m.number) $f $want $null
                            }
                        }
                        elseif ($f -eq 'Tier') {
                            $opt = @{}; foreach ($k in $Tiers.tierBoardOptions.Keys) { if ($null -ne $Tiers.tierBoardOptions[$k]) { $opt[[string]$Tiers.tierBoardOptions[$k]] = [string]$k } }
                            foreach ($ti in @($Tiers.items)) { if ($ti.tier -and $opt.ContainsKey([string]$ti.tier)) { & $fieldCheck $wid 'field' ([int]$ti.number) 'Tier' $opt[[string]$ti.tier] $null } }
                        }
                    }
                }
            }
        }
    }
    elseif ($Target) {
        foreach ($c in @($Target.creates)) { $code = [string]$c.code; $ok = $CategoryByCode.Contains($code); & $addRow ([string]$c.stage) 'create-category' $(if ($ok) { [int]$CategoryByCode[$code].issue } else { $null }) "[$code] $($c.name)" $(if ($ok) { 'exists' } else { 'missing' }) $(if ($ok) { 'done' } else { 'pending' }) $null }
        foreach ($w in @($Target.writes)) { & $fieldCheck ([string]$w.stage) 'field' ([int]$w.issue) ([string]$w.field) ([string]$w.value) $null }
        foreach ($r in @($Target.reparents)) { $want = if ($CategoryByCode.Contains([string]$r.to)) { [int]$CategoryByCode[[string]$r.to].issue } else { $null }; $it = $ByNum[[int]$r.issue]; $cur = if ($it) { $it.parent } else { $null }; & $addRow ([string]$r.stage) 'reparent' ([int]$r.issue) "under $($r.to)" $(if ($null -ne $cur) { "#$cur" } else { $null }) $(if ($null -ne $want -and $cur -eq $want) { 'done' } else { 'pending' }) $null }
        foreach ($l in @($Target.links)) { $ok = & $hasNative ([int]$l.issue) ([int]$l.blockedBy); & $addRow ([string]$l.stage) 'gate-link' ([int]$l.issue) "blocked by #$($l.blockedBy)" $(if ($ok) { 'linked' } else { 'no link' }) $(if ($ok) { 'done' } else { 'pending' }) $null }
        foreach ($b in @($Target.bodyAppends)) { $it = $ByNum[[int]$b.issue]; $ok = ($it -and @($it.bodyHeadings) -contains [string]$b.marker); & $addRow ([string]$b.stage) 'body-append' ([int]$b.issue) "section '$($b.marker)'" $(if ($ok) { 'present' } else { 'absent' }) $(if ($ok) { 'done' } else { 'pending' }) $null }
    }

    # summary per stage
    $byStage = [ordered]@{}
    foreach ($r in $rows) {
        if (-not $byStage.Contains($r.stage)) { $byStage[$r.stage] = [ordered]@{ done = 0; pending = 0; diverged = 0; total = 0 } }
        $byStage[$r.stage][$r.state] = $byStage[$r.stage][$r.state] + 1
        $byStage[$r.stage].total++
    }
    $windowIds = @(if ($Plan) { @($Plan.windows) | ForEach-Object { [string]$_.id } })
    $windowsRun = @($windowIds | Where-Object { $byStage.Contains($_) -and $byStage[$_].done -eq $byStage[$_].total }).Count
    $windowsStarted = @($windowIds | Where-Object { $byStage.Contains($_) -and $byStage[$_].done -gt 0 }).Count
    $complete = ($windowIds.Count -gt 0 -and $windowsRun -eq $windowIds.Count)
    $corrIds = @($byStage.Keys | Where-Object { $_ -like 'D*' })
    $corrDone = @($rows | Where-Object { $_.stage -like 'D*' -and $_.state -eq 'done' }).Count
    $corrTotal = @($rows | Where-Object { $_.stage -like 'D*' }).Count
    $seamDone = @($rows | Where-Object { $_.stage -eq 'seam' -and $_.state -eq 'done' }).Count
    $seamTotal = @($rows | Where-Object { $_.stage -eq 'seam' }).Count
    $appliedDecisions = @(@('D11', 'D12', 'D13') | Where-Object { $byStage.Contains($_) -and $byStage[$_].done -eq $byStage[$_].total })
    $headline = if ($complete) { "Realignment complete, verified against the board on $SnapAsOf." }
    elseif ($windowsStarted -eq 0) { "Approved $($Plan.approvedOn), and no board window has run: 0 of $($windowIds.Count) windows started." }
    else { "Approved $($Plan.approvedOn); $windowsRun of $($windowIds.Count) windows complete, $windowsStarted started." }
    $sub = [System.Collections.Generic.List[string]]::new()
    $decText = [System.Collections.Generic.List[string]]::new()
    foreach ($d in @($byStage.Keys | Where-Object { $_ -like 'D*' })) {
        $s = $byStage[$d]
        $one = @($rows | Where-Object { $_.stage -eq $d })
        $decText.Add("decision $($d.Substring(1)): " + $(if ($s.done -eq $s.total) { "in effect ($($s.done) of $($s.total) checks)" } elseif ($s.diverged) { "$($s.diverged) of $($s.total) checks diverged" } elseif ($s.total -eq 1 -and $one[0].current) { "pending ($($one[0].current))" } else { "$($s.pending) of $($s.total) checks pending" }))
    }
    if ($decText.Count) { $sub.Add('Board corrections, read from the board: ' + ($decText -join '; ') + '.') }
    if ($seamTotal) { $sub.Add("$($Plan.seam.title): " + $(if ($seamDone -eq $seamTotal) { "filed and linked ($seamDone of $seamTotal checks done)" } else { "$seamDone of $seamTotal checks done" }) + '.') }

    $journal = $null
    if ($StateDir -and (Test-Path -LiteralPath (Join-Path $StateDir 'journal.jsonl'))) {
        $last = [ordered]@{}
        foreach ($line in (Get-Content -LiteralPath (Join-Path $StateDir 'journal.jsonl') -Encoding utf8)) {
            if (-not $line.Trim()) { continue }
            try { $j = $line | ConvertFrom-Json -AsHashtable -DateKind String } catch { continue }
            if ($j.ContainsKey('stage') -and $j.ContainsKey('ts')) { $last[[string]$j.stage] = [string]$j.ts }
        }
        $journal = $last
    }
    $windowsOut = @(if ($Plan) { @($Plan.windows) | ForEach-Object {
                $s = if ($byStage.Contains([string]$_.id)) { $byStage[[string]$_.id] } else { [ordered]@{ done = 0; pending = 0; diverged = 0; total = 0 } }
                $o = [ordered]@{ id = [string]$_.id; name = [string]$_.name; requests = $_.requests; endState = [string]$_.endState; done = $s.done; pending = $s.pending; diverged = $s.diverged; total = $s.total; state = $(if ($s.total -and $s.done -eq $s.total) { 'complete' } elseif ($s.done) { 'started' } else { 'not started' }) }
                if ($journal -and $journal.Contains([string]$_.id)) { $o.lastApplied = $journal[[string]$_.id] }
                $o
            } })
    return [ordered]@{
        source        = $targetSource
        approvedOn    = $(if ($Plan) { [string]$Plan.approvedOn } else { $null })
        approvedBy    = $(if ($Plan) { [string]$Plan.approvedBy } else { $null })
        executorState = $(if ($Plan) { [string]$Plan.executorState } else { $null })
        verifiedAsOf  = $SnapAsOf
        headline      = $headline
        detail        = @($sub)
        summary       = [ordered]@{ byStage = $byStage; windowsRun = $windowsRun; windowsStarted = $windowsStarted; windowCount = $windowIds.Count; complete = $complete; verifiedAsOf = $SnapAsOf; corrections = [ordered]@{ done = $corrDone; total = $corrTotal }; seam = [ordered]@{ done = $seamDone; total = $seamTotal } }
        windows       = $windowsOut
        decisions     = @(if ($Plan) { @($Plan.decisions) })
        seam          = $(if ($Plan -and $Plan.ContainsKey('seam')) { [ordered]@{ title = [string]$Plan.seam.title; note = [string]$Plan.seam.note } } else { $null })
        windowsNote   = $(if ($Plan) { [string]$Plan.windowsNote } else { $null })
        rows          = @($rows)
        journal       = $journal
    }
}

function Get-ChangesBlock {
    param($Previous, $Doc)
    $prevNodes = @{}; foreach ($n in @($Previous.nodes)) { $prevNodes[[string]$n.id] = $n }
    $curNodes = @{}; foreach ($n in @($Doc.nodes)) { $curNodes[[string]$n.id] = $n }
    $prevOffset = @{}; foreach ($s in @($Previous.stages)) { if (-not $prevOffset.ContainsKey([string]$s.band)) { $prevOffset[[string]$s.band] = [int]$s.index } }
    $curOffset = @{}; foreach ($s in @($Doc.stages)) { if (-not $curOffset.ContainsKey([string]$s.band)) { $curOffset[[string]$s.band] = [int]$s.index } }
    $moved = [System.Collections.Generic.List[object]]::new(); $added = [System.Collections.Generic.List[object]]::new(); $removed = [System.Collections.Generic.List[object]]::new(); $statusChanged = [System.Collections.Generic.List[object]]::new()
    foreach ($id in @($curNodes.Keys | Sort-Ordinal)) {
        $c = $curNodes[$id]
        if (-not $prevNodes.ContainsKey($id)) { $added.Add([ordered]@{ id = $id; stage = $c.stage; placement = $c.placement }); continue }
        $p = $prevNodes[$id]
        if ([string]$p.status -ne [string]$c.status) { $statusChanged.Add([ordered]@{ id = $id; from = [string]$p.status; to = [string]$c.status }) }
        if ([int]$p.stage -eq [int]$c.stage -and [int]$p.stageEnd -eq [int]$c.stageEnd) { continue }
        $reasons = [System.Collections.Generic.List[string]]::new()
        if ([string]$p.tier -ne [string]$c.tier) { $reasons.Add('tier') }
        if ([string]$p.placement -ne [string]$c.placement) { $reasons.Add('placement') }
        if ([string]$p.status -ne [string]$c.status) { $reasons.Add('status') }
        if ([string]$p.wave -ne [string]$c.wave) { $reasons.Add('wave') }
        if ([string]$p.priority -ne [string]$c.priority) { $reasons.Add('priority') }
        if ([bool]$p.pinned -ne [bool]$c.pinned) { $reasons.Add('pin') }
        if ($c.window -and $p.window -and ([int]$p.window.lo -ne [int]$c.window.lo -or [int]$p.window.hi -ne [int]$c.window.hi) -and [string]$p.window.value -eq [string]$c.window.value) { $reasons.Add('window-recalibrated') }
        $b = [string]$c.band
        if ($prevOffset.ContainsKey($b) -and $curOffset.ContainsKey($b) -and $prevOffset[$b] -ne $curOffset[$b]) { $reasons.Add('band-resized') }
        if ($c.placement -in @('tier', 'tier-inherited', 'prerequisite') -and $prevOffset.ContainsKey($b) -and $curOffset.ContainsKey($b) -and ([int]$p.stage - $prevOffset[$b]) -ne ([int]$c.stage - $curOffset[$b])) { $reasons.Add('dependency') }
        if ([string]$p.stageSource -ne [string]$c.stageSource -and -not $reasons.Contains('dependency')) { $reasons.Add('dependency') }
        if ($c.placement -eq 'bars') {
            # A bar spans its sub-issues: it moves when one of them moves, changes status, or joins or leaves it.
            $pk = @(@($p.children) | ForEach-Object { [int]$_ } | Sort-Object); $ck = @(@($c.children) | ForEach-Object { [int]$_ } | Sort-Object)
            $subMoved = (($pk -join ',') -ne ($ck -join ','))
            if (-not $subMoved) {
                foreach ($ch in $ck) {
                    $chId = "i$ch"
                    if (-not ($prevNodes.ContainsKey($chId) -and $curNodes.ContainsKey($chId))) { continue }
                    $pc = $prevNodes[$chId]; $cc = $curNodes[$chId]
                    if ([int]$pc.stage -ne [int]$cc.stage -or [int]$pc.stageEnd -ne [int]$cc.stageEnd -or [string]$pc.status -ne [string]$cc.status) { $subMoved = $true; break }
                }
            }
            if ($subMoved) { $reasons.Add('sub-issues') }
        }
        if (-not $reasons.Count) { $reasons.Add('unexplained') }
        $moved.Add([ordered]@{ id = $id; from = [ordered]@{ stage = [int]$p.stage; stageEnd = [int]$p.stageEnd; placement = [string]$p.placement }; to = [ordered]@{ stage = [int]$c.stage; stageEnd = [int]$c.stageEnd; placement = [string]$c.placement }; reasons = @($reasons) })
    }
    foreach ($id in @($prevNodes.Keys | Sort-Ordinal)) { if (-not $curNodes.ContainsKey($id)) { $removed.Add([ordered]@{ id = $id; stage = $prevNodes[$id].stage; title = [string]$prevNodes[$id].title }) } }
    return [ordered]@{ previous = [ordered]@{ generated = [string]$Previous.generated; asOf = [string]$Previous.asOf }; moved = @($moved); added = @($added); removed = @($removed); statusChanged = @($statusChanged); unexplained = @($moved | Where-Object { @($_.reasons) -contains 'unexplained' }).Count }
}

# ------------------------------------------------------------------ validate

function Assert-RoadmapDocument {
    param([Parameter(Mandatory)]$Doc)

    $problems = [System.Collections.Generic.List[string]]::new()
    $required = @('schema', 'generated', 'generatedBy', 'asOf', 'sources', 'legend', 'placement', 'stages', 'lanes', 'categories', 'nodes', 'edges', 'programs', 'milestones', 'criticalPath', 'nextUp', 'conflicts', 'contradictions', 'hygiene', 'openCalls', 'overlay', 'taxonomy', 'bugPolicy', 'counts', 'warnings')
    foreach ($k in $required) { if (-not $Doc.Contains($k)) { $problems.Add("missing top-level key '$k'") } }
    if ($Doc.schema -ne 'cohesion.database.roadmap/2') { $problems.Add("schema is '$($Doc.schema)', expected cohesion.database.roadmap/2") }

    $nodeIds = [System.Collections.Generic.HashSet[string]]::new()
    $KINDS = @('program', 'feature', 'task', 'slot', 'milestone', 'gate')
    $STATUSES = @('delivered', 'delivered-unmerged', 'in-progress', 'ready', 'backlog', 'withdrawn', 'unfiled', 'out', 'awaiting-evidence')
    $PLACEMENTS = @('tier', 'tier-inherited', 'prerequisite', 'dependency', 'wave', 'priority', 'bars', 'unplaced', 'landed', 'withdrawn', 'out', 'gate', 'milestone')
    $laneIds = @(@($Doc.lanes) | ForEach-Object { $_.id })
    $maxStage = -1
    foreach ($s in @($Doc.stages)) { if ([int]$s.index -gt $maxStage) { $maxStage = [int]$s.index } }
    $lastPositioned = [int]$Doc.placement.bandOffsets.unplaced - 1
    $unplacedStage = @($Doc.stages | Where-Object { $_.band -eq 'unplaced' })

    foreach ($n in @($Doc.nodes)) {
        if (-not $nodeIds.Add([string]$n.id)) { $problems.Add("duplicate node id $($n.id)") }
        if ($n.kind -notin $KINDS) { $problems.Add("$($n.id): unknown kind '$($n.kind)'") }
        if ($n.status -notin $STATUSES) { $problems.Add("$($n.id): unknown status '$($n.status)'") }
        if ($n.placement -notin $PLACEMENTS) { $problems.Add("$($n.id): no placement (got '$($n.placement)')") }
        if ($n.lane -notin $laneIds) { $problems.Add("$($n.id): lane '$($n.lane)' is not a declared lane") }
        if ($n.placement -in @('withdrawn', 'out')) { if ($n.stage -ne -1) { $problems.Add("$($n.id): footer node with stage $($n.stage)") } }
        elseif ($n.stage -lt 0 -or $n.stage -gt $maxStage) { $problems.Add("$($n.id): stage $($n.stage) outside 0..$maxStage") }
        if ($n.stageEnd -lt $n.stage) { $problems.Add("$($n.id): stageEnd $($n.stageEnd) precedes stage $($n.stage)") }
        if (-not $n.stageExplanation) { $problems.Add("$($n.id): no stageExplanation - every position must be auditable") }
        if ($null -ne $n.number -and -not $n.url) { $problems.Add("$($n.id): a filed item with no url") }
        if ($n.status -eq 'withdrawn' -and $n.delivered) { $problems.Add("$($n.id): withdrawn but counted as delivered") }
        if ($n.window -and ([int]$n.window.lo -lt 0 -or [int]$n.window.lo -gt [int]$n.window.hi -or [int]$n.window.hi -gt $lastPositioned)) { $problems.Add("$($n.id): window $($n.window.lo)-$($n.window.hi) outside 0..$lastPositioned") }
        if ($n.placement -eq 'unplaced' -and -not $n.unplacedReason) { $problems.Add("$($n.id): unplaced with no reason") }
    }
    foreach ($kv in $Doc.placement.windows.GetEnumerator()) { foreach ($w in $kv.Value.Values) { if ([int]$w.lo -lt 0 -or [int]$w.lo -gt [int]$w.hi -or [int]$w.hi -gt $lastPositioned) { $problems.Add("window $($w.value): $($w.lo)-$($w.hi) outside 0..$lastPositioned") } } }
    $byId = @{}; foreach ($n in @($Doc.nodes)) { $byId[[string]$n.id] = $n }
    foreach ($e in @($Doc.edges)) {
        if (-not $nodeIds.Contains([string]$e.from)) { $problems.Add("edge $($e.from)->$($e.to): unknown source") }
        if (-not $nodeIds.Contains([string]$e.to)) { $problems.Add("edge $($e.from)->$($e.to): unknown target") }
        if ($e.kind -eq 'plan-sequence' -and ($byId[[string]$e.from].tier -ne 'T2' -or $byId[[string]$e.to].tier -ne 'T2')) { $problems.Add("plan-sequence edge $($e.from)->$($e.to) does not join two T2 nodes") }
    }
    foreach ($c in @($Doc.criticalPath.chains)) { foreach ($id in @($c)) { if (-not $nodeIds.Contains([string]$id)) { $problems.Add("criticalPath names unknown node $id") } } }
    foreach ($g in @($Doc.milestones.gates)) {
        $gn = $byId[[string]$g.node]
        foreach ($m in @($gn.gateMembers)) { $mn = $byId[[string]$m]; if ($mn -and $mn.placement -notin @('unplaced', 'withdrawn', 'out') -and [int]$mn.stageEnd -gt [int]$gn.stage) { $problems.Add("gate $($g.node) at $($gn.stage) precedes member $m (stageEnd $($mn.stageEnd))") } }
    }
    if (@($Doc.stages).Count -lt 2) { $problems.Add('fewer than two stages - the band computation collapsed') }
    if (@($Doc.nodes).Count -lt 50) { $problems.Add("only $(@($Doc.nodes).Count) nodes - the inputs did not join") }
    if ([int]$Doc.counts.boardItems -lt 50) { $problems.Add("counts.boardItems is $($Doc.counts.boardItems) - the snapshot looks partial") }

    # every board item is a container or appears exactly once as a node
    $filed = @(@($Doc.nodes) | Where-Object { $null -ne $_.number }).Count
    $containers = [int]$Doc.counts.containers.root + [int]$Doc.counts.containers.epics + [int]$Doc.counts.containers.categories
    if ($filed + $containers -ne [int]$Doc.counts.boardItems) { $problems.Add("$filed filed nodes (work items + gates) + $containers containers != $($Doc.counts.boardItems) board items") }
    $work = @(@($Doc.nodes) | Where-Object { $null -ne $_.number -and $_.kind -ne 'gate' })
    if ($work.Count -ne [int]$Doc.counts.workItems) { $problems.Add("counts.workItems $($Doc.counts.workItems) != $($work.Count) work-item nodes") }
    $unplacedNodes = @($work | Where-Object { $_.placement -eq 'unplaced' }).Count
    if ([int]$Doc.counts.unplaced.total -ne $unplacedNodes) { $problems.Add("counts.unplaced.total $($Doc.counts.unplaced.total) != $unplacedNodes nodes placed 'unplaced'") }
    $colCount = if ($unplacedStage.Count) { [int]$unplacedStage[0].nodeCount } else { 0 }
    $unplacedAll = @(@($Doc.nodes) | Where-Object { $_.placement -eq 'unplaced' }).Count
    if ($colCount -ne $unplacedAll) { $problems.Add("the Unplaced column's nodeCount $colCount != $unplacedAll unplaced nodes") }
    $unplacedWarn = @(@($Doc.warnings) | Where-Object { $_.code -eq 'unplaced' })
    if ($unplacedNodes -and (-not $unplacedWarn.Count -or [int]$unplacedWarn[0].count -ne $unplacedNodes)) { $problems.Add('the unplaced warning does not read counts.unplaced') }
    $sumOpen = 0; foreach ($k in $Doc.counts.open.byPlacement.Keys) { $sumOpen += [int]$Doc.counts.open.byPlacement[$k] }
    if ($sumOpen -ne [int]$Doc.counts.open.total) { $problems.Add("open.byPlacement sums to $sumOpen, open.total is $($Doc.counts.open.total)") }

    if ($problems.Count) {
        $detail = (@($problems | Select-Object -First 25)) -join "`n  - "
        throw "The roadmap document failed validation ($($problems.Count) problems); nothing was written.`n  - $detail"
    }
}

# ---------------------------------------------------------------------- html

# The page reads a compact projection of the document: short keys, no child-of edges (node.p
# reconstructs them). Keep this in step with assets/roadmap-template.html - the two are one contract.
function New-RoadmapPayload {
    param([Parameter(Mandatory)]$Doc)

    $p = [ordered]@{}
    $p.meta = [ordered]@{
        generated         = $Doc.generated
        asOf              = $Doc.asOf
        snapshotAt        = $Doc.snapshotAt
        repo              = $Doc.sources.repo
        board             = $Doc.sources.board.project
        integrationBranch = $Doc.sources.integrationBranch
        integrationPr     = $Doc.sources.integrationPr
        codeBaseline      = $Doc.sources.codeBaseline
        plan              = $Doc.sources.plan
        carried           = $Doc.sources.carried
        inputs            = @(@($Doc.sources.inputs) | ForEach-Object { [ordered]@{ path = $_.path; sha = $(if ($_.sha256) { ([string]$_.sha256).Substring(0, 12) } else { $null }); generated = $_.generated } })
    }
    foreach ($k in 'legend', 'placement', 'stages', 'lanes', 'counts', 'criticalPath', 'conflicts', 'openCalls', 'warnings', 'programs', 'milestones', 'nextUp', 'contradictions', 'hygiene', 'taxonomy', 'bugPolicy') { $p[$k] = $Doc[$k] }
    if ($Doc.Contains('realign')) { $p.realign = $Doc.realign }
    if ($Doc.Contains('changes')) { $p.changes = $Doc.changes }
    $p.overlay = $Doc.overlay

    $p.nodes = @(@($Doc.nodes) | ForEach-Object {
            $n = $_
            $o = [ordered]@{ i = $n.id }
            if ($null -ne $n.number) { $o.n = $n.number }
            $o.k = $n.kind
            if ($n.wbs) { $o.w = $n.wbs }
            $o.t = $n.title
            $o.ln = $n.lane
            $o.bd = $n.band
            if ($n.category) { $o.c = $n.category; $o.cs = $n.categorySource }
            if ($n.epic) { $o.e = $n.epic }
            if (@($n.programs).Count) { $o.pgs = @($n.programs) }
            if ($n.program) { $o.pg = $n.program; $o.ps = $n.programSource }
            if ($n.family) { $o.fm = $n.family }
            if ($n.tier) { $o.tr = $n.tier }
            $o.tsx = $n.tierSource
            if ($n.planLanguage) { $o.lg = $n.planLanguage }
            if ($n.planRank) { $o.rk = @($n.planRank.language, $n.planRank.rank, $n.planRank.of) }
            $o.pl = $n.placement
            if ($n.window) { $o.wf = $n.window.value; $o.wl = $n.window.lo; $o.wh = $n.window.hi }
            if ($n.unplacedReason) { $o.ur = $n.unplacedReason }
            $o.s = $n.stage; $o.se = $n.stageEnd; $o.ss = $n.stageSource
            if ($n.stageExplanation) { $o.sx = $n.stageExplanation }
            $o.st = $n.status
            if ($n.state) { $o.gh = $n.state }
            $o.d = [int][bool]$n.delivered
            $o.b = [int][bool]$n.blocked
            if ($n.untracked) { $o.u = 1 }
            if ($n.priority) { $o.pr = $n.priority }
            if ($n.wave) { $o.wv = $n.wave }
            if ($n.area) { $o.ar = $n.area }
            if ($n.issueType) { $o.it = $n.issueType }
            if ($n.isBug) { $o.bug = 1 }
            if ($null -ne $n.parent) { $o.p = $n.parent }
            if (@($n.children).Count) { $o.ch = @($n.children) }
            if (@($n.externalBlockers).Count) { $o.xb = @($n.externalBlockers | ForEach-Object { [ordered]@{ n = $_.number; t = $_.title; gh = $_.state; st = $_.boardStatus } }) }
            $o.dn = [int]$n.downstreamOpen
            if ($n.onCriticalPath) { $o.cp = 1 }
            if ($n.conflict) { $o.cf = $n.conflict }
            if ($n.hasOpenDefect) { $o.hd = [int]$n.openDefects }
            if ($n.pinned) { $o.pin = 1 }
            if ($n.Contains('complete') -and $n.complete) { $o.cm = 1 }
            if ($n.Contains('label')) { $o.lb = $n.label }
            if ($n.Contains('gateMembers')) { $o.gm = @($n.gateMembers) }
            if ($n.Contains('note') -and $n.note) { $o.nt = $n.note }
            $o
        })
    $p.edges = @(@($Doc.edges) | Where-Object { $_.kind -ne 'child-of' } | ForEach-Object {
            $e = [ordered]@{ f = $_.from; t = $_.to; k = $_.kind; s = $_.source }
            if ($_.crossLane) { $e.x = 1 }
            if ($_.Contains('critical') -and $_.critical) { $e.c = 1 }
            if ($_.Contains('inheritedFrom')) { $e.ih = $_.inheritedFrom }
            if ($_.Contains('note')) { $e.nt = $_.note }
            $e
        })
    $p.categories = @($Doc.categories)
    return $p
}

function Write-RoadmapHtml {
    param([Parameter(Mandatory)]$Payload, [Parameter(Mandatory)][string]$OutPath)
    $template = Join-Path $AssetDir 'roadmap-template.html'
    if (-not (Test-Path -LiteralPath $template)) { throw "HTML template not found: $template" }
    $html = Get-Content -LiteralPath $template -Raw -Encoding utf8
    if ($html.IndexOf('__PAYLOAD__') -lt 0) { throw "The template does not carry the __PAYLOAD__ placeholder: $template" }
    $json = $Payload | ConvertTo-Json -Depth 32 -Compress
    # the payload sits in <script type="application/json">; only "</" can close it early.
    $json = $json.Replace('</', '<\/').Replace('<!--', '<\u0021--')
    $out = $html.Replace('__PAYLOAD__', $json)
    $m = [regex]::Match($out, '<script type="application/json" id="payload">([\s\S]*?)</script>')
    if (-not $m.Success) { throw 'The payload block is missing from the rendered page.' }
    try { [void]($m.Groups[1].Value | ConvertFrom-Json -Depth 64) }
    catch { throw "The inlined payload does not parse as JSON: $($_.Exception.Message)" }
    $tmp = "$OutPath.$PID.tmp"
    Set-Content -LiteralPath $tmp -Value $out -Encoding utf8 -NoNewline
    Move-Item -LiteralPath $tmp -Destination $OutPath -Force
}

# ---------------------------------------------------------------------- main

$OutDir = (New-Item -ItemType Directory -Path $OutDir -Force).FullName
$CacheDir = Join-Path $OutDir 'cache'
$SnapshotPath = if ($BoardSnapshot) { (Resolve-Path -LiteralPath $BoardSnapshot).Path } else { Join-Path $OutDir 'board-snapshot.json' }
$DataPath = Join-Path $OutDir 'roadmap-data.json'
$HtmlPath = Join-Path $OutDir 'roadmap.html'
$script:GhErrFile = Join-Path $OutDir 'gh.stderr.txt'

Write-Host "cohesion-roadmap  (read-only: no issue, project or git mutation is performed)" -ForegroundColor DarkGray
Write-Host "  repo $script:Repo   project #$script:ProjectNumber   out $OutDir"

$prevDoc = $null
if ($Previous) {
    $prevDoc = Read-JsonFile (Resolve-Path -LiteralPath $Previous).Path
    if ([string]$prevDoc.schema -ne 'cohesion.database.roadmap/2') { throw "-Previous must be a cohesion.database.roadmap/2 document; '$Previous' is '$($prevDoc.schema)'." }
}

$snapshot = $null
if ($Stage -in @('collect', 'all')) {
    Write-Phase "Collect (GitHub, read-only)"
    $snapshot = Invoke-Collect -CacheDir $CacheDir -SnapshotPath $SnapshotPath
}

if ($Stage -in @('build', 'all')) {
    Write-Phase "Build"
    if (-not $snapshot) {
        if (-not (Test-Path -LiteralPath $SnapshotPath)) { throw "No board snapshot at $SnapshotPath. Run with -Stage collect first." }
        $snapshot = Read-JsonFile $SnapshotPath
        Write-Step "snapshot $(Format-IsoDate $snapshot.generatedAt) ($($snapshot.counts.totalItems) items)"
    }
    $doc = Invoke-Build -Snapshot $snapshot -SnapshotPath $SnapshotPath -OutPath $DataPath -PreviousDoc $prevDoc
    Write-Step "data -> $DataPath"
    if ($Html) {
        $payload = New-RoadmapPayload -Doc $doc
        Write-JsonFileAtomic -Object $payload -Path (Join-Path $OutDir 'payload.json') -Depth 32
        Write-RoadmapHtml -Payload $payload -OutPath $HtmlPath
        Write-Step "page -> $HtmlPath"
    }
}

Write-Host "`nDone. $script:RequestCount GitHub requests this run." -ForegroundColor Green
