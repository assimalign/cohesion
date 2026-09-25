<#
.SYNOPSIS
    Regenerates docs/DEPENDENCIES.md — the repository-wide reference graph.

.DESCRIPTION
    Walks every csproj under libraries/, resources/, analyzers/, tooling/, extensions/,
    sdks/, build/ and samples/, reads the Cohesion reference items
    (CohesionProjectReference, CohesionPrivateProjectReference, CohesionAnalyzerReference,
    CohesionAnalyzerAsProjectReference, CohesionPackageReference, CohesionSharedSource) plus raw
    ProjectReference and PackageReference, and writes a single generated document: per-area mermaid graphs, per-area
    reference tables, an area-to-area roll-up, and a fan-in ranking.

    This is a *static* read of the project files, deliberately: it does not restore, evaluate
    MSBuild, or build anything, so it runs in seconds on a clean tree and cannot be perturbed by
    package state. It therefore reports what the csprojs declare, which is the thing the
    dependency rules (COHRES001-004, COHAM001) are written against.

    Run it whenever a dependency is added or removed — see .claude/rules/documentation.md.

.PARAMETER RepositoryRoot
    Repository root. Defaults to two levels above this script (build/scripts/ -> repo root).

.PARAMETER OutputPath
    Destination document. Defaults to <RepositoryRoot>/docs/DEPENDENCIES.md.

.PARAMETER Check
    Do not write. Regenerate in memory and compare against the file on disk; exit 1 when they
    differ. This is the CI/pre-commit form — it fails when someone changed a reference without
    rerunning the generator.

.EXAMPLE
    pwsh build/scripts/Update-CohesionDependencyGraph.ps1

.EXAMPLE
    pwsh build/scripts/Update-CohesionDependencyGraph.ps1 -Check
#>
[CmdletBinding()]
param(
    [string] $RepositoryRoot,
    [string] $OutputPath,
    [switch] $Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $RepositoryRoot) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
}
$RepositoryRoot = (Resolve-Path $RepositoryRoot).Path
if (-not $OutputPath) {
    $OutputPath = Join-Path $RepositoryRoot 'docs/DEPENDENCIES.md'
}

# Roots scanned, in the order their sections appear in the document.
$scanRoots = @('libraries', 'resources', 'analyzers', 'sdks', 'tooling', 'extensions', 'build', 'samples')

# ---------------------------------------------------------------------------
# 1. Index every project.
# ---------------------------------------------------------------------------

function Get-ProjectKind {
    param([string] $RelativePath)
    $segments = $RelativePath -split '/'
    if ($segments -contains 'tests') { return 'tests' }
    if ($segments -contains 'samples') { return 'samples' }
    if ($segments -contains 'examples') { return 'examples' }
    if ($segments -contains 'src') { return 'src' }
    return 'other'
}

function Get-ProjectArea {
    param([string] $RelativePath)
    $segments = $RelativePath -split '/'
    if ($segments.Length -ge 2 -and $segments[0] -in @('libraries', 'resources')) {
        return "$($segments[0])/$($segments[1])"
    }
    return $segments[0]
}

