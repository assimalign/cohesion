#Requires -Version 5.1
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    $repositoryDirectory = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
    $localPackagingModule = Join-Path $PSScriptRoot '../CohesionLocalPackaging.psm1'
    Import-Module $localPackagingModule -Force

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
}

Describe 'Cohesion local package versions' {
    It 'appends the reserved local identifier to a canonical prerelease' {
        $version = Get-CohesionLocalPackageVersion -Version '10.0.1-preview.3'

        $version.Version | Should -Be '10.0.1-preview.3.local'
        $version.VersionPrefix | Should -Be '10.0.1'
        $version.VersionSuffix | Should -Be 'preview.3.local'
        $version.PatchVersion | Should -Be '1-preview.3.local'
    }

    It 'rejects a stable canonical version until the post-tag bump lands' {
        { Get-CohesionLocalPackageVersion -Version '10.0.1' } |
            Should -Throw -ExpectedMessage '*post-tag bump*'
    }

    It 'rejects the local identifier on the canonical version line' {
        { Get-CohesionLocalPackageVersion -Version '10.0.1-preview.3.local' } |
            Should -Throw -ExpectedMessage '*reserved local identifier*'
    }

    It 'rejects a leading-zero numeric prerelease identifier' {
        { Get-CohesionLocalPackageVersion -Version '10.0.1-preview.03' } |
            Should -Throw -ExpectedMessage '*leading zero*'
    }

    It 'rejects a local-only identity in the release packer' {
        $releasePacker = Join-Path $repositoryDirectory 'installer/scripts/Pack-Release.ps1'

        { & $releasePacker -Version '10.0.1-preview.3.local' } |
            Should -Throw -ExpectedMessage '*cannot be release-packed*'
    }
}

Describe 'Cohesion local library package pruning' {
    It 'selects exact library package artifacts without including prefix-sharing or unrelated files' {
        $packageDirectory = Join-Path $TestDrive 'packages'
        $null = New-Item -Path $packageDirectory -ItemType Directory
        $fileName = @(
            'Assimalign.Cohesion.Core.10.0.1-preview.2.nupkg'
            'Assimalign.Cohesion.Core.10.0.1-preview.3.local.nupkg'
            'Assimalign.Cohesion.Http.10.0.1-preview.3.local.snupkg'
            'Assimalign.Cohesion.Http.10.0.1-preview.3.local.symbols.nupkg'
            'Assimalign.Cohesion.Core.Extensions.10.0.1-preview.2.nupkg'
            'Assimalign.Cohesion.Sdk.10.0.1-preview.2.nupkg'
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
            'Assimalign.Cohesion.Core.10.0.1-preview.2.nupkg'
            'Assimalign.Cohesion.Core.10.0.1-preview.3.local.nupkg'
            'Assimalign.Cohesion.Http.10.0.1-preview.3.local.snupkg'
            'Assimalign.Cohesion.Http.10.0.1-preview.3.local.symbols.nupkg'
        )
        (Test-Path -LiteralPath (Join-Path $packageDirectory 'Assimalign.Cohesion.Core.Extensions.10.0.1-preview.2.nupkg')) | Should -BeTrue
        (Test-Path -LiteralPath (Join-Path $packageDirectory 'Assimalign.Cohesion.Sdk.10.0.1-preview.2.nupkg')) | Should -BeTrue
        (Test-Path -LiteralPath (Join-Path $packageDirectory 'Assimalign.Cohesion.Core.notes.nupkg')) | Should -BeTrue
        (Test-Path -LiteralPath (Join-Path $packageDirectory 'README.txt')) | Should -BeTrue
    }
}

Describe 'Cohesion release policy wiring' {
    BeforeAll {
        $releaseWorkflow = Get-Content -LiteralPath (Join-Path $repositoryDirectory '.github/workflows/release.yml') -Raw
        $installLocal = Get-Content -LiteralPath (Join-Path $repositoryDirectory 'installer/scripts/Install-Local.ps1') -Raw
    }

    It 'keeps the signed-off canonical version line' {
        $version = & (Join-Path $repositoryDirectory 'installer/scripts/Get-CohesionVersion.ps1') `
            -RepoRoot $repositoryDirectory

        $version | Should -Be '10.0.1-preview.3'
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
        $stagingJob | Should -Match '(?m)^    needs: pack-packages\r?$'
        (Get-WorkflowJobBody -Workflow $releaseWorkflow -Job 'prepare:') |
            Should -Not -Match 'inputs\.promote'
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
        $installLocal | Should -Match 'Get-CohesionLocalPackageVersion'
        $installLocal | Should -Match 'Get-CohesionStaleLibraryPackage'
        $installLocal | Should -Match '(?ms)foreach \(\$packagePath in \$staleLibraryPackage\).*?Remove-Item -LiteralPath \$packagePath -Force'
        $installLocal | Should -Match 'Get-CohesionReleaseLibrary -RepositoryDirectory \$repoRoot'
        $installLocal | Should -Match 'Join-Path \(Join-Path \$globalPackagesRoot \$pkg\) \$cohesionVersion'
        $releaseWorkflow | Should -Match '-RepositoryCommit \$env:COHESION_RELEASE_COMMIT'
    }
}
