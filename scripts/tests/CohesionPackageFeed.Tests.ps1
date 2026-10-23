#Requires -Version 7.2
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../modules/CohesionPackageFeed.psm1') -Force

    # An in-memory GitHub Packages feed behind a mocked Invoke-WebRequest. Each package is
    # @{ name; repository; versions }, and each version @{ id; name; created_at }.
    function New-FakeFeed {
        $feed = [pscustomobject]@{
            Packages      = [System.Collections.Generic.List[object]]::new()
            PullRequests  = @{}
            Deleted       = [System.Collections.Generic.List[string]]::new()
            DeleteStatus  = @{}
            Remaining     = 4000
            # GITHUB_TOKEN cannot use the organization listing; a test that has a token that may
            # list sets this to 200.
            ListStatus    = 403
            SecondaryOnce = $false
            OnRelist      = $null
            ListCount     = @{}
            NextId        = 100
        }
        return $feed
    }

    function Add-FakePackage {
        param(
            [Parameter(Mandatory)]
            [object] $Feed,

            [Parameter(Mandatory)]
            [string] $Name,

            [AllowNull()]
            [string] $Repository = 'assimalign/cohesion',

            [Parameter(Mandatory)]
            [string[]] $Version
        )

        $versions = [System.Collections.Generic.List[object]]::new()
        $hour = 0
        foreach ($entry in $Version) {
            $Feed.NextId++
            $hour++
            $versions.Add([pscustomobject]@{
                    id         = $Feed.NextId
                    name       = $entry
                    created_at = ([System.DateTimeOffset]::new(2026, 10, 1, 0, 0, 0, [System.TimeSpan]::Zero)).AddHours($hour).ToString('yyyy-MM-ddTHH:mm:ssZ')
                })
        }

        $Feed.Packages.Add([pscustomobject]@{
                name       = $Name
                repository = if ($Repository) { [pscustomobject]@{ full_name = $Repository } } else { $null }
                versions   = $versions
            })
    }

    function New-FakeResponse {
        param(
            [Parameter(Mandatory)]
            [object] $Feed,

            [int] $StatusCode = 200,

            [AllowNull()]
            [object] $Body,

            [hashtable] $Header = @{}
        )

        $Feed.Remaining--
        $headers = @{
            'x-ratelimit-remaining' = @([string] $Feed.Remaining)
            'x-ratelimit-reset'     = @('1790000000')
        }
        foreach ($key in $Header.Keys) {
            $headers[$key] = @($Header[$key])
        }

        $content = if ($null -eq $Body) { '' } else { ConvertTo-Json -InputObject $Body -Depth 8 }
        return [pscustomobject]@{
            StatusCode = $StatusCode
            Headers    = $headers
            Content    = $content
        }
    }

    function Invoke-FakeFeedRequest {
        param(
            [Parameter(Mandatory)]
            [object] $Feed,

            [Parameter(Mandatory)]
            [string] $Uri,

            [Parameter(Mandatory)]
            [string] $Method
        )

        $path = ([System.Uri] $Uri).PathAndQuery
        if ($Method -eq 'GET' -and $path -match '^/orgs/assimalign/packages\?package_type=nuget&per_page=100&page=(?<page>\d+)$') {
            if ($Feed.ListStatus -ne 200) {
                return New-FakeResponse -Feed $Feed -StatusCode $Feed.ListStatus -Body @{ message = 'Resource not accessible by integration' }
            }

            $page = [int] $Matches.page
            $slice = @($Feed.Packages | Select-Object -Skip (($page - 1) * 100) -First 100 |
                    ForEach-Object { [pscustomobject]@{ name = $_.name; repository = $_.repository } })
            return New-FakeResponse -Feed $Feed -Body $slice
        }

        if ($Method -eq 'GET' -and $path -match '^/orgs/assimalign/packages/nuget/(?<name>[^/]+)/versions\?per_page=100&page=(?<page>\d+)$') {
            $name = [System.Uri]::UnescapeDataString($Matches.name)
            $page = [int] $Matches.page
            $package = $Feed.Packages | Where-Object name -EQ $name
            if (-not $package) {
                return New-FakeResponse -Feed $Feed -StatusCode 404 -Body @{ message = 'Package not found.' }
            }

            $Feed.ListCount[$name] = 1 + [int] $Feed.ListCount[$name]
            if ($Feed.ListCount[$name] -eq 2 -and $Feed.OnRelist) {
                & $Feed.OnRelist $package
            }

            $slice = @($package.versions | Select-Object -Skip (($page - 1) * 100) -First 100)
            return New-FakeResponse -Feed $Feed -Body $slice
        }

        if ($Method -eq 'GET' -and $path -match '^/repos/assimalign/cohesion/pulls/(?<number>\d+)$') {
            $state = $Feed.PullRequests[[int] $Matches.number]
            if (-not $state) {
                return New-FakeResponse -Feed $Feed -StatusCode 404 -Body @{ message = 'Not Found' }
            }

            return New-FakeResponse -Feed $Feed -Body @{ number = [int] $Matches.number; state = $state }
        }

        if ($Method -eq 'DELETE' -and $path -match '^/orgs/assimalign/packages/nuget/(?<name>[^/]+)/versions/(?<id>\d+)$') {
            $name = [System.Uri]::UnescapeDataString($Matches.name)
            $id = [int] $Matches.id
            if ($Feed.SecondaryOnce) {
                $Feed.SecondaryOnce = $false
                return New-FakeResponse -Feed $Feed -StatusCode 403 -Body @{ message = 'You have exceeded a secondary rate limit.' } -Header @{ 'retry-after' = '30' }
            }

            if ($Feed.DeleteStatus.ContainsKey($id)) {
                return New-FakeResponse -Feed $Feed -StatusCode $Feed.DeleteStatus[$id] -Body @{ message = 'Refused.' }
            }

            $package = $Feed.Packages | Where-Object name -EQ $name
            $version = $package.versions | Where-Object id -EQ $id
            if (-not $version) {
                return New-FakeResponse -Feed $Feed -StatusCode 404 -Body @{ message = 'Not Found' }
            }

            $null = $package.versions.Remove($version)
            $Feed.Deleted.Add("$name $($version.name)")
            return New-FakeResponse -Feed $Feed -StatusCode 204
        }

        if ($Method -eq 'DELETE' -and $path -match '^/orgs/assimalign/packages/nuget/(?<name>[^/]+)$') {
            $name = [System.Uri]::UnescapeDataString($Matches.name)
            $package = $Feed.Packages | Where-Object name -EQ $name
            $null = $Feed.Packages.Remove($package)
            $Feed.Deleted.Add("$name (package)")
            return New-FakeResponse -Feed $Feed -StatusCode 204
        }

        throw "The fake feed has no route for $Method $path."
    }

    function Invoke-TestPrune {
        param(
            [string[]] $PackageName = @('Assimalign.Cohesion.Core', 'Assimalign.Cohesion.Sdk'),

            [switch] $WhatIf,

            [int] $RateLimitReserve = 100,

            [string] $SummaryPath
        )

        $arguments = @{
            Organization               = 'assimalign'
            Repository                 = 'assimalign/cohesion'
            PackageName                = $PackageName
            Token                      = 'test-token'
            MainBuildCount             = 2
            RateLimitReserve           = $RateLimitReserve
            DeleteIntervalMilliseconds = 0
            WhatIf                     = $WhatIf
        }
        if ($SummaryPath) {
            $arguments.SummaryPath = $SummaryPath
        }

        return Invoke-CohesionCiPackagePrune @arguments
    }
}