# Keyed by RELATIVE PATH, and enumerated in a stable ordinal order.
#
# Both matter, and the reason is the same bug. Keying by file name silently dropped every
# project whose base name another project shares - and the survivor was whichever the
# filesystem happened to hand over last. Get-ChildItem -Recurse returns directory order, which
# is roughly alphabetical on NTFS and effectively arbitrary on ext4, so a name shared by a
# src/ project and a tests/ project resolved to a DIFFERENT project on a Linux runner than on
# a Windows workstation. That flipped those projects between the shipped graph and the harness
# list, changed the counts, and made -Check fail in CI while passing locally.
#
# The duplicate base names are reported below; the repository's name-only reference resolver
# (build/Targets/Build.References.Projects.targets) indexes by file name too, so they are
# ambiguous for it as well.
$projects = [ordered]@{}
$allFiles = New-Object System.Collections.Generic.List[object]
foreach ($root in $scanRoots) {
    $rootPath = Join-Path $RepositoryRoot $root
    if (-not (Test-Path -LiteralPath $rootPath)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $rootPath -Filter '*.csproj' -Recurse -File) {
        $relative = $file.FullName.Substring($RepositoryRoot.Length + 1).Replace('\', '/')
        if ($relative -match '(^|/)(bin|obj)/') { continue }
        $allFiles.Add([pscustomobject]@{ Relative = $relative; Root = $root })
    }
}

$byPath = @{}
$orderedPaths = New-Object System.Collections.Generic.List[string]
foreach ($file in $allFiles) {
    $byPath[$file.Relative] = $file
    $orderedPaths.Add($file.Relative)
}
# Ordinal, not culture-aware: the order has to be identical on every platform, and it is the
# order of a path list, not text for a human to read.
$orderedPaths.Sort([System.StringComparer]::Ordinal)

foreach ($relative in $orderedPaths) {
    $entry = $byPath[$relative]
    $projects[$relative] = [pscustomobject]@{
        Name         = [System.IO.Path]::GetFileNameWithoutExtension($relative)
        RelativePath = $relative
        Root         = $entry.Root
        Area         = Get-ProjectArea $relative
        Kind         = Get-ProjectKind $relative
        Producer     = $false
        Project      = @()
        Private      = @()
        Shared       = @()
        Analyzer     = @()
        Package      = @()
    }
}

# ---------------------------------------------------------------------------
# 2. Read each project's declared references.
# ---------------------------------------------------------------------------

function Get-IncludeValues {
    param([xml] $Xml, [string] $ItemName)
    $values = New-Object System.Collections.Generic.List[string]
    foreach ($node in $Xml.SelectNodes("//$ItemName[@Include]")) {
        $include = $node.GetAttribute('Include')
        if ([string]::IsNullOrWhiteSpace($include)) { continue }
        foreach ($piece in ($include -split ';')) {
            $trimmed = $piece.Trim()
            if ($trimmed -and $trimmed -notmatch '[@$]\(') { $values.Add($trimmed) }
        }
    }
    # Return the plain array and let every caller wrap the result in @(). Do NOT comma-wrap here:
    # the wrap survives @(), producing a one-element array holding a nested String[], which
    # stringifies as space-joined names and breaks every downstream name lookup.
    return $values.ToArray()
}

foreach ($project in $projects.Values) {
    $full = Join-Path $RepositoryRoot $project.RelativePath
    try { $xml = [xml](Get-Content -LiteralPath $full -Raw) }
    catch { Write-Warning "Skipping unparsable project: $($project.RelativePath) — $($_.Exception.Message)"; continue }

    $projectRefs  = @(Get-IncludeValues $xml 'CohesionProjectReference')
    $privateRefs  = @(Get-IncludeValues $xml 'CohesionPrivateProjectReference')
    $sharedRefs   = @(Get-IncludeValues $xml 'CohesionSharedSource')
    $analyzerRefs = @(Get-IncludeValues $xml 'CohesionAnalyzerReference') +
                    @(Get-IncludeValues $xml 'CohesionAnalyzerAsProjectReference')
    $packageRefs  = @(Get-IncludeValues $xml 'CohesionPackageReference') +
                    @(Get-IncludeValues $xml 'PackageReference')

    # Raw <ProjectReference Include="..\relative\path.csproj" /> resolves to its filename, which
    # is the same key the Cohesion name-only items use.
    $projectRefs += @(Get-IncludeValues $xml 'ProjectReference' |
        ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension(($_ -replace '\\', '/')) })

    # A shared-framework producer (Assimalign.Cohesion.<Owner>.Refs/.Runtime) is a packaging shell:
    # its membership is a framework item list, not a declared reference, so it is indexed for
    # fan-in but kept out of the product graph, as it was when producers lived under frameworks/.
    $project.Producer = $xml.SelectNodes('//CohesionFrameworkName').Count -gt 0

    $project.Project  = @($projectRefs  | Sort-Object -Unique)
    $project.Private  = @($privateRefs  | Sort-Object -Unique)
    $project.Shared   = @($sharedRefs   | Sort-Object -Unique)
    $project.Analyzer = @($analyzerRefs | Sort-Object -Unique)
    $project.Package  = @($packageRefs  | Sort-Object -Unique)
}

# ---------------------------------------------------------------------------
# 3. Derived views.
# ---------------------------------------------------------------------------

# Only shipped/buildable code participates in the graph; harnesses are listed separately.
$graphProjects = @($projects.Values | Where-Object { $_.Kind -in @('src', 'other') -and $_.Root -in @('libraries', 'resources') -and -not $_.Producer })

