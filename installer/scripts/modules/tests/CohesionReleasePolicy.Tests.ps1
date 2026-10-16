#Requires -Version 5.1
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    $repositoryDirectory = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
    $localPackagingModule = Join-Path $PSScriptRoot '../CohesionLocalPackaging.psm1'
    Import-Module $localPackagingModule -Force
    Import-Module (Join-Path $PSScriptRoot '../CohesionPackaging.psm1') -Force

    # NuGet's SemVer 2.0 precedence, as NuGet.Versioning's VersionComparer applies it: numeric
    # identifiers are the ones Int32.TryParse accepts, a numeric identifier sorts below an
    # alphanumeric one, labels compare ordinally ignoring case, and with an equal prefix the
    # shorter identifier list sorts lower. Returns -1, 0, or 1.
    function Compare-NuGetVersion {
        param(
            [Parameter(Mandatory)]
            [string] $Left,

            [Parameter(Mandatory)]
            [string] $Right
        )

        $leftPart = $Left.Split('-', 2)
        $rightPart = $Right.Split('-', 2)
        $coreComparison = ([version] $leftPart[0]).CompareTo([version] $rightPart[0])
        if ($coreComparison -ne 0) {
            return [Math]::Sign($coreComparison)
        }

        if ($leftPart.Count -eq 1 -or $rightPart.Count -eq 1) {
            # A release sorts above every prerelease of the same core.
            return [Math]::Sign($leftPart.Count.CompareTo($rightPart.Count)) * -1
        }

        $leftIdentifier = $leftPart[1].Split('.')
        $rightIdentifier = $rightPart[1].Split('.')
        for ($index = 0; $index -lt [Math]::Min($leftIdentifier.Count, $rightIdentifier.Count); $index++) {
            $leftNumber = 0
            $rightNumber = 0
            $leftIsNumber = [int]::TryParse($leftIdentifier[$index], [ref] $leftNumber)
            $rightIsNumber = [int]::TryParse($rightIdentifier[$index], [ref] $rightNumber)
            $comparison = if ($leftIsNumber -and $rightIsNumber) {
                $leftNumber.CompareTo($rightNumber)
            }
            elseif ($leftIsNumber) {
                -1
            }
            elseif ($rightIsNumber) {
                1
            }
            else {
                [string]::Compare(
                    $leftIdentifier[$index],
                    $rightIdentifier[$index],
                    [System.StringComparison]::OrdinalIgnoreCase)
            }

            if ($comparison -ne 0) {
                return [Math]::Sign($comparison)
            }
        }

        return [Math]::Sign($leftIdentifier.Count.CompareTo($rightIdentifier.Count))
    }

    function Get-WorkflowJobBody {
        param(
            [Parameter(Mandatory)]
            [string] $Workflow,

            [Parameter(Mandatory)]
            [string] $Job
        )

        $escapedJob = [System.Text.RegularExpressions.Regex]::Escape($Job)
        $match = [System.Text.RegularExpressions.Regex]::Match(
            $Workflow,
            "(?ms)^  $escapedJob\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\r?\n|\z)")
        $match.Success | Should -BeTrue
        return $match.Groups['body'].Value
    }
}

AfterAll {
    Remove-Module CohesionLocalPackaging -Force -ErrorAction SilentlyContinue
    Remove-Module CohesionPackaging -Force -ErrorAction SilentlyContinue
}

Describe 'Cohesion local package versions' {
    It 'appends the reserved local identifier to a canonical prerelease' {
        $version = Get-CohesionLocalPackageVersion -Version '10.0.0-preview.1'

        $version.Version | Should -Be '10.0.0-preview.1.local'
        $version.VersionPrefix | Should -Be '10.0.0'
        $version.VersionSuffix | Should -Be 'preview.1.local'
        $version.PatchVersion | Should -Be '0-preview.1.local'
    }

    It 'rejects a stable canonical version until the post-tag bump lands' {
        { Get-CohesionLocalPackageVersion -Version '10.0.1' } |
            Should -Throw -ExpectedMessage '*post-tag bump*'
    }

    It 'preserves an exact stable identity only for canonical validation mode' {
        $version = Get-CohesionLocalPackageVersion `
            -Version '10.0.1' `
            -UseCanonicalVersion

        $version.Version | Should -Be '10.0.1'
        $version.VersionPrefix | Should -Be '10.0.1'
        $version.VersionSuffix | Should -Be ''
        $version.PatchVersion | Should -Be '1'
    }

    It 'preserves an exact prerelease identity in canonical validation mode' {
        $version = Get-CohesionLocalPackageVersion `
            -Version '10.0.1-preview.4' `
            -UseCanonicalVersion

        $version.Version | Should -Be '10.0.1-preview.4'
        $version.VersionSuffix | Should -Be 'preview.4'
        $version.PatchVersion | Should -Be '1-preview.4'
    }

    It 'rejects the local identifier on the canonical version line' {
        { Get-CohesionLocalPackageVersion -Version '10.0.0-preview.1.local' } |
            Should -Throw -ExpectedMessage '*reserved local identifier*'
    }

    It 'rejects a leading-zero numeric prerelease identifier' {
        { Get-CohesionLocalPackageVersion -Version '10.0.1-preview.03' } |
            Should -Throw -ExpectedMessage '*leading zero*'
    }

    It 'rejects a local-only identity in the release packer' {
        $releasePacker = Join-Path $repositoryDirectory 'installer/scripts/Pack-Release.ps1'

        { & $releasePacker -Version '10.0.0-preview.1.local' } |
            Should -Throw -ExpectedMessage '*cannot be release-packed*'
    }
}