AfterAll {
    Remove-Module CohesionPackageFeed -Force -ErrorAction SilentlyContinue
}

Describe 'Cohesion CI package prune against the feed' {
    BeforeEach {
        $script:feed = New-FakeFeed
        Mock -ModuleName CohesionPackageFeed Start-Sleep {}
        Mock -ModuleName CohesionPackageFeed Invoke-WebRequest {
            Invoke-FakeFeedRequest -Feed $script:feed -Uri $Uri -Method $Method
        }

        foreach ($name in @('Assimalign.Cohesion.Core', 'Assimalign.Cohesion.Sdk')) {
            Add-FakePackage -Feed $script:feed -Name $name -Version @(
                '10.0.0-beta.1'
                '10.0.0-preview.0.ci.1'
                '10.0.0-preview.0.ci.2'
                '10.0.0-preview.0.pr.15.3'
                '10.0.0-preview.0.pr.152.4'
                '10.0.0-preview.0.ci.5'
                '10.0.0-preview.0.pr.20.6'
                '10.0.0-preview.0.ci.7'
                '10.0.0-preview.1'
            )
        }

        # Same names, other owners: cohesion-platforms' gateway packages, an unlinked package, and
        # a linked package outside the Assimalign.Cohesion namespace. None may be touched.
        Add-FakePackage -Feed $script:feed -Name 'Assimalign.Cohesion.ApplicationModel.Gateway.Docker' -Repository 'assimalign/cohesion-platforms' -Version @(
            '10.0.0-preview.0.ci.1'
            '10.0.0-preview.0.pr.15.2'
        )
        Add-FakePackage -Feed $script:feed -Name 'Assimalign.Cohesion.Unlinked' -Repository $null -Version @('10.0.0-preview.0.pr.15.1')
        Add-FakePackage -Feed $script:feed -Name 'Other.Package' -Version @('10.0.0-preview.0.pr.15.1')

        $script:feed.PullRequests[15] = 'closed'
        $script:feed.PullRequests[152] = 'open'
        # Pull request 20 is unknown to the API (404), so its builds stay.
    }

    It 'deletes closed pull request builds and expired main builds of the named packages only' {
        $summaryPath = Join-Path $TestDrive 'summary.md'

        $summary = Invoke-TestPrune -SummaryPath $summaryPath

        @($script:feed.Deleted) | Should -Be @(
            'Assimalign.Cohesion.Sdk 10.0.0-preview.0.pr.15.3'
            'Assimalign.Cohesion.Core 10.0.0-preview.0.pr.15.3'
            'Assimalign.Cohesion.Sdk 10.0.0-preview.0.ci.1'
            'Assimalign.Cohesion.Core 10.0.0-preview.0.ci.1'
            'Assimalign.Cohesion.Sdk 10.0.0-preview.0.ci.2'
            'Assimalign.Cohesion.Core 10.0.0-preview.0.ci.2'
        )
        foreach ($name in @('Assimalign.Cohesion.Core', 'Assimalign.Cohesion.Sdk')) {
            @(($script:feed.Packages | Where-Object name -EQ $name).versions.name) | Should -Be @(
                '10.0.0-beta.1'
                '10.0.0-preview.0.pr.152.4'
                '10.0.0-preview.0.ci.5'
                '10.0.0-preview.0.pr.20.6'
                '10.0.0-preview.0.ci.7'
                '10.0.0-preview.1'
            )
        }

        $summary.InventoryPackages | Should -Be 2
        $summary.LinkedOnlyPackages | Should -Be 0
        $summary.ScannedPackages | Should -Be 2
        $summary.Selected | Should -Be 6
        $summary.Deleted | Should -Be 6
        $summary.Deferred | Should -Be 0
        @($summary.ClosedPullRequests) | Should -Be @(15)
        Get-Content -LiteralPath $summaryPath -Raw | Should -Match '(?m)^## CI package prune'
        Should -Invoke -ModuleName CohesionPackageFeed Invoke-WebRequest -ParameterFilter {
            $Uri -match 'Gateway\.Docker|Unlinked|Other\.Package'
        } -Times 0
    }

    It 'adds linked packages the inventory no longer names when the token may list' {
        $script:feed.ListStatus = 200
        Add-FakePackage -Feed $script:feed -Name 'Assimalign.Cohesion.Removed' -Version @(
            '10.0.0-beta.1'
            '10.0.0-preview.0.ci.1'
            '10.0.0-preview.0.ci.2'
            '10.0.0-preview.0.ci.5'
            '10.0.0-preview.0.ci.7'
        )

        $summary = Invoke-TestPrune

        $script:feed.Deleted | Should -Contain 'Assimalign.Cohesion.Removed 10.0.0-preview.0.ci.1'
        $script:feed.Deleted | Should -Contain 'Assimalign.Cohesion.Removed 10.0.0-preview.0.ci.2'
        $script:feed.Deleted.Count | Should -Be 8
        $summary.LinkedOnlyPackages | Should -Be 1
        $summary.ScannedPackages | Should -Be 3
        Should -Invoke -ModuleName CohesionPackageFeed Invoke-WebRequest -ParameterFilter {
            $Uri -match 'Gateway\.Docker|Unlinked|Other\.Package'
        } -Times 0
    }

    It 'reads and selects but deletes nothing under -WhatIf, and still writes the summary' {
        $summaryPath = Join-Path $TestDrive 'dry-run-summary.md'

        $summary = Invoke-TestPrune -WhatIf -SummaryPath $summaryPath

        $script:feed.Deleted.Count | Should -Be 0
        $summary.DryRun | Should -BeTrue
        $summary.WouldDelete | Should -Be 6
        Get-Content -LiteralPath $summaryPath -Raw | Should -Match '\| Would delete \| 6 \|'
        Should -Invoke -ModuleName CohesionPackageFeed Invoke-WebRequest -ParameterFilter { $Method -eq 'DELETE' } -Times 0
    }

    It 'deletes a package whole when a fresh listing still holds only selected versions' {
        Add-FakePackage -Feed $script:feed -Name 'Assimalign.Cohesion.Added' -Version @(
            '10.0.0-preview.0.pr.15.3'
            '10.0.0-preview.0.pr.15.8'
        )

        $summary = Invoke-TestPrune -PackageName @('Assimalign.Cohesion.Core', 'Assimalign.Cohesion.Sdk', 'Assimalign.Cohesion.Added')

        $script:feed.Deleted | Should -Contain 'Assimalign.Cohesion.Added (package)'
        $script:feed.Deleted | Should -Not -Contain 'Assimalign.Cohesion.Added 10.0.0-preview.0.pr.15.3'
        @($summary.DeletedPackages) | Should -Be @('Assimalign.Cohesion.Added')
        $summary.Deleted | Should -Be 8
    }

    It 'deletes version by version when the package gained a version since it was listed' {
        Add-FakePackage -Feed $script:feed -Name 'Assimalign.Cohesion.Added' -Version @('10.0.0-preview.0.pr.15.3')
        $script:feed.OnRelist = {
            param($package)
            $package.versions.Add([pscustomobject]@{ id = 999; name = '10.0.0-preview.0.ci.9'; created_at = '2026-10-02T00:00:00Z' })
        }

        $null = Invoke-TestPrune -PackageName @('Assimalign.Cohesion.Core', 'Assimalign.Cohesion.Sdk', 'Assimalign.Cohesion.Added')

        $script:feed.Deleted | Should -Not -Contain 'Assimalign.Cohesion.Added (package)'
        $script:feed.Deleted | Should -Contain 'Assimalign.Cohesion.Added 10.0.0-preview.0.pr.15.3'
        @(($script:feed.Packages | Where-Object name -EQ 'Assimalign.Cohesion.Added').versions.name) |
            Should -Be @('10.0.0-preview.0.ci.9')
    }

    It 'stops at the rate-limit reserve and defers the rest to the next run' {
        $script:feed.Packages.Clear()
        foreach ($name in @('Assimalign.Cohesion.Core', 'Assimalign.Cohesion.Sdk')) {
            Add-FakePackage -Feed $script:feed -Name $name -Version @(1..20 | ForEach-Object { "10.0.0-preview.0.ci.$_" })
        }

        # 128 requests: the refused organization listing leaves 127, exactly the listing guard (2
        # packages, the reserve of 100, and 25 to spare); the two version listings leave 125, and
        # the reserve stops the run after 25 of the 36 selected deletes.
        $script:feed.Remaining = 128

        $summary = Invoke-TestPrune -RateLimitReserve 100

        $summary.Selected | Should -Be 36
        $summary.Deleted | Should -Be 25
        $summary.Deferred | Should -Be 11
        $summary.DeferredReason | Should -Match 'reserve'
    }

    It 'does not start a listing the remaining budget cannot finish' {
        $script:feed.Remaining = 100 + 5

        $summary = Invoke-TestPrune -RateLimitReserve 100

        $summary.ListedVersions | Should -Be 0
        $summary.DeferredReason | Should -Match 'Listing 2 packages'
        Should -Invoke -ModuleName CohesionPackageFeed Invoke-WebRequest -ParameterFilter { $Uri -match '/versions' } -Times 0
    }

    It 'waits out a secondary rate limit and retries the same delete' {
        $script:feed.SecondaryOnce = $true

        $summary = Invoke-TestPrune

        $summary.Deleted | Should -Be 6
        Should -Invoke -ModuleName CohesionPackageFeed Start-Sleep -ParameterFilter { $Seconds -eq 30 } -Times 1 -Exactly
    }

    It 'fails the run after trying the rest when a delete is refused' {
        $refused = ($script:feed.Packages | Where-Object name -EQ 'Assimalign.Cohesion.Core').versions |
            Where-Object name -EQ '10.0.0-preview.0.ci.1'
        $script:feed.DeleteStatus[[int] $refused.id] = 403

        { Invoke-TestPrune } | Should -Throw -ExpectedMessage '*1 CI package deletions failed*Assimalign.Cohesion.Core 10.0.0-preview.0.ci.1: HTTP 403*'
        $script:feed.Deleted.Count | Should -Be 5
    }

    It 'stops deleting after five consecutive permission refusals' {
        foreach ($package in $script:feed.Packages) {
            foreach ($version in $package.versions) {
                $script:feed.DeleteStatus[[int] $version.id] = 403
            }
        }

        { Invoke-TestPrune } | Should -Throw -ExpectedMessage '*Manage Actions access*'
        Should -Invoke -ModuleName CohesionPackageFeed Invoke-WebRequest -ParameterFilter { $Method -eq 'DELETE' } -Times 5 -Exactly
    }

    It 'refuses to prune when none of the packages is visible on the feed' {
        { Invoke-TestPrune -PackageName @('Assimalign.Cohesion.Missing', 'Assimalign.Cohesion.Absent') } |
            Should -Throw -ExpectedMessage '*None of the 2 Cohesion packages is visible on the assimalign feed*'
        Should -Invoke -ModuleName CohesionPackageFeed Invoke-WebRequest -ParameterFilter { $Method -eq 'DELETE' } -Times 0
    }
}
