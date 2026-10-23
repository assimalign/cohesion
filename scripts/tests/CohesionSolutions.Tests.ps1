#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    $updater = Join-Path $PSScriptRoot '../Update-CohesionSolutions.ps1'
    $pwsh = (Get-Process -Id $PID).Path
}

Describe 'Repository solution maintenance' {
    BeforeEach {
        $repository = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        foreach ($directory in @('libraries/A/src', 'libraries/B/src', 'fixtures/SdkSmoke',
                'tooling/Templates/content', '_out/copied', 'libraries/A/obj/stale')) {
            New-Item -ItemType Directory -Path (Join-Path $repository $directory) -Force | Out-Null
        }
        Set-Content (Join-Path $repository 'libraries/A/src/A.csproj') '<Project><ItemGroup><CohesionProjectReference Include="B" /></ItemGroup></Project>'
        Set-Content (Join-Path $repository 'libraries/B/src/B.csproj') '<Project><ItemGroup><ProjectReference Include="../../A/src/A.csproj" /></ItemGroup></Project>'
        foreach ($directory in @('fixtures/SdkSmoke', 'tooling/Templates/content', '_out/copied', 'libraries/A/obj/stale')) {
            Set-Content (Join-Path $repository "$directory/Excluded.csproj") '<Project />'
        }
        $solution = Join-Path $repository 'libraries/A/A.slnx'
        Set-Content $solution '<Solution><Folder Name="/Custom/"><Project Path="src/A.csproj" /><Project Path="gone.csproj" /><File Path="notes.txt" /></Folder></Solution>'
        Set-Content (Join-Path $repository 'libraries/A/notes.txt') 'keep this solution item'
    }

    It 'preserves custom groups and resolves a dependency cycle across areas' {
        & $updater -RepositoryRoot $repository -SolutionPath 'libraries/A/A.slnx'
        [xml]$result = Get-Content $solution -Raw
        $result.SelectSingleNode('/Solution/Folder[@Name="/Custom/"]/Project').Path | Should -Be 'src/A.csproj'
        $result.SelectSingleNode('/Solution/Folder[@Name="/Custom/"]/File').Path | Should -Be 'notes.txt'
        @($result.SelectNodes('/Solution//Project')).Count | Should -Be 2
        @($result.SelectNodes('/Solution//Project') | ForEach-Object Path) | Should -Contain '../B/src/B.csproj'
    }

    It 'skips package consumers, template content, and build output' {
        $rootSolution = Join-Path $repository 'Root.slnx'
        Set-Content $rootSolution '<Solution />'
        & $updater -RepositoryRoot $repository -SolutionPath $rootSolution
        [xml]$result = Get-Content $rootSolution -Raw
        @($result.SelectNodes('/Solution//Project')).Count | Should -Be 2
        Get-Content $rootSolution -Raw | Should -Not -Match 'Excluded'
    }

    It 'is byte-for-byte stable on a second run' {
        & $updater -RepositoryRoot $repository -SolutionPath $solution
        $first = [IO.File]::ReadAllText($solution)
        & $updater -RepositoryRoot $repository -SolutionPath $solution
        [IO.File]::ReadAllText($solution) | Should -BeExactly $first
    }

    It 'reports drift with Check and leaves the solution untouched' {
        $before = [IO.File]::ReadAllText($solution)
        & $pwsh -NoProfile -File $updater -RepositoryRoot $repository -SolutionPath $solution -Check
        $LASTEXITCODE | Should -Be 1
        [IO.File]::ReadAllText($solution) | Should -BeExactly $before
    }

    It 'supports a non-mutating WhatIf preview' {
        $before = [IO.File]::ReadAllText($solution)
        & $updater -RepositoryRoot $repository -SolutionPath $solution -WhatIf
        [IO.File]::ReadAllText($solution) | Should -BeExactly $before
    }
}