Describe 'Cohesion local library package pruning' {
    It 'selects exact library package artifacts without including prefix-sharing or unrelated files' {
        $packageDirectory = Join-Path $TestDrive 'packages'
        $null = New-Item -Path $packageDirectory -ItemType Directory
        $fileName = @(
            'Assimalign.Cohesion.Core.10.0.0-beta.1.nupkg'
            'Assimalign.Cohesion.Core.10.0.0-preview.1.local.nupkg'
            'Assimalign.Cohesion.Http.10.0.0-preview.1.local.snupkg'
            'Assimalign.Cohesion.Http.10.0.0-preview.1.local.symbols.nupkg'
            'Assimalign.Cohesion.Core.Extensions.10.0.0-beta.1.nupkg'
            'Assimalign.Cohesion.Sdk.10.0.0-beta.1.nupkg'
            'Assimalign.Cohesion.Core.notes.nupkg'
            'README.txt'
        )
        foreach ($name in $fileName) {
            Set-Content -LiteralPath (Join-Path $packageDirectory $name) -Value '' -NoNewline
        }

        $removed = @(
            Get-CohesionStaleLibraryPackage `
                -PackageDirectory $packageDirectory `
                -PackageId @('Assimalign.Cohesion.Core', 'Assimalign.Cohesion.Http')
        ) | ForEach-Object { Split-Path -Leaf $_ }

        $removed | Should -Be @(
            'Assimalign.Cohesion.Core.10.0.0-beta.1.nupkg'
            'Assimalign.Cohesion.Core.10.0.0-preview.1.local.nupkg'
            'Assimalign.Cohesion.Http.10.0.0-preview.1.local.snupkg'
            'Assimalign.Cohesion.Http.10.0.0-preview.1.local.symbols.nupkg'
        )
        (Test-Path -LiteralPath (Join-Path $packageDirectory 'Assimalign.Cohesion.Core.Extensions.10.0.0-beta.1.nupkg')) | Should -BeTrue
        (Test-Path -LiteralPath (Join-Path $packageDirectory 'Assimalign.Cohesion.Sdk.10.0.0-beta.1.nupkg')) | Should -BeTrue
        (Test-Path -LiteralPath (Join-Path $packageDirectory 'Assimalign.Cohesion.Core.notes.nupkg')) | Should -BeTrue
        (Test-Path -LiteralPath (Join-Path $packageDirectory 'README.txt')) | Should -BeTrue
    }
}