# Name -> project, for resolving a CohesionProjectReference's Include back to a project. Built
# over the ordinally-sorted index, preferring a src/ project over a harness when a base name is
# shared, so the choice is deterministic and matches what a reference by that name means.
$projectsByName = [ordered]@{}
$duplicateNames = [ordered]@{}
foreach ($project in $projects.Values) {
    $existing = if ($projectsByName.Contains($project.Name)) { $projectsByName[$project.Name] } else { $null }
    if ($null -eq $existing) {
        $projectsByName[$project.Name] = $project
        continue
    }
    if (-not $duplicateNames.Contains($project.Name)) { $duplicateNames[$project.Name] = @($existing.RelativePath) }
    $duplicateNames[$project.Name] = @($duplicateNames[$project.Name]) + $project.RelativePath
    if ($existing.Kind -ne 'src' -and $project.Kind -eq 'src') { $projectsByName[$project.Name] = $project }
}

function Get-AreaOf {
    param([string] $Name)
    if ($projectsByName.Contains($Name)) { return $projectsByName[$Name].Area }
    return $null
}

# Flatten every declared edge once, then group. Accumulating into hashtable-of-collections is the
# obvious shape and the wrong one in PowerShell: the stored value degrades to a fixed-size array.
$edges = foreach ($project in $projects.Values) {
    # Shared-source links are edges too: compiling another project's shared\ folder into this
    # assembly couples the two exactly as a reference does. Deduped per project so a name that is
    # both referenced and shared-source-linked (the normal case) is one edge, not two.
    # A project naming ITSELF is how it compiles its own shared\ folder (that folder sits outside
    # the csproj's src\ directory). It is not a dependency on anything, so it stays out of the
    # edge list and out of the graphs; the per-area table's Shared source column still records it.
    foreach ($target in @(@($project.Project) + @($project.Private) + @($project.Shared) | Sort-Object -Unique | Where-Object { $_ -ne $project.Name })) {
        [pscustomobject]@{
            From      = $project.Name
            FromArea  = $project.Area
            To        = $target
            ToArea    = Get-AreaOf $target
            InGraph   = ($project.Kind -in @('src', 'other') -and $project.Root -in @('libraries', 'resources') -and -not $project.Producer)
        }
    }
}
$edges = @($edges)

$fanIn = @{}
foreach ($group in ($edges | Group-Object To)) { $fanIn[$group.Name] = @($group.Group.From) }

# Area -> the other areas it references.
$areaEdges = @{}
foreach ($group in ($edges | Where-Object { $_.InGraph } | Group-Object FromArea)) {
    $areaEdges[$group.Name] = @(
        $group.Group |
            Where-Object { $_.ToArea -and $_.ToArea -ne $group.Name } |
            Select-Object -ExpandProperty ToArea -Unique |
            Sort-Object
    )
}

# ---------------------------------------------------------------------------
# 4. Emit.
# ---------------------------------------------------------------------------

$shortName = { param([string] $n) $n -replace '^Assimalign\.Cohesion\.', '' }
$sb = New-Object System.Text.StringBuilder
function Add-Line { param([string] $Text = '') [void]$sb.AppendLine($Text) }

$areaNames = @($graphProjects | Select-Object -ExpandProperty Area -Unique | Sort-Object)
$libraryAreas = @($areaNames | Where-Object { $_ -like 'libraries/*' })
$resourceAreas = @($areaNames | Where-Object { $_ -like 'resources/*' })

