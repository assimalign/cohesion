#Requires -Version 5.1
<#
.SYNOPSIS
    Emits the GitHub Actions job matrix that validates a Cohesion release.

.DESCRIPTION
    .github/workflows/release.yml builds its validation matrix from this script rather than from a
    literal YAML table. The release validates exactly the set it ships, because both come from
    installer/scripts/modules/CohesionPackaging.psm1 - a 131-row YAML table maintained beside a
    131-entry PowerShell list is a drift bomb, and the drift would be silent.

    The emitted shape feeds `strategy.matrix: ${{ fromJSON(...) }}`:

        {"include":[{"area":"libraries","category":"Core","project":"Assimalign.Cohesion.Core"},...]}

    area/category/project are exactly the inputs .github/actions/build takes.

    The inventory drift guard runs first, so a release that ships something CI never validated (or
    that CI validates but the release would silently omit) fails here - before anything is packed,
    let alone published.

.PARAMETER RepositoryDirectory
    Repo root. Defaults to the parent of the parent of this script's folder.

.OUTPUTS
    System.String. One line of compact JSON on stdout.

.EXAMPLE
    ./installer/scripts/Get-ReleaseMatrix.ps1
#>
[CmdletBinding()]
param(
    [string] $RepositoryDirectory
)

$ErrorActionPreference = 'Stop'

if (-not $RepositoryDirectory) {
    $RepositoryDirectory = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

Import-Module (Join-Path $PSScriptRoot 'modules/CohesionPackaging.psm1') -Force

# Writes its own progress to the host, which is stderr-adjacent in Actions and does not pollute
# the JSON captured from stdout.
Assert-CohesionReleaseInventory -RepositoryDirectory $RepositoryDirectory

$include = @(
    Get-CohesionReleaseLibrary -RepositoryDirectory $RepositoryDirectory |
        ForEach-Object {
            [pscustomobject]@{
                area     = $_.Area
                category = $_.Category
                project  = $_.Project
            }
        }
)

if ($include.Count -eq 0) {
    throw 'The release inventory produced an empty validation matrix.'
}

# ConvertTo-Json unwraps a one-element array; the inventory is far larger than that today, but the
# explicit wrapper keeps the contract true regardless of how small the set gets.
$json = [pscustomobject]@{ include = $include } | ConvertTo-Json -Depth 4 -Compress
if ($json -notmatch '^\{"include":\[') {
    throw "The emitted matrix is not in the expected {`"include`":[...]} shape: $json"
}

$json