Describe 'Cohesion release policy wiring' {
    BeforeAll {
        $releaseWorkflow = Get-Content -LiteralPath (Join-Path $repositoryDirectory '.github/workflows/release.yml') -Raw
        $sdkSmokeWorkflow = Get-Content -LiteralPath (Join-Path $repositoryDirectory '.github/workflows/sdk-smoke.yml') -Raw
        $releasePacker = Get-Content -LiteralPath (Join-Path $repositoryDirectory 'installer/scripts/Pack-Release.ps1') -Raw
        $databaseWorkflow = Get-Content -LiteralPath (Join-Path $repositoryDirectory '.github/workflows/resource-database.yml') -Raw
        $installLocal = Get-Content -LiteralPath (Join-Path $repositoryDirectory 'installer/scripts/Install-Local.ps1') -Raw
    }

    It 'keeps the signed-off canonical version line' {
        $version = & (Join-Path $repositoryDirectory 'installer/scripts/Get-CohesionVersion.ps1') `
            -RepoRoot $repositoryDirectory

        $version | Should -Be '10.0.0-preview.1'
    }

    It 'declares promotion as an opt-in manual dispatch' {
        $releaseWorkflow | Should -Match '(?ms)^  workflow_dispatch:\r?\n    inputs:\r?\n.*?      promote:\r?\n.*?        type: boolean\r?\n        default: false'

        $promotionJob = Get-WorkflowJobBody -Workflow $releaseWorkflow -Job 'publish-nuget:'
        $promotionJob | Should -Match "github\.event_name == 'workflow_dispatch'"
        $promotionJob | Should -Match 'inputs\.promote == true'
        $promotionJob | Should -Match '(?m)^    environment:\r?$'
        $promotionJob | Should -Match '(?m)^      name: nuget-org\r?$'
    }

    It 'requires a published release tag while running trusted workflow code from the default branch' {
        $releaseWorkflow | Should -Match '(?ms)^      release_tag:\r?\n.*?        required: true\r?$'
        $releaseWorkflow | Should -Match 'inputs\.release_tag'
        $releaseWorkflow | Should -Match 'COHESION_WORKFLOW_REF: \$\{\{ github\.ref \}\}'
        $releaseWorkflow | Should -Match 'COHESION_DEFAULT_BRANCH: \$\{\{ github\.event\.repository\.default_branch \}\}'
        $releaseWorkflow | Should -Match 'releases/tags/\$env:COHESION_RELEASE_TAG'
        $releaseWorkflow | Should -Match 'refs/heads/\$env:COHESION_DEFAULT_BRANCH'
        $releaseWorkflow | Should -Match 'tag_ref=refs/tags/\$env:COHESION_RELEASE_TAG'
        $releaseWorkflow | Should -Match 'ref: \$\{\{ steps\.release\.outputs\.tag_ref \}\}'
        $releaseWorkflow | Should -Match 'git merge-base --is-ancestor \$releaseCommit origin/main'
    }

    It 'keeps GitHub Packages staging independent from the promote flag' {
        $stagingJob = Get-WorkflowJobBody -Workflow $releaseWorkflow -Job 'publish-github-packages:'

        $stagingJob | Should -Not -Match 'inputs\.promote'
        $stagingJob | Should -Not -Match '(?m)^    if:'
        $stagingJob | Should -Match '(?m)^    needs: \[pack-packages, validate-consumer\]\r?$'
        (Get-WorkflowJobBody -Workflow $releaseWorkflow -Job 'prepare:') |
            Should -Not -Match 'inputs\.promote'
    }

    It 'strict-packs and exercises package-only SDK consumers without publishing' {
        $releasePacker | Should -Match '(?m)^\s*\[switch\] \$SkipLibraries,\r?$'
        $releasePacker | Should -Match "Where-Object \{ -not \`$SkipLibraries -or \`$_.Kind -ne 'Library' \}"
        $releasePacker | Should -Match '(?m)^\$supportedRuntimeIdentifier = Get-CohesionReleaseRuntimeIdentifier\r?$'
        $releasePacker | Should -Match 'Unsupported release runtime identifier\(s\)'
        $sdkSmokeWorkflow | Should -Match '(?m)^  pull_request:\r?$'
        # main, and a manual run with all-os, cover all three OSes; other pushes and pull requests
        # run Linux only. The release's validate-consumer job below stays three-OS unconditionally.
        $allOsOnMain = @'
os: ${{ fromJSON((github.ref == 'refs/heads/main' || inputs.all-os) && '["ubuntu-latest","windows-latest","macos-latest"]' || '["ubuntu-latest"]') }}
'@
        $sdkSmokeWorkflow | Should -Match ('(?m)^        ' + [regex]::Escape($allOsOnMain) + '\r?$')
        $sdkSmokeWorkflow | Should -Match '\./installer/scripts/Pack-Release\.ps1'
        $sdkSmokeWorkflow | Should -Match '(?m)^\s*-SkipLibraries\r?$'
        $sdkSmokeWorkflow | Should -Match 'Invoke-SdkConsumerSmoke\.ps1'
        $sdkSmokeWorkflow | Should -Not -Match 'packages:\s*write'
        $sdkSmokeWorkflow | Should -Not -Match 'dotnet nuget push'
    }

    It 'gates both release publishers on the three-OS packed-consumer validation' {
        $consumerJob = Get-WorkflowJobBody -Workflow $releaseWorkflow -Job 'validate-consumer:'
        $stagingJob = Get-WorkflowJobBody -Workflow $releaseWorkflow -Job 'publish-github-packages:'
        $promotionJob = Get-WorkflowJobBody -Workflow $releaseWorkflow -Job 'publish-nuget:'

        $consumerJob | Should -Match '(?m)^    needs: \[prepare, pack-packages\]\r?$'
        $consumerJob | Should -Match '(?m)^        os: \[ubuntu-latest, windows-latest, macos-latest\]\r?$'
        $consumerJob | Should -Match 'name: Assimalign\.Cohesion\.Packages'
        $consumerJob | Should -Match 'Invoke-SdkConsumerSmoke\.ps1'
        $consumerJob | Should -Not -Match 'dotnet nuget push'
        $stagingJob | Should -Match '(?m)^    needs: \[pack-packages, validate-consumer\]\r?$'
        $promotionJob | Should -Match '(?m)^    needs: \[pack-packages, validate-consumer, publish-github-packages\]\r?$'
    }

    It 'retires the replace-on-main package publisher and disabled workflow' {
        (Test-Path -LiteralPath (Join-Path $repositoryDirectory '.github/scripts/Publish-Nupkg.ps1')) |
            Should -BeFalse
        (Test-Path -LiteralPath (Join-Path $repositoryDirectory '.github/workflows-diabled/framework.yml')) |
            Should -BeFalse

        $activeWorkflow = Get-ChildItem `
            -LiteralPath (Join-Path $repositoryDirectory '.github/workflows') `
            -Filter '*.yml' `
            -File |
            ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
        ($activeWorkflow -join "`n") | Should -Not -Match 'Publish-Nupkg\.ps1'
    }

    It 'passes the local version vector to every local pack invocation and prunes library artifacts' {
        $packCallCount = [System.Text.RegularExpressions.Regex]::Matches(
            $installLocal,
            '(?m)^\s*& dotnet pack ').Count
        $localPropertyCount = [System.Text.RegularExpressions.Regex]::Matches(
            $installLocal,
            '(?m)^\s*& dotnet pack .*@localPackProperties\r?$').Count

        $packCallCount | Should -BeGreaterThan 0
        $localPropertyCount | Should -Be $packCallCount

        # Batched packs (Invoke-CohesionPackBatch, continued across lines with backticks) carry the
        # same vector through -Property.
        $batchCall = @([System.Text.RegularExpressions.Regex]::Matches(
            $installLocal,
            '(?m)^\s*Invoke-CohesionPackBatch(?:[^\r\n]*`\r?\n)*[^\r\n]*') | ForEach-Object Value)
        $batchCall.Count | Should -BeGreaterThan 0
        foreach ($call in $batchCall) {
            $call | Should -Match '-Property (?:\$localPackProperties|@\(\$localPackProperties )'
        }
        $installLocal | Should -Match 'Get-CohesionLocalPackageVersion'
        $installLocal | Should -Match 'Get-CohesionStaleLibraryPackage'
        $installLocal | Should -Match '(?ms)foreach \(\$packagePath in \$staleLibraryPackage\).*?Remove-Item -LiteralPath \$packagePath -Force'
        $installLocal | Should -Match 'Get-CohesionReleaseLibrary -RepositoryDirectory \$repoRoot'
        $installLocal | Should -Match 'Join-Path \(Join-Path \$globalPackagesRoot \$pkg\) \$cohesionVersion'
        $releaseWorkflow | Should -Match '-RepositoryCommit \$env:COHESION_RELEASE_COMMIT'
    }

    It 'generates the Database SDK consumer pin only after successful package creation' {
        # The consumer pin derives from the root global.json, which must name both SDKs the Database
        # sample imports and never a .local build: that identity exists only in the generated pin.
        $globalJsonPath = Join-Path $repositoryDirectory 'global.json'
        $globalJson = (Get-Content -LiteralPath $globalJsonPath -Raw) -replace '(?m)^\s*//.*$', '' |
            ConvertFrom-Json
        $sdkPins = $globalJson.'msbuild-sdks'
        $sdkPins.'Assimalign.Cohesion.Sdk' | Should -Not -BeNullOrEmpty
        $sdkPins.'Assimalign.Cohesion.Sdk.Database' | Should -Not -BeNullOrEmpty
        @($sdkPins.PSObject.Properties.Value | Where-Object { $_ -like '*.local' }).Count |
            Should -Be 0

        $lastPack = [Math]::Max(
            $installLocal.LastIndexOf('& dotnet pack ', [StringComparison]::Ordinal),
            $installLocal.LastIndexOf('Invoke-CohesionPackBatch ', [StringComparison]::Ordinal))
        $generation = $installLocal.IndexOf('$consumerSourcePath =', [StringComparison]::Ordinal)
        $generation | Should -BeGreaterThan $lastPack
        $installLocal | Should -Match "Consumer SDK '.+?' was not produced"
        $installLocal | Should -Match 'Move-Item -LiteralPath \$temporaryGlobalJsonPath'
    }

    It 'keeps the Database SDK consumer on the package-backed local source' {
        [xml]$nuget = Get-Content -LiteralPath (
            Join-Path $repositoryDirectory 'resources/Database/Assimalign.Cohesion.Database.Testing/fixtures/Assimalign.Cohesion.Database.SampleHost/NuGet.Config') -Raw
        $localSource = @($nuget.configuration.packageSourceMapping.packageSource |
            Where-Object key -EQ 'cohesion-local')[0]
        $localPatterns = @($localSource.package | ForEach-Object { $_.pattern })
        $localPatterns | Should -Contain 'Assimalign.Cohesion.*'

        $releaseWorkflow | Should -Match 'local-sdk-use-canonical-version:'
        $databaseWorkflow | Should -Match 'resources/Database/global.json'
    }
}

