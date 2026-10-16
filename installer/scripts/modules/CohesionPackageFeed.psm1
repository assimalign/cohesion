#Requires -Version 7.2
<#
.SYNOPSIS
    Prunes CI builds from the Assimalign GitHub Packages NuGet feed.

.DESCRIPTION
    .github/workflows/ci-packages.yml publishes the full release set, about 400 package versions,
    for every push to main and every same-repository pull request commit. This module is the only
    thing that deletes them. .github/workflows/ci-packages-prune.yml calls
    Invoke-CohesionCiPackagePrune, which:

        1. takes the package ids the release inventory publishes (Get-CohesionReleasePackageId),
           because GITHUB_TOKEN cannot use the organization's list-packages endpoint. When that
           endpoint does answer (a token that may list), it adds packages named
           Assimalign.Cohesion.* AND linked to this repository, which covers ids since dropped from
           the inventory. Another repository's packages (cohesion-platforms publishes
           Assimalign.Cohesion.* names too) are never in either set;
        2. lists every version of each of them;
        3. asks the GitHub API which pull requests that have builds on the feed are closed;
        4. selects with Select-CohesionCiPackageVersionToPrune (CohesionPackaging.psm1), which only
           ever returns versions in the exact CI shapes, never a release; and
        5. deletes the selection, one request at a time, within the run's rate-limit budget.

    Every run converges: it recomputes the whole selection from the feed, so a run that stops on
    the budget, the time limit, or a cancelled queue entry leaves its remainder to the next run,
    with nothing to carry over.

    The REST calls go through Invoke-CohesionFeedRequest, which spends the token's primary budget
    only down to a reserve, honours retry-after on a secondary rate limit, and paces deletes
    (GitHub asks for at least a second between mutating requests). It needs PowerShell 7 for
    Invoke-WebRequest -SkipHttpErrorCheck; the offline inventory module stays on 5.1.
#>

Import-Module (Join-Path $PSScriptRoot 'CohesionPackaging.psm1')

# Thrown when the run must stop for budget or time rather than because something is wrong. The
# orchestrator catches it, reports the remainder as deferred, and exits successfully.
class CohesionFeedBudgetException : System.Exception {
    CohesionFeedBudgetException([string] $message) : base($message) {
    }
}