Add-Line '<!--'
Add-Line '    GENERATED FILE - DO NOT EDIT BY HAND.'
Add-Line '    Regenerate with: pwsh build/scripts/Update-CohesionDependencyGraph.ps1'
Add-Line '    Verify with:     pwsh build/scripts/Update-CohesionDependencyGraph.ps1 -Check'
Add-Line '-->'
Add-Line ''
Add-Line '# Dependency Graph'
Add-Line ''
Add-Line 'Every reference declared by every Cohesion project, read straight from the csprojs. This is'
Add-Line 'the 10,000-foot view: which assembly depends on which, which areas cross, and what the'
Add-Line 'most-depended-on assemblies are.'
Add-Line ''
Add-Line '**This file is generated.** Edit the csprojs, then rerun the generator:'
Add-Line ''
Add-Line '```bash'
Add-Line 'pwsh build/scripts/Update-CohesionDependencyGraph.ps1'
Add-Line '```'
Add-Line ''
Add-Line 'An arrow always means "references" / "depends on", the same direction the dependency rules are'
Add-Line 'written in: `Web.Hosting --> Web` reads "`Web.Hosting` references `Assimalign.Cohesion.Web`".'
Add-Line 'The `Assimalign.Cohesion.` prefix is stripped from node labels; tables carry the exact names.'
Add-Line ''
Add-Line 'A `CohesionSharedSource` link — compiling another project''s `shared/` folder into this'
Add-Line 'assembly — counts as an edge here, because it couples the two exactly as a reference does. It'
Add-Line 'also has its own column in the per-area tables. No edge is counted twice when a project both'
Add-Line 'references a project and links its shared source, and a project naming itself (how it compiles'
Add-Line 'its own `shared/` folder, which sits outside its `src/` directory) is shown in the table but is'
Add-Line 'not an edge.'
Add-Line ''
Add-Line '| | Count |'
Add-Line '| --- | --- |'
Add-Line "| Projects indexed | $($projects.Count) |"
Add-Line "| Shipped library/resource projects | $($graphProjects.Count) |"
Add-Line "| Library areas | $($libraryAreas.Count) |"
Add-Line "| Resource areas | $($resourceAreas.Count) |"
$edgeCount = ($graphProjects | ForEach-Object { @($_.Project).Count + @($_.Private).Count } | Measure-Object -Sum).Sum
Add-Line "| Declared project references | $edgeCount |"
$sharedCount = (@($projects.Values) | ForEach-Object { @($_.Shared).Count } | Measure-Object -Sum).Sum
Add-Line "| Declared shared-source links | $sharedCount |"
Add-Line ''

# --- Ambiguous project names ---------------------------------------------
if ($duplicateNames.Count -gt 0) {
    Add-Line '## Ambiguous project names'
    Add-Line ''
    Add-Line 'More than one project file shares each of these base names. That matters beyond this'
    Add-Line 'document: `CohesionProjectReference` resolves **by file name**'
    Add-Line '(`build/Targets/Build.References.Projects.targets`), so a reference to one of these names is'
    Add-Line 'ambiguous and the winner is whichever the resolver indexed last. Where a name is shared by a'
    Add-Line '`src/` project and a harness, this document resolves it to the `src/` one.'
    Add-Line ''
    Add-Line '| Name | Files |'
    Add-Line '| --- | --- |'
    foreach ($key in $duplicateNames.Keys) {
        $paths = (@($duplicateNames[$key]) | ForEach-Object { "``$_``" }) -join '<br>'
        Add-Line "| ``$key`` | $paths |"
    }
    Add-Line ''
}

# --- Area roll-up --------------------------------------------------------
Add-Line '## Area roll-up'
Add-Line ''
Add-Line 'Which areas reference which, collapsed to one node per area. Resource areas sit on library'
Add-Line 'areas; library areas sit on each other in L1 dependency order. An area never appears as its'
Add-Line 'own target — intra-area references are in the per-area sections below.'
Add-Line ''
# Drawing all 21 library areas would blow the twelve-node ceiling in
# .claude/rules/documentation.md and spend most of its arrows restating "everything references
# Core". Split instead: the Core-only and dependency-free areas are one sentence each, and the
# graph carries only the areas whose dependencies are actually worth a picture. Resource-area
# targets collapse to a single node - the complete per-area list is in the table below.
function Get-AreaTargets { param([string] $Area) if ($areaEdges.ContainsKey($Area)) { return @($areaEdges[$Area]) } return @() }

$coreOnlyAreas = @($libraryAreas | Where-Object { $t = @(Get-AreaTargets $_); $t.Count -eq 1 -and $t[0] -eq 'libraries/Core' })
$leafAreas     = @($libraryAreas | Where-Object { @(Get-AreaTargets $_).Count -eq 0 })
$layeredAreas  = @($libraryAreas | Where-Object { $_ -notin $coreOnlyAreas -and $_ -notin $leafAreas })