Describe 'Cohesion CI package versions' {
    It 'extends the canonical predecessor for a main build' {
        $version = Get-CohesionCiPackageVersion -Version '10.0.0-preview.1' -BuildNumber 57

        $version.Version | Should -Be '10.0.0-preview.0.ci.57'
        $version.BaseVersion | Should -Be '10.0.0-preview.0'
        $version.VersionPrefix | Should -Be '10.0.0'
        $version.VersionSuffix | Should -Be 'preview.0.ci.57'
        $version.Channel | Should -Be 'ci'
    }

    It 'carries the pull request number for a pull request build' {
        $version = Get-CohesionCiPackageVersion `
            -Version '10.0.0-preview.1' `
            -BuildNumber 58 `
            -PullRequestNumber 1520

        $version.Version | Should -Be '10.0.0-preview.0.pr.1520.58'
        $version.VersionSuffix | Should -Be 'preview.0.pr.1520.58'
        $version.Channel | Should -Be 'pr'
    }

    It 'sorts every build of <Canonical> above <Previous> and below the canonical version' -ForEach @(
        @{ Canonical = '10.0.0-preview.1'; Previous = '9.0.0' }
        @{ Canonical = '10.0.0-preview.2'; Previous = '10.0.0-preview.1' }
        @{ Canonical = '10.0.0-preview.10'; Previous = '10.0.0-preview.9' }
        @{ Canonical = '10.0.0-rc.1'; Previous = '10.0.0-preview.9' }
        @{ Canonical = '10.0.1-preview.1'; Previous = '10.0.0' }
    ) {
        $build = @(
            (Get-CohesionCiPackageVersion -Version $Canonical -BuildNumber 9).Version
            (Get-CohesionCiPackageVersion -Version $Canonical -BuildNumber 10).Version
            (Get-CohesionCiPackageVersion -Version $Canonical -BuildNumber 9 -PullRequestNumber 1520).Version
            (Get-CohesionCiPackageVersion -Version $Canonical -BuildNumber 10 -PullRequestNumber 1520).Version
        )

        foreach ($version in $build) {
            $version | Should -Match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*$'
            Compare-NuGetVersion -Left $version -Right $Canonical | Should -Be -1
            Compare-NuGetVersion -Left $Previous -Right $version | Should -Be -1
        }

        # Run 10 above run 9 is the order a text comparison gets wrong.
        Compare-NuGetVersion -Left $build[0] -Right $build[1] | Should -Be -1
        Compare-NuGetVersion -Left $build[2] -Right $build[3] | Should -Be -1
    }

    It 'rejects a stable canonical version' {
        { Get-CohesionCiPackageVersion -Version '10.0.1' -BuildNumber 1 } |
            Should -Throw -ExpectedMessage '*stable*post-tag bump*'
    }

    It 'rejects canonical prerelease <Version>, which has no positive trailing number' -ForEach @(
        @{ Version = '10.0.0-preview' }
        @{ Version = '10.0.0-preview.0' }
    ) {
        { Get-CohesionCiPackageVersion -Version $Version -BuildNumber 1 } |
            Should -Throw -ExpectedMessage '*positive integer*'
    }

    It 'rejects the reserved <Identifier> identifier on the canonical version' -ForEach @(
        @{ Identifier = 'local'; Version = '10.0.0-preview.1.local' }
        @{ Identifier = 'ci'; Version = '10.0.0-ci.1' }
        @{ Identifier = 'pr'; Version = '10.0.0-pr.1' }
    ) {
        { Get-CohesionCiPackageVersion -Version $Version -BuildNumber 1 } |
            Should -Throw -ExpectedMessage "*reserved '$Identifier'*"
    }

    It 'rejects a leading-zero numeric prerelease identifier' {
        { Get-CohesionCiPackageVersion -Version '10.0.1-preview.03' -BuildNumber 1 } |
            Should -Throw -ExpectedMessage '*leading zero*'
    }

    It 'rejects build number <BuildNumber>, which NuGet cannot order as a number' -ForEach @(
        @{ BuildNumber = '0' }
        @{ BuildNumber = '2147483648' }
    ) {
        { Get-CohesionCiPackageVersion -Version '10.0.0-preview.1' -BuildNumber $BuildNumber } |
            Should -Throw
        { Get-CohesionCiPackageVersion -Version '10.0.0-preview.1' -BuildNumber 1 -PullRequestNumber $BuildNumber } |
            Should -Throw
    }
}

Describe 'Cohesion CI package version parsing' {
    It 'reads back every field of a <Channel> build of <Canonical>' -ForEach @(
        @{ Canonical = '10.0.0-preview.1'; Channel = 'ci'; PullRequestNumber = $null }
        @{ Canonical = '10.0.0-preview.1'; Channel = 'pr'; PullRequestNumber = 1558 }
        @{ Canonical = '10.0.0-preview.10'; Channel = 'pr'; PullRequestNumber = 15 }
        @{ Canonical = '10.0.0-rc.1'; Channel = 'ci'; PullRequestNumber = $null }
        @{ Canonical = '10.0.1-beta.2.3'; Channel = 'pr'; PullRequestNumber = 2147483647 }
    ) {
        $arguments = @{ Version = $Canonical; BuildNumber = 42 }
        if ($null -ne $PullRequestNumber) {
            $arguments.PullRequestNumber = $PullRequestNumber
        }
        $derived = Get-CohesionCiPackageVersion @arguments

        $parsed = ConvertFrom-CohesionCiPackageVersion -Version $derived.Version

        $parsed.Version | Should -Be $derived.Version
        $parsed.BaseVersion | Should -Be $derived.BaseVersion
        $parsed.VersionPrefix | Should -Be $derived.VersionPrefix
        $parsed.Channel | Should -Be $Channel
        $parsed.PullRequestNumber | Should -Be $PullRequestNumber
        $parsed.BuildNumber | Should -Be 42
    }

    It 'treats <Version> as no CI build' -ForEach @(
        @{ Version = '10.0.0-preview.1' }
        @{ Version = '10.0.0-beta.1' }
        @{ Version = '10.0.0' }
        @{ Version = '10.0.0-preview.1.local' }
        @{ Version = '10.0.0-ci.5' }
        @{ Version = '10.0.0-pr.15.2' }
        @{ Version = '10.0.0-preview.0.ci' }
        @{ Version = '10.0.0-preview.0.ci.0' }
        @{ Version = '10.0.0-preview.0.ci.05' }
        @{ Version = '10.0.0-preview.0.ci.2147483648' }
        @{ Version = '10.0.0-preview.0.ci.5.local' }
        @{ Version = '10.0.0-preview.0.ci.5+build.7' }
        @{ Version = '10.0.0-preview.0.pr.15' }
        @{ Version = '10.0.0-preview.0.pr.015.2' }
        @{ Version = '10.0.0-preview.0.pr.15.2.1' }
        @{ Version = '10.0.0-preview.local.ci.5' }
        @{ Version = '10.0.0-preview.0.ci.4.ci.5' }
        @{ Version = '10.0.0-preview.ci.5' }
        @{ Version = '10.0.0-preview.00.ci.5' }
        @{ Version = '010.0.0-preview.0.ci.5' }
        @{ Version = '' }
    ) {
        ConvertFrom-CohesionCiPackageVersion -Version $Version | Should -BeNullOrEmpty
    }

    It 'reads the ci and pr labels case-insensitively, as NuGet compares them' {
        (ConvertFrom-CohesionCiPackageVersion -Version '10.0.0-preview.0.CI.7').Channel | Should -Be 'ci'
        (ConvertFrom-CohesionCiPackageVersion -Version '10.0.0-preview.0.Pr.15.7').PullRequestNumber | Should -Be 15
    }
}

Describe 'Cohesion CI package prune selection' {
    BeforeAll {
        $script:nextVersionId = 1000
        $script:epoch = [System.DateTimeOffset]::new(2026, 10, 1, 0, 0, 0, [System.TimeSpan]::Zero)

        # One feed entry as the pruner lists it. Hours stands in for the publish time.
        function New-TestPackageVersion {
            param(
                [Parameter(Mandatory)]
                [string] $Version,

                [string] $PackageName = 'Assimalign.Cohesion.Core',

                [double] $Hours = 0
            )

            $script:nextVersionId++
            return [pscustomobject]@{
                PackageName = $PackageName
                VersionId   = $script:nextVersionId
                Version     = $Version
                CreatedAt   = $script:epoch.AddHours($Hours).ToString('o')
            }
        }
    }

    It 'selects only the closed pull request, never one whose number shares its digits' {
        $feed = @(
            New-TestPackageVersion -Version '10.0.0-preview.0.pr.15.3'
            New-TestPackageVersion -Version '10.0.0-preview.0.pr.15.9'
            New-TestPackageVersion -Version '10.0.0-preview.0.pr.152.4'
            New-TestPackageVersion -Version '10.0.0-preview.0.pr.151.5'
            New-TestPackageVersion -Version '10.0.0-preview.0.pr.1.15'
            New-TestPackageVersion -Version '10.0.0-preview.0.pr.115.6'
            New-TestPackageVersion -Version '10.0.0-preview.0.ci.15'
            New-TestPackageVersion -Version '10.0.0-preview.0.pr.15.7' -PackageName 'Assimalign.Cohesion.Sdk'
        )

        $selected = Select-CohesionCiPackageVersionToPrune -PackageVersion $feed -ClosedPullRequestNumber 15

        @($selected.Version | Sort-Object) | Should -Be @(
            '10.0.0-preview.0.pr.15.3'
            '10.0.0-preview.0.pr.15.7'
            '10.0.0-preview.0.pr.15.9'
        )
        $selected.PullRequestNumber | Should -Be @(15, 15, 15)
        $selected[0].Reason | Should -Be 'pull request #15 is closed'
    }

    It 'leaves open pull requests and main builds alone when nothing asks for them' {
        $feed = @(
            New-TestPackageVersion -Version '10.0.0-preview.0.pr.20.1'
            New-TestPackageVersion -Version '10.0.0-preview.0.ci.2'
            New-TestPackageVersion -Version '10.0.0-preview.0.ci.3'
        )

        $selected = Select-CohesionCiPackageVersionToPrune -PackageVersion $feed -ClosedPullRequestNumber 21

        $selected.Count | Should -Be 0
    }

    It 'keeps the newest main builds of each base on every package' {
        $feed = foreach ($package in @('Assimalign.Cohesion.Core', 'Assimalign.Cohesion.Http')) {
            foreach ($build in 1..5) {
                New-TestPackageVersion -PackageName $package -Version "10.0.0-preview.0.ci.$build" -Hours $build
            }

            # The post-tag bump to preview.2 starts a new base; its builds rank on their own.
            foreach ($build in 6..8) {
                New-TestPackageVersion -PackageName $package -Version "10.0.0-preview.1.ci.$build" -Hours $build
            }
        }

        $selected = Select-CohesionCiPackageVersionToPrune -PackageVersion $feed -MainBuildCount 2

        @($selected | ForEach-Object { "$($_.PackageName) $($_.Version)" } | Sort-Object) | Should -Be @(
            'Assimalign.Cohesion.Core 10.0.0-preview.0.ci.1'
            'Assimalign.Cohesion.Core 10.0.0-preview.0.ci.2'
            'Assimalign.Cohesion.Core 10.0.0-preview.0.ci.3'
            'Assimalign.Cohesion.Core 10.0.0-preview.1.ci.6'
            'Assimalign.Cohesion.Http 10.0.0-preview.0.ci.1'
            'Assimalign.Cohesion.Http 10.0.0-preview.0.ci.2'
            'Assimalign.Cohesion.Http 10.0.0-preview.0.ci.3'
            'Assimalign.Cohesion.Http 10.0.0-preview.1.ci.6'
        )
        $selected[0].Reason | Should -Be 'main build 1 of 10.0.0-preview.0 is older than the newest 2'
    }

    It 'ranks whole builds, so a build missing from one package still counts against it' {
        # Build 3 reached only Core (a partial publish). It is still one of the two newest builds,
        # so Http's build 1 goes rather than Http keeping three builds.
        $feed = @(
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.Core' -Version '10.0.0-preview.0.ci.1' -Hours 1
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.Core' -Version '10.0.0-preview.0.ci.2' -Hours 2
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.Core' -Version '10.0.0-preview.0.ci.3' -Hours 3
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.Http' -Version '10.0.0-preview.0.ci.1' -Hours 1
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.Http' -Version '10.0.0-preview.0.ci.2' -Hours 2
        )

        $selected = Select-CohesionCiPackageVersionToPrune -PackageVersion $feed -MainBuildCount 2

        @($selected | ForEach-Object { "$($_.PackageName) $($_.Version)" } | Sort-Object) | Should -Be @(
            'Assimalign.Cohesion.Core 10.0.0-preview.0.ci.1'
            'Assimalign.Cohesion.Http 10.0.0-preview.0.ci.1'
        )
    }

    It 'ranks by publish time, so restarted run numbers keep the fresh builds' {
        $feed = @(
            New-TestPackageVersion -Version '10.0.0-preview.0.ci.300' -Hours 1
            New-TestPackageVersion -Version '10.0.0-preview.0.ci.301' -Hours 2
            New-TestPackageVersion -Version '10.0.0-preview.0.ci.1' -Hours 10
            New-TestPackageVersion -Version '10.0.0-preview.0.ci.2' -Hours 11
        )

        $selected = Select-CohesionCiPackageVersionToPrune -PackageVersion $feed -MainBuildCount 2

        @($selected.Version) | Should -Be @('10.0.0-preview.0.ci.300', '10.0.0-preview.0.ci.301')
    }

    It 'never selects a release, stable, or local version' {
        $canonicalVersion = & (Join-Path $repositoryDirectory 'installer/scripts/Get-CohesionVersion.ps1') `
            -RepoRoot $repositoryDirectory
        $nonCiVersion = @(
            $canonicalVersion
            '10.0.0-beta.1'
            '10.0.0-preview.1'
            '10.0.0-preview.2'
            '10.0.0-rc.1'
            '10.0.0'
            '1.0.0'
            '10.0.0-preview.1.local'
            '10.0.0-ci.5'
            '10.0.0-pr.15.2'
            '10.0.0-preview.0.pr.15'
        )
        $feed = @(
            foreach ($package in @('Assimalign.Cohesion.Sdk', 'Assimalign.Cohesion.Core')) {
                foreach ($version in $nonCiVersion) {
                    New-TestPackageVersion -PackageName $package -Version $version -Hours (-100)
                }

                New-TestPackageVersion -PackageName $package -Version '10.0.0-preview.0.ci.1' -Hours 1
                New-TestPackageVersion -PackageName $package -Version '10.0.0-preview.0.ci.2' -Hours 2
                New-TestPackageVersion -PackageName $package -Version '10.0.0-preview.0.pr.15.3' -Hours 3
            }
        )

        # The most aggressive call possible: every pull request number closed, one main build kept.
        $selected = Select-CohesionCiPackageVersionToPrune `
            -PackageVersion $feed `
            -ClosedPullRequestNumber @(1, 2, 5, 15) `
            -MainBuildCount 1

        foreach ($version in $nonCiVersion) {
            $selected.Version | Should -Not -Contain $version
        }
        @($selected.Version | Sort-Object -Unique) | Should -Be @(
            '10.0.0-preview.0.ci.1'
            '10.0.0-preview.0.pr.15.3'
        )
        foreach ($entry in $selected) {
            ConvertFrom-CohesionCiPackageVersion -Version $entry.Version | Should -Not -BeNullOrEmpty
        }
    }

    It 'orders closed pull requests first, then main builds oldest first, SDKs first within a build' {
        $feed = @(
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.Core' -Version '10.0.0-preview.0.ci.2' -Hours 2
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.Sdk.Web' -Version '10.0.0-preview.0.ci.2' -Hours 2
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.Core' -Version '10.0.0-preview.0.ci.1' -Hours 1
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.Sdk' -Version '10.0.0-preview.0.ci.1' -Hours 1
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.Core' -Version '10.0.0-preview.0.ci.3' -Hours 3
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.Core' -Version '10.0.0-preview.0.pr.30.9' -Hours 9
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.SdkTools' -Version '10.0.0-preview.0.pr.7.8' -Hours 8
            New-TestPackageVersion -PackageName 'Assimalign.Cohesion.Sdk' -Version '10.0.0-preview.0.pr.7.8' -Hours 8
        )

        $selected = Select-CohesionCiPackageVersionToPrune `
            -PackageVersion $feed `
            -ClosedPullRequestNumber @(30, 7) `
            -MainBuildCount 1

        @($selected | ForEach-Object { "$($_.PackageName) $($_.Version)" }) | Should -Be @(
            'Assimalign.Cohesion.Sdk 10.0.0-preview.0.pr.7.8'
            'Assimalign.Cohesion.SdkTools 10.0.0-preview.0.pr.7.8'
            'Assimalign.Cohesion.Core 10.0.0-preview.0.pr.30.9'
            'Assimalign.Cohesion.Sdk 10.0.0-preview.0.ci.1'
            'Assimalign.Cohesion.Core 10.0.0-preview.0.ci.1'
            'Assimalign.Cohesion.Sdk.Web 10.0.0-preview.0.ci.2'
            'Assimalign.Cohesion.Core 10.0.0-preview.0.ci.2'
        )
    }

    It 'requires at least one kept main build' {
        { Select-CohesionCiPackageVersionToPrune -PackageVersion @() -MainBuildCount 0 } | Should -Throw
    }

    It 'refuses to rank a main build without a publish time' {
        $feed = @([pscustomobject]@{
                PackageName = 'Assimalign.Cohesion.Core'
                VersionId   = 1
                Version     = '10.0.0-preview.0.ci.1'
                CreatedAt   = $null
            })

        { Select-CohesionCiPackageVersionToPrune -PackageVersion $feed -MainBuildCount 1 } |
            Should -Throw -ExpectedMessage '*no parseable CreatedAt*'
    }

    It 'refuses an entry with no version id' {
        $feed = @([pscustomobject]@{
                PackageName = 'Assimalign.Cohesion.Core'
                VersionId   = $null
                Version     = '10.0.0-preview.0.pr.1.1'
                CreatedAt   = $null
            })

        { Select-CohesionCiPackageVersionToPrune -PackageVersion $feed -ClosedPullRequestNumber 1 } |
            Should -Throw -ExpectedMessage "*missing 'VersionId'*"
    }
}