function Get-CohesionFeedHeader {
    param(
        [Parameter(Mandatory)]
        [object] $Response,

        [Parameter(Mandatory)]
        [string] $Name
    )

    if ($null -eq $Response.Headers) {
        return $null
    }

    foreach ($key in @($Response.Headers.Keys)) {
        if ([string]::Equals([string] $key, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            $value = $Response.Headers[$key]
            if ($value -is [string]) {
                return $value
            }

            return (@($value) | Select-Object -First 1)
        }
    }

    return $null
}

function Invoke-CohesionFeedRequest {
    <#
    .SYNOPSIS
        Sends one GitHub REST request within the run's budget and returns its status and body.

    .DESCRIPTION
        Before each request: stops (CohesionFeedBudgetException) when the run's time limit has
        passed or the primary rate limit is down to the reserve, so the rest of the repository's
        hourly GITHUB_TOKEN budget stays available.

        After each response: records x-ratelimit-remaining and x-ratelimit-reset. A primary
        rate-limit response stops the run. A secondary rate-limit response (429, a retry-after
        header, or the "secondary rate limit" message) waits as asked, or a minute doubling per
        attempt when GitHub names no wait, up to MaxRetryAfterSeconds and three attempts; past
        that the run stops rather than keep requesting while limited, which GitHub warns can get an
        integration banned. A 5xx or a transport failure is retried twice with a short backoff.
        Every other status is returned for the caller to judge.
    #>
    param(
        [Parameter(Mandatory)]
        [object] $Context,

        [Parameter(Mandatory)]
        [ValidateSet('GET', 'DELETE')]
        [string] $Method,

        [Parameter(Mandatory)]
        [string] $Path
    )

    $headers = @{
        Authorization          = "Bearer $($Context.Token)"
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent'           = 'assimalign-cohesion-ci-packages-prune'
    }

    for ($attempt = 1; ; $attempt++) {
        if ([System.DateTimeOffset]::UtcNow -gt $Context.Deadline) {
            throw [CohesionFeedBudgetException]::new("The run reached its $($Context.MaxMinutes)-minute limit.")
        }

        if ($null -ne $Context.Remaining -and $Context.Remaining -le $Context.Reserve) {
            throw [CohesionFeedBudgetException]::new(
                "The API rate limit is down to $($Context.Remaining) requests, the reserve kept for the rest of the repository; it resets at $($Context.ResetAt).")
        }

        $Context.RequestCount++
        try {
            $response = Invoke-WebRequest `
                -Uri "$($Context.ApiUrl)$Path" `
                -Method $Method `
                -Headers $headers `
                -SkipHttpErrorCheck `
                -TimeoutSec 60 `
                -ErrorAction Stop
        }
        catch {
            if ($attempt -ge 3) {
                throw
            }

            Write-Host "$Method $Path failed ($($_.Exception.Message)); retrying."
            Start-Sleep -Seconds (5 * $attempt)
            continue
        }

        $status = [int] $response.StatusCode
        $remaining = Get-CohesionFeedHeader -Response $response -Name 'x-ratelimit-remaining'
        $reset = Get-CohesionFeedHeader -Response $response -Name 'x-ratelimit-reset'
        $remainingValue = 0
        if ($null -ne $remaining -and [int]::TryParse([string] $remaining, [ref] $remainingValue)) {
            $Context.Remaining = $remainingValue
        }

        $resetValue = 0L
        if ($null -ne $reset -and [long]::TryParse([string] $reset, [ref] $resetValue)) {
            $Context.ResetAt = [System.DateTimeOffset]::FromUnixTimeSeconds($resetValue).ToString('u')
        }

        $content = if ($response.Content -is [byte[]]) {
            [System.Text.Encoding]::UTF8.GetString($response.Content)
        }
        else {
            [string] $response.Content
        }

        $body = $null
        if (-not [string]::IsNullOrWhiteSpace($content)) {
            try {
                $body = $content | ConvertFrom-Json -NoEnumerate -Depth 32
            }
            catch {
                $body = $null
            }
        }

        $message = if ($null -ne $body -and $body.PSObject.Properties['message']) {
            [string] $body.message
        }
        else {
            $content
        }

        if ($status -eq 403 -or $status -eq 429) {
            $retryAfter = Get-CohesionFeedHeader -Response $response -Name 'retry-after'
            if ($null -ne $remaining -and $remainingValue -eq 0) {
                throw [CohesionFeedBudgetException]::new(
                    "The primary API rate limit is exhausted; it resets at $($Context.ResetAt).")
            }

            if ($status -eq 429 -or $null -ne $retryAfter -or $message -match 'secondary rate limit') {
                $wait = 0
                if ($null -eq $retryAfter -or -not [int]::TryParse([string] $retryAfter, [ref] $wait)) {
                    $wait = 60 * [int] [System.Math]::Pow(2, $attempt - 1)
                }

                if ($attempt -ge 3 -or $wait -gt $Context.MaxRetryAfterSeconds) {
                    throw [CohesionFeedBudgetException]::new(
                        "GitHub's secondary rate limit asked for a $wait-second wait on attempt $attempt.")
                }

                Write-Host "Secondary rate limit on $Method $Path; waiting $wait seconds."
                Start-Sleep -Seconds $wait
                continue
            }
        }

        if ($status -ge 500 -and $attempt -lt 3) {
            Write-Host "$Method $Path returned HTTP $status; retrying."
            Start-Sleep -Seconds (5 * $attempt)
            continue
        }

        return [pscustomobject]@{
            StatusCode = $status
            Body       = $body
            Message    = $message
        }
    }
}

function Get-CohesionFeedLinkedPackageName {
    <#
    .SYNOPSIS
        Names of the organization's NuGet packages that are named Assimalign.Cohesion.* and linked
        to the repository, or nothing when the token may not list packages.

    .DESCRIPTION
        GITHUB_TOKEN cannot use the list-packages endpoint, so for the workflow this usually
        returns nothing after one request and the release inventory is the whole package set. A
        token that may list gets the linked packages the inventory no longer names.
    #>
    param(
        [Parameter(Mandatory)]
        [object] $Context,

        [Parameter(Mandatory)]
        [string] $Organization,

        [Parameter(Mandatory)]
        [string] $Repository
    )

    $name = [System.Collections.Generic.List[string]]::new()
    for ($page = 1; ; $page++) {
        $response = Invoke-CohesionFeedRequest `
            -Context $Context `
            -Method GET `
            -Path "/orgs/$Organization/packages?package_type=nuget&per_page=100&page=$page"
        if ($response.StatusCode -ne 200) {
            Write-Host "The organization package listing is unavailable to this token (HTTP $($response.StatusCode)); pruning the release inventory's packages."
            return
        }

        $item = @($response.Body)
        foreach ($entry in $item) {
            if (([string] $entry.name) -like 'Assimalign.Cohesion.*' -and
                $null -ne $entry.repository -and
                [string]::Equals([string] $entry.repository.full_name, $Repository, [System.StringComparison]::OrdinalIgnoreCase)) {
                $name.Add([string] $entry.name)
            }
        }

        if ($item.Count -lt 100) {
            break
        }
    }

    return , $name.ToArray()
}

function Get-CohesionFeedPackageVersion {
    param(
        [Parameter(Mandatory)]
        [object] $Context,

        [Parameter(Mandatory)]
        [string] $Organization,

        [Parameter(Mandatory)]
        [string] $PackageName
    )

    $escapedName = [System.Uri]::EscapeDataString($PackageName)
    $version = [System.Collections.Generic.List[object]]::new()
    for ($page = 1; ; $page++) {
        $response = Invoke-CohesionFeedRequest `
            -Context $Context `
            -Method GET `
            -Path "/orgs/$Organization/packages/nuget/$escapedName/versions?per_page=100&page=$page"
        if ($response.StatusCode -eq 404) {
            # Deleted since the package listing; nothing of it is left to prune.
            break
        }

        if ($response.StatusCode -ne 200) {
            throw "Listing the versions of $PackageName failed with HTTP $($response.StatusCode): $($response.Message)"
        }

        $item = @($response.Body)
        foreach ($entry in $item) {
            $version.Add([pscustomobject]@{
                    PackageName = $PackageName
                    VersionId   = $entry.id
                    Version     = [string] $entry.name
                    CreatedAt   = $entry.created_at
                })
        }

        if ($item.Count -lt 100) {
            break
        }
    }

    return , $version.ToArray()
}

function Get-CohesionPullRequestState {
    param(
        [Parameter(Mandatory)]
        [object] $Context,

        [Parameter(Mandatory)]
        [string] $Repository,

        [Parameter(Mandatory)]
        [int] $Number
    )

    $response = Invoke-CohesionFeedRequest -Context $Context -Method GET -Path "/repos/$Repository/pulls/$Number"
    if ($response.StatusCode -ne 200 -or $null -eq $response.Body) {
        return $null
    }

    return [string] $response.Body.state
}

function Write-CohesionCiPackagePruneSummary {
    param(
        [Parameter(Mandatory)]
        [object] $Summary,

        [Parameter(Mandatory)]
        [string] $Path
    )

    $closed = if ($Summary.ClosedPullRequests.Count -gt 0) {
        ($Summary.ClosedPullRequests | ForEach-Object { "#$_" }) -join ', '
    }
    else {
        'none'
    }

    $deferred = if ($Summary.Deferred -gt 0) {
        "$($Summary.Deferred) - $($Summary.DeferredReason) The next run continues."
    }
    else {
        '0'
    }

    $deleteRow = if ($Summary.DryRun) {
        "| Would delete | $($Summary.WouldDelete) |"
    }
    else {
        "| Deleted | $($Summary.Deleted) versions ($($Summary.DeletedPackages.Count) whole packages); $($Summary.AlreadyAbsent) were already gone |"
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('## CI package prune')
    $lines.Add('')
    $lines.Add('| | |')
    $lines.Add('| --- | --- |')
    $lines.Add("| Mode | $(if ($Summary.DryRun) { 'dry run, nothing deleted' } else { 'delete' }) |")
    $lines.Add("| Packages scanned | $($Summary.ScannedPackages) on the feed, of $($Summary.InventoryPackages) in the release inventory and $($Summary.LinkedOnlyPackages) more linked to ``$($Summary.Repository)`` |")
    $lines.Add("| Versions listed | $($Summary.ListedVersions) |")
    $lines.Add("| Closed pull requests with builds | $closed |")
    $lines.Add("| Main builds kept per base | $($Summary.MainBuildCount) |")
    $lines.Add("| Selected | $($Summary.Selected) |")
    $lines.Add($deleteRow)
    $lines.Add("| Deferred | $deferred |")
    $lines.Add("| Failed | $($Summary.Failed.Count) |")
    $lines.Add("| API requests | $($Summary.Requests) (rate limit remaining: $($Summary.RateLimitRemaining), resets $($Summary.RateLimitReset)) |")

    if ($Summary.Failed.Count -gt 0) {
        $lines.Add('')
        $lines.Add('### Failed deletions')
        $lines.Add('')
        foreach ($failure in @($Summary.Failed | Select-Object -First 20)) {
            $lines.Add("- $failure")
        }

        if ($Summary.Failed.Count -gt 20) {
            $lines.Add("- ...and $($Summary.Failed.Count - 20) more; see the job log.")
        }
    }

    $lines.Add('')
    # A dry run is Invoke-CohesionCiPackagePrune -WhatIf, which Out-File would honour too; the
    # summary is the dry run's whole output.
    ($lines -join "`n") | Out-File -FilePath $Path -Append -Encoding utf8 -WhatIf:$false
}

function Invoke-CohesionCiPackagePrune {
    <#
    .SYNOPSIS
        Deletes closed pull requests' builds and old main builds from the organization's NuGet feed.

    .DESCRIPTION
        See the module description for the sequence. Deletion order is the selection's: closed pull
        requests first, then main builds oldest first, SDK packages first within each build.

        A package whose every listed version is selected (one that only a closed pull request ever
        published, say) is deleted as a whole, after a fresh listing confirms that no version
        arrived in the meantime: one request instead of one per version, and no dependence on how
        GitHub treats deleting a package's last version. A deleted package is restorable for 30
        days and is recreated by its next publish.

        A run stops early, successfully, on the rate-limit reserve, a secondary rate limit it will
        not wait out, or MaxMinutes, and reports the rest as deferred. It fails after the run when
        any deletion was refused, and stops deleting after five consecutive permission refusals,
        because those mean the token lacks the package Admin role, which no later run fixes.

        -WhatIf lists and selects with real API reads and reports what it would delete.

    .PARAMETER Organization
        The package owner, assimalign.

    .PARAMETER Repository
        owner/name of the repository whose packages are pruned. Its pull requests decide which pr
        builds are closed, and the organization listing, when the token may use it, adds only
        packages linked to it.

    .PARAMETER PackageName
        The package ids the repository publishes: Get-CohesionReleasePackageId, the release
        inventory, which is exactly what ci-packages.yml pushes.

    .PARAMETER Token
        A token with the Admin role on the packages (the publishing repository's GITHUB_TOKEN with
        packages: write and pull-requests: read).

    .PARAMETER MainBuildCount
        Main builds kept per base; passed to Select-CohesionCiPackageVersionToPrune.

    .PARAMETER RateLimitReserve
        Primary rate-limit requests left unspent for the rest of the repository.

    .PARAMETER MaxMinutes
        Run time after which no new request starts.

    .PARAMETER DeleteIntervalMilliseconds
        Pause after each delete.

    .PARAMETER MaxRetryAfterSeconds
        Longest secondary rate-limit wait the run sits out; a longer one defers the rest.

    .PARAMETER SummaryPath
        File the Markdown run summary is appended to (the job's GITHUB_STEP_SUMMARY).
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z0-9-]+$')]
        [string] $Organization,

        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$')]
        [string] $Repository,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string[]] $PackageName,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string] $Token,

        [Parameter(Mandatory)]
        [ValidateRange(1, 1000)]
        [int] $MainBuildCount,

        [ValidateRange(0, 5000)]
        [int] $RateLimitReserve = 100,

        [ValidateRange(1, 360)]
        [int] $MaxMinutes = 45,

        [ValidateRange(0, 60000)]
        [int] $DeleteIntervalMilliseconds = 1000,

        [ValidateRange(0, 3600)]
        [int] $MaxRetryAfterSeconds = 120,

        [ValidateNotNullOrEmpty()]
        [string] $ApiUrl = 'https://api.github.com',

        [string] $SummaryPath
    )

    $context = [pscustomobject]@{
        Token        = $Token
        ApiUrl       = $ApiUrl.TrimEnd('/')
        Reserve      = $RateLimitReserve
        Remaining    = $null
        ResetAt      = 'unknown'
        Deadline     = [System.DateTimeOffset]::UtcNow.AddMinutes($MaxMinutes)
        MaxMinutes   = $MaxMinutes
        MaxRetryAfterSeconds = $MaxRetryAfterSeconds
        RequestCount = 0
    }

    $summary = [pscustomobject]@{
        Repository         = $Repository
        DryRun             = [bool] $WhatIfPreference
        MainBuildCount     = $MainBuildCount
        InventoryPackages  = 0
        LinkedOnlyPackages = 0
        ScannedPackages    = 0
        ListedVersions     = 0
        ClosedPullRequests = [System.Collections.Generic.List[int]]::new()
        Selected           = 0
        Deleted            = 0
        WouldDelete        = 0
        AlreadyAbsent      = 0
        DeletedPackages    = [System.Collections.Generic.List[string]]::new()
        Deferred           = 0
        DeferredReason     = $null
        Failed             = [System.Collections.Generic.List[string]]::new()
        Requests           = 0
        RateLimitRemaining = $null
        RateLimitReset     = $null
    }

    $selection = @()
    $handled = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $consecutivePermissionFailure = 0
    try {
        $package = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($name in $PackageName) {
            $null = $package.Add($name)
        }
        $summary.InventoryPackages = $package.Count

        $linkedName = Get-CohesionFeedLinkedPackageName -Context $context -Organization $Organization -Repository $Repository
        if ($null -ne $linkedName) {
            foreach ($name in $linkedName) {
                if ($package.Add($name)) {
                    $summary.LinkedOnlyPackages++
                }
            }
        }

        # Listing costs at least one request per package. Starting a listing the budget cannot
        # finish would spend it all and delete nothing.
        if ($null -ne $context.Remaining -and $context.Remaining -lt ($package.Count + $context.Reserve + 25)) {
            throw [CohesionFeedBudgetException]::new(
                "Listing $($package.Count) packages needs at least $($package.Count + 25) requests above the reserve of $($context.Reserve), and $($context.Remaining) remain; the limit resets at $($context.ResetAt).")
        }

        $listed = [System.Collections.Generic.List[object]]::new()
        foreach ($name in $package) {
            $version = Get-CohesionFeedPackageVersion -Context $context -Organization $Organization -PackageName $name
            if ($version.Count -gt 0) {
                $summary.ScannedPackages++
            }

            foreach ($entry in $version) {
                $listed.Add($entry)
            }
        }
        $summary.ListedVersions = $listed.Count

        if ($summary.ScannedPackages -eq 0) {
            throw "None of the $($package.Count) Cohesion packages is visible on the $Organization feed. Either the token cannot read them or the names are wrong; prune nothing until that is understood."
        }

        $pullRequestNumber = @(
            $listed |
                ForEach-Object { ConvertFrom-CohesionCiPackageVersion -Version $_.Version } |
                Where-Object { $_.Channel -eq 'pr' } |
                ForEach-Object { [int] $_.PullRequestNumber } |
                Sort-Object -Unique
        )
        foreach ($number in $pullRequestNumber) {
            # An unknown state (a 404, an error) keeps the builds; only "closed" deletes.
            if ((Get-CohesionPullRequestState -Context $context -Repository $Repository -Number $number) -eq 'closed') {
                $summary.ClosedPullRequests.Add($number)
            }
        }

        $selection = Select-CohesionCiPackageVersionToPrune `
            -PackageVersion $listed.ToArray() `
            -ClosedPullRequestNumber $summary.ClosedPullRequests.ToArray() `
            -MainBuildCount $MainBuildCount
        $summary.Selected = $selection.Count
        Write-Host "Selected $($selection.Count) of $($listed.Count) versions across $($package.Count) packages."

        $selectedByPackage = @{}
        foreach ($entry in $selection) {
            if (-not $selectedByPackage.ContainsKey($entry.PackageName)) {
                $selectedByPackage[$entry.PackageName] = [System.Collections.Generic.List[object]]::new()
            }

            $selectedByPackage[$entry.PackageName].Add($entry)
        }

        $listedCount = @{}
        foreach ($group in ($listed | Group-Object -Property PackageName)) {
            $listedCount[$group.Name] = $group.Count
        }

        $escapedPackage = @{}
        foreach ($name in @($selectedByPackage.Keys | Sort-Object)) {
            $escapedPackage[$name] = [System.Uri]::EscapeDataString($name)
            if ($selectedByPackage[$name].Count -ne $listedCount[$name]) {
                continue
            }

            # Every version of this package is selected. Delete it whole, but only if a fresh
            # listing still holds nothing but selected versions.
            $selectedId = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            foreach ($entry in $selectedByPackage[$name]) {
                $null = $selectedId.Add([string] $entry.VersionId)
            }

            $fresh = Get-CohesionFeedPackageVersion -Context $context -Organization $Organization -PackageName $name
            if ($fresh.Count -eq 0) {
                foreach ($entry in $selectedByPackage[$name]) {
                    $null = $handled.Add("$name/$($entry.VersionId)")
                }

                $summary.AlreadyAbsent += $selectedByPackage[$name].Count
                continue
            }

            if (@($fresh | Where-Object { -not $selectedId.Contains([string] $_.VersionId) }).Count -gt 0) {
                Write-Host "$name gained a version since it was listed; deleting its selected versions one by one."
                continue
            }

            if (-not $PSCmdlet.ShouldProcess("$name (all $($fresh.Count) versions are CI builds selected for pruning)", 'Delete package')) {
                foreach ($entry in $selectedByPackage[$name]) {
                    $null = $handled.Add("$name/$($entry.VersionId)")
                }

                $summary.WouldDelete += $selectedByPackage[$name].Count
                continue
            }

            $response = Invoke-CohesionFeedRequest `
                -Context $context `
                -Method DELETE `
                -Path "/orgs/$Organization/packages/nuget/$($escapedPackage[$name])"
            if ($response.StatusCode -eq 204 -or $response.StatusCode -eq 404) {
                foreach ($entry in $selectedByPackage[$name]) {
                    $null = $handled.Add("$name/$($entry.VersionId)")
                }

                if ($response.StatusCode -eq 204) {
                    $summary.Deleted += $selectedByPackage[$name].Count
                    $summary.DeletedPackages.Add($name)
                    Write-Host "Deleted package $name ($($fresh.Count) versions, all CI builds selected for pruning)."
                }
                else {
                    $summary.AlreadyAbsent += $selectedByPackage[$name].Count
                }
            }
            else {
                Write-Host "Deleting package $name returned HTTP $($response.StatusCode) ($($response.Message)); deleting its versions one by one."
            }

            Start-Sleep -Milliseconds $DeleteIntervalMilliseconds
        }

        foreach ($entry in $selection) {
            $key = "$($entry.PackageName)/$($entry.VersionId)"
            if ($handled.Contains($key)) {
                continue
            }

            # The selection only returns CI shapes; check again at the point of no return.
            if ($null -eq (ConvertFrom-CohesionCiPackageVersion -Version $entry.Version)) {
                throw "Refusing to delete $($entry.PackageName) $($entry.Version): it is not a CI build version."
            }

            if (-not $PSCmdlet.ShouldProcess("$($entry.PackageName) $($entry.Version) ($($entry.Reason))", 'Delete package version')) {
                $null = $handled.Add($key)
                $summary.WouldDelete++
                continue
            }

            $response = Invoke-CohesionFeedRequest `
                -Context $context `
                -Method DELETE `
                -Path "/orgs/$Organization/packages/nuget/$($escapedPackage[$entry.PackageName])/versions/$($entry.VersionId)"
            $null = $handled.Add($key)
            switch ($response.StatusCode) {
                204 {
                    $summary.Deleted++
                    $consecutivePermissionFailure = 0
                    Write-Host "Deleted $($entry.PackageName) $($entry.Version) ($($entry.Reason))."
                }
                404 {
                    $summary.AlreadyAbsent++
                    $consecutivePermissionFailure = 0
                }
                default {
                    $summary.Failed.Add("$($entry.PackageName) $($entry.Version): HTTP $($response.StatusCode) $($response.Message)")
                    if ($response.StatusCode -eq 401 -or $response.StatusCode -eq 403) {
                        $consecutivePermissionFailure++
                    }
                    else {
                        $consecutivePermissionFailure = 0
                    }
                }
            }

            if ($consecutivePermissionFailure -ge 5) {
                $summary.Failed.Add('Stopped after five consecutive permission refusals. The token needs the Admin role on each package: in the package settings, Manage Actions access, give the repository the Admin role (see docs/VERSIONING_RELEASE_POLICY.md, "Feed growth").')
                break
            }

            Start-Sleep -Milliseconds $DeleteIntervalMilliseconds
        }
    }
    catch [CohesionFeedBudgetException] {
        $summary.DeferredReason = $_.Exception.Message
        Write-Host "Stopping early: $($summary.DeferredReason)"
    }
    finally {
        $summary.Deferred = @($selection | Where-Object { -not $handled.Contains("$($_.PackageName)/$($_.VersionId)") }).Count
        if ($summary.Deferred -gt 0 -and $null -eq $summary.DeferredReason) {
            $summary.DeferredReason = 'Deleting stopped before the end of the selection.'
        }

        $summary.Requests = $context.RequestCount
        $summary.RateLimitRemaining = $context.Remaining
        $summary.RateLimitReset = $context.ResetAt
        if ($SummaryPath) {
            Write-CohesionCiPackagePruneSummary -Summary $summary -Path $SummaryPath
        }
    }

    if ($summary.Failed.Count -gt 0) {
        throw "$($summary.Failed.Count) CI package deletions failed:`n  " + (@($summary.Failed | Select-Object -First 20) -join "`n  ")
    }

    return $summary
}

Export-ModuleMember -Function @(
    'Invoke-CohesionCiPackagePrune'
)