$tick = [char]0x60
if ($leafAreas.Count) {
    $list = (($leafAreas | ForEach-Object { "$tick$_$tick" }) -join ', ')
    Add-Line "**Depends on no other area:** $list."
    Add-Line ''
}
if ($coreOnlyAreas.Count) {
    $list = (($coreOnlyAreas | ForEach-Object { "$tick$_$tick" }) -join ', ')
    Add-Line "**Depends on ${tick}libraries/Core$tick and nothing else:** $list."
    Add-Line ''
}
Add-Line 'Every remaining library area, with the resource areas collapsed into one node:'
Add-Line ''
Add-Line '```mermaid'
Add-Line 'flowchart LR'
$libIds = @{}
$i = 0
$graphAreas = @($layeredAreas + @($layeredAreas | ForEach-Object { @(Get-AreaTargets $_) } | Where-Object { $_ -like 'libraries/*' }) | Sort-Object -Unique)
foreach ($area in $graphAreas) { $libIds[$area] = "L$i"; $i++ }
foreach ($area in $graphAreas) {
    $label = $area -replace '^libraries/', ''
    Add-Line "    $($libIds[$area])[`"$label`"]"
}
$needsResourceNode = @($layeredAreas | Where-Object { @(Get-AreaTargets $_) -like 'resources/*' }).Count -gt 0
if ($needsResourceNode) { Add-Line '    RES["resources/* — client packages"]' }
foreach ($area in $layeredAreas) {
    $drewResourceEdge = $false
    foreach ($target in @(Get-AreaTargets $area)) {
        if ($libIds.ContainsKey($target)) { Add-Line "    $($libIds[$area]) --> $($libIds[$target])" }
        elseif ($target -like 'resources/*' -and -not $drewResourceEdge) {
            Add-Line "    $($libIds[$area]) --> RES"
            $drewResourceEdge = $true
        }
    }
}
Add-Line '```'
Add-Line ''
if ($needsResourceNode) {
    Add-Line 'The edge into `resources/*` is the gateway consuming resource **client** packages to'
    Add-Line 'orchestrate them; it is not an L2-on-L3 layering inversion. `COHRES003` enforces the'
    Add-Line 'direction that matters — no shipped project under `resources/**` may reference an'
    Add-Line '`ApplicationModel.Gateway*` assembly.'
    Add-Line ''
}
Add-Line '| Area | References |'
Add-Line '| --- | --- |'
foreach ($area in $areaNames) {
    $targets = if ($areaEdges.ContainsKey($area) -and $areaEdges[$area].Count -gt 0) {
        (($areaEdges[$area] | Sort-Object) -join ', ')
    } else { '_(none)_' }
    Add-Line "| ``$area`` | $targets |"
}
Add-Line ''

# --- Per-area detail ------------------------------------------------------
Add-Line '## Areas'
Add-Line ''
foreach ($area in $areaNames) {
    $members = @($graphProjects | Where-Object { $_.Area -eq $area } | Sort-Object Name)
    Add-Line "### ``$area``"
    Add-Line ''
    Add-Line "$($members.Count) shipped project$(if ($members.Count -ne 1) { 's' })."
    Add-Line ''

    # The twelve-node ceiling in .claude/rules/documentation.md applies to the drawn graph; past
    # it the table alone carries the area and the roll-up above carries the direction.
    $intra = @()
    foreach ($m in $members) {
        foreach ($t in @(@($m.Project) + @($m.Private) + @($m.Shared) | Sort-Object -Unique | Where-Object { $_ -ne $m.Name })) {
            if ((Get-AreaOf $t) -eq $area) { $intra += ,@($m.Name, $t) }
        }
    }
    if ($members.Count -le 12 -and $intra.Count -gt 0) {
        Add-Line 'Intra-area references:'
        Add-Line ''
        Add-Line '```mermaid'
        Add-Line 'flowchart LR'
        $ids = @{}
        $n = 0
        foreach ($m in $members) { $ids[$m.Name] = "N$n"; $n++ }
        foreach ($m in $members) { Add-Line "    $($ids[$m.Name])[`"$(& $shortName $m.Name)`"]" }
        foreach ($edge in $intra) {
            if ($ids.ContainsKey($edge[0]) -and $ids.ContainsKey($edge[1])) {
                Add-Line "    $($ids[$edge[0]]) --> $($ids[$edge[1]])"
            }
        }
        Add-Line '```'
        Add-Line ''
    }
    elseif ($members.Count -gt 12) {
        Add-Line "_More than twelve projects: the table below is the area's graph (see the node ceiling in ``.claude/rules/documentation.md``)._"
        Add-Line ''
    }

    Add-Line '| Project | References | Private references | Shared source | Packages |'
    Add-Line '| --- | --- | --- | --- | --- |'
    foreach ($m in $members) {
        $refs = if (@($m.Project).Count) { (@($m.Project) | ForEach-Object { "``$_``" }) -join '<br>' } else { '—' }
        $priv = if (@($m.Private).Count) { (@($m.Private) | ForEach-Object { "``$_``" }) -join '<br>' } else { '—' }
        $shrd = if (@($m.Shared).Count)  { (@($m.Shared)  | ForEach-Object { "``$_``" }) -join '<br>' } else { '—' }
        $pkgs = if (@($m.Package).Count) { (@($m.Package) | ForEach-Object { "``$_``" }) -join '<br>' } else { '—' }
        Add-Line "| ``$($m.Name)`` | $refs | $priv | $shrd | $pkgs |"
    }
    Add-Line ''
}

# --- Fan-in ---------------------------------------------------------------
Add-Line '## Most-referenced assemblies'
Add-Line ''
Add-Line 'Fan-in across every indexed project, harnesses included. A high count is a change-blast-radius'
Add-Line 'warning, not a problem in itself: these are the assemblies whose contracts cost the most to move.'
Add-Line ''
Add-Line '| Assembly | Referenced by | Top referrers |'
Add-Line '| --- | --- | --- |'
# Count descending, then name ascending. The second key is not cosmetic: Sort-Object is not a
# stable sort, so ties reorder between runs and -Check fails at random in CI without it.
$fanInRanked = $fanIn.GetEnumerator() |
    Sort-Object -Property @{ Expression = { $_.Value.Count }; Descending = $true },
                          @{ Expression = { $_.Key }; Descending = $false }
foreach ($entry in ($fanInRanked | Select-Object -First 25)) {
    $top = (@($entry.Value | Sort-Object -Unique | Select-Object -First 4) -join ', ')
    if (@($entry.Value | Sort-Object -Unique).Count -gt 4) { $top += ', …' }
    Add-Line "| ``$($entry.Key)`` | $($entry.Value.Count) | $top |"
}
Add-Line ''

# --- Harnesses ------------------------------------------------------------
$harnesses = @($projects.Values | Where-Object { $_.Kind -in @('tests', 'samples', 'examples') } | Sort-Object Name)
Add-Line '## Harnesses'
Add-Line ''
Add-Line "$($harnesses.Count) test, sample, and example projects are indexed for fan-in but excluded from the"
Add-Line 'area graphs above: they consume the shipped assemblies rather than forming part of the product'
Add-Line 'graph, and the dependency guards exempt them by path.'
Add-Line ''
$producers = @($projects.Values | Where-Object { $_.Producer })
if ($producers.Count) {
    Add-Line "$($producers.Count) shared-framework producer projects (``Assimalign.Cohesion.<Owner>.Refs`` / ``.Runtime``) are"
    Add-Line 'likewise indexed but kept out of the area graphs: they are packaging shells whose framework'
    Add-Line 'membership is an item list, not a project reference: App''s kernel roots in'
    Add-Line '`libraries/App/Assimalign.Cohesion.App.props`, and each area''s members in'
    Add-Line '`resources/<Area>/Assimalign.Cohesion.<Area>.Runtime/Directory.Build.props`.'
    Add-Line ''
}
Add-Line '| Kind | Count |'
Add-Line '| --- | --- |'
foreach ($group in ($harnesses | Group-Object Kind | Sort-Object Name)) {
    Add-Line "| ``$($group.Name)/`` | $($group.Count) |"
}
Add-Line ''
$samples = @($harnesses | Where-Object { $_.Kind -eq 'samples' })
if ($samples.Count) {
    Add-Line 'Samples, which live in the repository-root `samples/` tree:'
    Add-Line ''
    Add-Line '| Sample | Path | References |'
    Add-Line '| --- | --- | --- |'
    foreach ($s in $samples) {
        $refs = if (@($s.Project).Count) { (@($s.Project) | ForEach-Object { "``$_``" }) -join '<br>' } else { '_(SDK-delivered)_' }
        Add-Line "| ``$($s.Name)`` | ``$($s.RelativePath)`` | $refs |"
    }
    Add-Line ''
}

$content = $sb.ToString() -replace "`r`n", "`n"

if ($Check) {
    if (-not (Test-Path -LiteralPath $OutputPath)) {
        Write-Error "Missing $OutputPath. Run the generator without -Check."
        exit 1
    }
    $existing = (Get-Content -LiteralPath $OutputPath -Raw) -replace "`r`n", "`n"
    if ($existing -ne $content) {
        Write-Error "$OutputPath is stale. A dependency changed without rerunning build/scripts/Update-CohesionDependencyGraph.ps1."
        exit 1
    }
    Write-Host "OK - $OutputPath matches the project files."
    exit 0
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory)) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
[System.IO.File]::WriteAllText($OutputPath, $content, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Wrote $OutputPath — $($projects.Count) projects, $($areaNames.Count) areas."