Describe 'Cohesion CI package workflow' {
    BeforeAll {
        $ciWorkflow = Get-Content -LiteralPath (Join-Path $repositoryDirectory '.github/workflows/ci-packages.yml') -Raw
    }

    It 'runs on pushes to main and on pull request commits, never on pull_request_target' {
        $ciWorkflow | Should -Match '(?m)^  push:\r?\n    branches: \[main\]\r?$'
        $ciWorkflow | Should -Match '(?m)^  pull_request:\r?\n    types: \[opened, synchronize, reopened\]\r?$'
        $ciWorkflow | Should -Not -Match '(?m)^  pull_request_target:'
    }

    It 'publishes only from same-repository pull requests that Dependabot did not trigger' {
        $prepareJob = Get-WorkflowJobBody -Workflow $ciWorkflow -Job 'prepare:'

        $prepareJob | Should -Match "github\.event_name == 'push'"
        $prepareJob | Should -Match 'github\.event\.pull_request\.head\.repo\.full_name == github\.repository'
        $prepareJob | Should -Match "github\.actor != 'dependabot\[bot\]'"
    }

    It 'derives the version with the workflow commit tooling, so a branch that predates it still works' {
        $prepareJob = Get-WorkflowJobBody -Workflow $ciWorkflow -Job 'prepare:'

        # No ref: the tooling checkout is the commit the workflow file came from (the merge commit
        # for a pull request), never the build commit, whose branch may predate the function.
        $prepareJob | Should -Match '(?m)^      - name: Checkout workflow tooling\r?\n        uses: actions/checkout@[0-9a-f]{40} # v[0-9.]+\r?\n        with:\r?\n          persist-credentials: false\r?\n          path: _ci/tooling\r?\n          sparse-checkout: installer/scripts/modules\r?$'
        $prepareJob | Should -Match 'Import-Module \./_ci/tooling/installer/scripts/modules/CohesionPackaging\.psm1 -Force'
        $prepareJob | Should -Not -Match 'Import-Module \./installer/'
        $prepareJob | Should -Match '\(& \./installer/scripts/Get-CohesionVersion\.ps1\)'
    }

    It 'packs the full release inventory at the derived CI version' {
        $prepareJob = Get-WorkflowJobBody -Workflow $ciWorkflow -Job 'prepare:'
        $packJob = Get-WorkflowJobBody -Workflow $ciWorkflow -Job 'pack-packages:'

        $prepareJob | Should -Match 'Get-CohesionCiPackageVersion @arguments'
        $prepareJob | Should -Match 'BuildNumber = \[int\] \$env:COHESION_BUILD_NUMBER'
        $prepareJob | Should -Match 'COHESION_BUILD_NUMBER: \$\{\{ github\.run_number \}\}'
        $packJob | Should -Match '\./installer/scripts/Pack-Release\.ps1'
        $packJob | Should -Match '-Version \$env:COHESION_PACKAGE_VERSION'
        $packJob | Should -Match '-RepositoryCommit \$env:COHESION_BUILD_COMMIT'
        $packJob | Should -Not -Match 'SkipLibraries'
    }

    It 'publishes to GitHub Packages only, after the packed-consumer validation' {
        $consumerJob = Get-WorkflowJobBody -Workflow $ciWorkflow -Job 'validate-consumer:'
        $publishJob = Get-WorkflowJobBody -Workflow $ciWorkflow -Job 'publish-github-packages:'

        $consumerJob | Should -Match 'Invoke-SdkConsumerSmoke\.ps1'
        $publishJob | Should -Match '(?m)^    needs: \[prepare, pack-packages, validate-consumer\]\r?$'
        $publishJob | Should -Match "--source 'https://nuget\.pkg\.github\.com/assimalign/index\.json'"
        [System.Text.RegularExpressions.Regex]::Matches($ciWorkflow, 'packages: write').Count |
            Should -Be 1
        $publishJob | Should -Match 'packages: write'
        $ciWorkflow | Should -Match '(?m)^permissions:\r?\n  contents: read\r?$'
        $ciWorkflow | Should -Not -Match 'api\.nuget\.org'
        $ciWorkflow | Should -Not -Match 'NuGet/login'
        $ciWorkflow | Should -Not -Match 'id-token:'
    }

    It 'skips duplicate versions only on a re-run attempt' {
        $publishJob = Get-WorkflowJobBody -Workflow $ciWorkflow -Job 'publish-github-packages:'

        $publishJob | Should -Match 'COHESION_RUN_ATTEMPT: \$\{\{ github\.run_attempt \}\}'
        $publishJob | Should -Match "(?ms)if \(\[int\] \`$env:COHESION_RUN_ATTEMPT -gt 1\) \{\s*\`$duplicateArgument = @\('--skip-duplicate'\)"
        [System.Text.RegularExpressions.Regex]::Matches($publishJob, "'--skip-duplicate'").Count |
            Should -Be 1
    }

    It 'lets a newer pull request commit cancel a pack but never a publish' {
        $packJob = Get-WorkflowJobBody -Workflow $ciWorkflow -Job 'pack-packages:'
        $publishJob = Get-WorkflowJobBody -Workflow $ciWorkflow -Job 'publish-github-packages:'

        $packJob | Should -Match 'group: cohesion-ci-packages-pack-\$\{\{ github\.event\.pull_request\.number \|\| github\.run_id \}\}'
        $packJob | Should -Match 'cancel-in-progress: true'
        $publishJob | Should -Not -Match 'concurrency:'
        $ciWorkflow | Should -Not -Match '(?m)^concurrency:'
    }
}

