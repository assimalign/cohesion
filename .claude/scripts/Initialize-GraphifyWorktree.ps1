<#
.SYNOPSIS
    Wires a linked git worktree to the main checkout's graphify knowledge graph.

.DESCRIPTION
    graphify-out/ (graph.json + cache) is a machine-local build artifact that lives
    only in the main checkout (kept fresh by the graphify post-commit hook, where
    installed). Linked worktrees get a directory junction pointing at it so
    `graphify query|path|explain` work from inside the worktree without a rebuild.

    Idempotent: safe to run repeatedly. No-ops in the main checkout or when the
    junction already exists. Refuses to replace a worktree-local graphify-out that
    contains anything beyond the tracked GRAPH_REPORT.md.
#>
$ErrorActionPreference = 'Stop'

$commonDir = (git rev-parse --path-format=absolute --git-common-dir).Trim()
if (-not $commonDir) { Write-Error 'Not inside a git repository.'; exit 1 }
$mainRoot = (Resolve-Path (Join-Path $commonDir '..')).Path.TrimEnd('\', '/')
$hereRoot = (git rev-parse --show-toplevel).Trim().Replace('/', [IO.Path]::DirectorySeparatorChar).TrimEnd('\', '/')

if ($mainRoot -eq $hereRoot) {
    Write-Host 'Main checkout - graphify-out is local here; nothing to wire.'
    exit 0
}

$target = Join-Path $mainRoot 'graphify-out'
$link   = Join-Path $hereRoot 'graphify-out'

if (-not (Test-Path (Join-Path $target 'graph.json'))) {
    Write-Host "No graph at $target - build it in the main checkout first (see .claude/CLAUDE.md, graphify section)."
    exit 1
}

$existing = Get-Item $link -ErrorAction SilentlyContinue
if ($existing -and $existing.LinkType) {
    Write-Host "Junction already wired: $link -> $($existing.Target)"
    exit 0
}
if ($existing) {
    # A real directory checked out here only ever holds the tracked GRAPH_REPORT.md.
    $unexpected = Get-ChildItem $link -Recurse -File | Where-Object Name -ne 'GRAPH_REPORT.md'
    if ($unexpected) {
        Write-Host "Refusing to replace $link - it contains unexpected files:"
        $unexpected | ForEach-Object { Write-Host "  $($_.FullName)" }
        exit 1
    }
    Remove-Item $link -Recurse -Force
}

New-Item -ItemType Junction -Path $link -Target $target | Out-Null

# The junction now surfaces the main checkout's GRAPH_REPORT.md, which usually
# differs from this worktree's HEAD version. skip-worktree stops git from
# reporting it as modified or sweeping it into commits made from this worktree.
# (Fails harmlessly if the file is not tracked yet.)
git update-index --skip-worktree graphify-out/GRAPH_REPORT.md 2>$null
if ($LASTEXITCODE -ne 0) { $global:LASTEXITCODE = 0 }

Write-Host "graphify-out junction wired: $link -> $target"
