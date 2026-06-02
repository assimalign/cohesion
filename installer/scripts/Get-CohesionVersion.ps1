#Requires -Version 5.1
<#
.SYNOPSIS
    Resolves the canonical Cohesion package version from the build props files.

.DESCRIPTION
    Mirrors the derivation in build/Targets/Build.Version.props:

        CohesionMajorVersion = [System.Version]::Parse(TargetFramework.TrimStart('net')).Major
        CohesionMinorVersion = literal integer from Build.Version.props
        CohesionPatchVersion = literal integer from Build.Version.props
        CohesionVersion      = Major.Minor.Patch

    Bumping <TargetFramework> in build/Targets/Build.TargetFramework.props is the
    single-place edit for stepping across .NET releases — it drives the package
    major version automatically and this script picks it up.

    We don't dot-navigate $xml.Project.PropertyGroup.CohesionVersion because:
      1. <CohesionVersion>'s body is an MSBuild expression ($(CohesionMajorVersion)…)
         that PowerShell returns verbatim, so we'd get a literal "$(...)" string
         instead of a resolved version.
      2. Both props files have multiple <PropertyGroup> blocks; on PowerShell 7
         (the pwsh shell on GitHub-hosted runners) dot-navigation auto-iterates
         into a null-containing array and downstream method calls throw
         "You cannot call a method on a null-valued expression".

    Using Select-Xml with an XPath query sidesteps both problems.

.PARAMETER RepoRoot
    Repo root. Defaults to the parent of the parent of the script's folder
    (which puts installer/scripts/ two levels under the root).

.OUTPUTS
    System.String. The version (e.g. "10.0.0") emitted to stdout.

.EXAMPLE
    $version = & ./installer/scripts/Get-CohesionVersion.ps1
#>
[CmdletBinding()]
param(
    [Parameter()] [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

function Get-XmlPropertyValue {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Property
    )
    if (-not (Test-Path -LiteralPath $Path)) { throw "Props file not found: $Path" }
    $match = Select-Xml -LiteralPath $Path -XPath "/Project/PropertyGroup/$Property" | Select-Object -First 1
    if (-not $match) { throw "Could not find <$Property> in '$Path'." }
    return $match.Node.InnerText.Trim()
}

$tfmPropsPath     = Join-Path $RepoRoot 'build/Targets/Build.TargetFramework.props'
$versionPropsPath = Join-Path $RepoRoot 'build/Targets/Build.Version.props'

$targetFramework = Get-XmlPropertyValue -Path $tfmPropsPath     -Property 'TargetFrameworkLatest'
$cohesionMinor   = Get-XmlPropertyValue -Path $versionPropsPath -Property 'CohesionMinorVersion'
$cohesionPatch   = Get-XmlPropertyValue -Path $versionPropsPath -Property 'CohesionPatchVersion'

# Minor/Patch must be literal integers; if either ever becomes an MSBuild
# expression we'd silently produce a junk version, so fail loud instead.
foreach ($p in @(
    @{ Name = 'CohesionMinorVersion'; Value = $cohesionMinor }
    @{ Name = 'CohesionPatchVersion'; Value = $cohesionPatch.Split('-')[0] }
)) {
    if ($p.Value -notmatch '^\d+$') {
        throw "<$($p.Name)> in '$versionPropsPath' must be a literal integer, got '$($p.Value)'."
    }
}

$tfmTrimmed = $targetFramework -replace '^net',''
try {
    $tfmVersion = [Version]$tfmTrimmed
}
catch {
    throw "Could not parse TargetFramework '$targetFramework' as a version: $_"
}

# Writing only the version string to stdout. Callers capture via `& script.ps1`
# or `$(script.ps1)`. No host writes here — they'd pollute the captured value.
"{0}.{1}.{2}" -f $tfmVersion.Major, $cohesionMinor, $cohesionPatch