Describe 'Cohesion CI package prune workflow' {
    BeforeAll {
        $pruneWorkflow = Get-Content -LiteralPath (Join-Path $repositoryDirectory '.github/workflows/ci-packages-prune.yml') -Raw
        $pruneJob = Get-WorkflowJobBody -Workflow $pruneWorkflow -Job 'prune:'
    }

    It 'runs when a pull request closes, hourly, and on demand as a dry run by default' {
        $pruneWorkflow | Should -Match '(?m)^  pull_request_target:\r?\n    types: \[closed\]\r?$'
        # Closing a pull request that ci-packages.yml never published for is not worth a scan.
        $publishFilter = [System.Text.RegularExpressions.Regex]::Match(
            (Get-Content -LiteralPath (Join-Path $repositoryDirectory '.github/workflows/ci-packages.yml') -Raw),
            "(?ms)^  pull_request:\r?\n    types: \[opened, synchronize, reopened\]\r?\n    paths-ignore:\r?\n(?<paths>(?:      - '[^']+'\r?\n)+)").Groups['paths'].Value
        $publishFilter | Should -Not -BeNullOrEmpty
        $pruneWorkflow | Should -Match ('(?ms)^  pull_request_target:\r?\n    types: \[closed\]\r?\n(?:    #.*?\r?\n)*    paths-ignore:\r?\n' + [regex]::Escape($publishFilter))
        $pruneWorkflow | Should -Match "(?m)^  schedule:\r?\n    - cron: '41 \* \* \* \*'\r?$"
        $pruneWorkflow | Should -Match '(?ms)^  workflow_dispatch:\r?\n    inputs:\r?\n      dry-run:\r?\n.*?        type: boolean\r?\n        default: true\r?$'
        $pruneWorkflow | Should -Not -Match '(?m)^  pull_request:'
        $pruneJob | Should -Match "COHESION_PRUNE_DRY_RUN: \`$\{\{ github\.event_name == 'workflow_dispatch' && inputs\.dry-run \}\}"
        $pruneJob | Should -Match "WhatIf         = \`$env:COHESION_PRUNE_DRY_RUN -eq 'true'"
    }

    It 'never checks out or reads pull request code under pull_request_target' {
        $pruneJob | Should -Match '(?m)^      - name: Checkout pruning tooling\r?\n        uses: actions/checkout@[0-9a-f]{40} # v[0-9.]+\r?\n        with:\r?\n          persist-credentials: false\r?\n          sparse-checkout: installer/scripts/modules\r?$'
        $pruneWorkflow | Should -Not -Match '(?m)^\s+ref:'
        # The only payload field read is the head repository, in the job condition.
        $payloadReference = @([System.Text.RegularExpressions.Regex]::Matches(
                $pruneWorkflow,
                'github\.event\.pull_request\.[A-Za-z0-9_.]+') | ForEach-Object Value | Sort-Object -Unique)
        $payloadReference | Should -Be @('github.event.pull_request.head.repo.full_name')
        $pruneJob | Should -Match "github\.event_name != 'pull_request_target'"
        $pruneJob | Should -Match "github\.actor != 'dependabot\[bot\]'"
    }

    It 'holds package write and pull request read on the prune job only' {
        $pruneWorkflow | Should -Match '(?m)^permissions:\r?\n  contents: read\r?$'
        $pruneJob | Should -Match '(?m)^    permissions:\r?\n      contents: read\r?\n(?:\s*#.*\r?\n)*      packages: write\r?\n(?:\s*#.*\r?\n)*      pull-requests: read\r?$'
        [System.Text.RegularExpressions.Regex]::Matches($pruneWorkflow, 'packages: write').Count | Should -Be 1
        $pruneWorkflow | Should -Not -Match 'id-token:'
        $pruneWorkflow | Should -Not -Match 'secrets\.'
    }

    It 'runs one prune at a time without cancelling one under way' {
        $pruneWorkflow | Should -Match '(?m)^concurrency:\r?\n  group: cohesion-ci-packages-prune\r?\n  cancel-in-progress: false\r?$'
    }

    It 'prunes the release inventory with the documented retention' {
        $pruneJob | Should -Match 'Import-Module \./installer/scripts/modules/CohesionPackaging\.psm1 -Force'
        $pruneJob | Should -Match 'Import-Module \./installer/scripts/modules/CohesionPackageFeed\.psm1 -Force'
        $pruneJob | Should -Match 'PackageName    = Get-CohesionReleasePackageId'
        $pruneJob | Should -Match 'MainBuildCount = 20'
        $pruneJob | Should -Match 'Invoke-CohesionCiPackagePrune @arguments'
        $pruneJob | Should -Match 'GITHUB_PACKAGES_TOKEN: \$\{\{ github\.token \}\}'
    }

    It 'pins every action to a commit SHA with a trailing version comment' {
        $uses = @([System.Text.RegularExpressions.Regex]::Matches($pruneWorkflow, '(?m)^\s+(?:- )?uses: .+$') | ForEach-Object Value)
        $uses.Count | Should -BeGreaterThan 0
        foreach ($line in $uses) {
            $line | Should -Match 'uses: [^@\s]+@[0-9a-f]{40} # v[0-9.]+\r?$'
        }
    }
}
