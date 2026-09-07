#Requires -Version 5.1

function Get-CohesionLocalPackageVersion {
    <#
    .SYNOPSIS
        Derives the local-only package version from the canonical Cohesion version.

    .DESCRIPTION
        Appends a final .local prerelease identifier so a development pack cannot share an
        id/version pair with a published package. A stable canonical version is rejected because
        adding -local would sort below that stable version; main must receive its post-tag bump
        before local packing resumes.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Version
    )

    $versionPattern = '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<suffix>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
    $versionMatch = [System.Text.RegularExpressions.Regex]::Match(
        $Version,
        $versionPattern,
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $versionMatch.Success) {
        throw "Version '$Version' must be SemVer in the supported form MAJOR.MINOR.PATCH-PRERELEASE."
    }

    if (-not $versionMatch.Groups['suffix'].Success) {
        throw "Canonical version '$Version' is stable. Apply the required post-tag bump to a higher prerelease before creating local packages; a -local prerelease would sort below the stable release."
    }

    $canonicalSuffix = $versionMatch.Groups['suffix'].Value
    foreach ($identifier in $canonicalSuffix.Split('.')) {
        if ($identifier -match '^[0-9]+$' -and $identifier -notmatch '^(0|[1-9][0-9]*)$') {
            throw "Version '$Version' contains a numeric prerelease identifier with a leading zero."
        }
    }

    if ($canonicalSuffix.Split('.') -contains 'local') {
        throw "Canonical version '$Version' already contains the reserved local identifier."
    }

    $majorVersion = $versionMatch.Groups['major'].Value
    $minorVersion = $versionMatch.Groups['minor'].Value
    $patchVersion = $versionMatch.Groups['patch'].Value
    $versionPrefix = "$majorVersion.$minorVersion.$patchVersion"
    $versionSuffix = "$canonicalSuffix.local"

    return [pscustomobject]@{
        CanonicalVersion = $Version
        Version          = "$versionPrefix-$versionSuffix"
        VersionPrefix    = $versionPrefix
        VersionSuffix    = $versionSuffix
        MajorVersion     = $majorVersion
        MinorVersion     = $minorVersion
        PatchVersion     = "$patchVersion-$versionSuffix"
    }
}

function Get-CohesionStaleLibraryPackage {
    <#
    .SYNOPSIS
        Selects existing Cohesion library package artifacts from a local feed.

    .DESCRIPTION
        Returns only files whose complete name is one of the supplied package ids followed by a
        supported Cohesion SemVer and a NuGet package extension. Prefix-sharing package ids and
        unrelated files are excluded. The selection is deliberately non-recursive; the caller
        owns any deletion.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $PackageDirectory,

        [Parameter(Mandatory)]
        [string[]] $PackageId
    )

    $directory = Get-Item -LiteralPath $PackageDirectory -ErrorAction Stop
    if (-not $directory.PSIsContainer) {
        throw "Local package path '$PackageDirectory' is not a directory."
    }

    $versionFilePattern = '(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?'
    $packagePattern = @(
        foreach ($id in $PackageId) {
            if ([string]::IsNullOrWhiteSpace($id)) {
                throw 'Library package ids cannot be empty.'
            }

            $escapedId = [System.Text.RegularExpressions.Regex]::Escape($id)
            [System.Text.RegularExpressions.Regex]::new(
                "^$escapedId\.$versionFilePattern(?:(?:\.symbols)?\.nupkg|\.snupkg)$",
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
                    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        }
    )

    $stalePackage = [System.Collections.Generic.List[string]]::new()
    foreach ($file in @(Get-ChildItem -LiteralPath $directory.FullName -File | Sort-Object Name)) {
        foreach ($pattern in $packagePattern) {
            if (-not $pattern.IsMatch($file.Name)) {
                continue
            }

            $stalePackage.Add($file.FullName)
            break
        }
    }

    return $stalePackage.ToArray()
}

Export-ModuleMember -Function @(
    'Get-CohesionLocalPackageVersion'
    'Get-CohesionStaleLibraryPackage'
)
