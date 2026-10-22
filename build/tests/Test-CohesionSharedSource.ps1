param(
    [string] $MSBuildPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$taskRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$taskFixture = Join-Path $taskTemporaryRoot ('cohesion-shared-source-' + [Guid]::NewGuid().ToString('N'))
try {
    $taskOwner = Join-Path $taskFixture 'libraries/Test/ModuleOwner'
    New-Item -ItemType Directory -Path (Join-Path $taskOwner 'src'), (Join-Path $taskOwner 'shared/A/Nested'), (Join-Path $taskOwner 'shared/B') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $taskOwner 'src/ModuleOwner.csproj') -Value '<Project />'
    Set-Content -LiteralPath (Join-Path $taskOwner 'shared/A/First.cs') -Value 'internal class First {}'
    Set-Content -LiteralPath (Join-Path $taskOwner 'shared/A/Nested/Second.cs') -Value 'internal class Second {}'
    Set-Content -LiteralPath (Join-Path $taskOwner 'shared/B/Third.cs') -Value 'internal class Third {}'
    Set-Content -LiteralPath (Join-Path $taskOwner 'shared/Excluded.cs') -Value 'This root file must never be compiled.'

    $taskCases = @(
        @{ Name = 'module-a'; Items = '<CohesionSharedSource Include="ModuleOwner" Module="A" />'; Files = @('First.cs', 'Second.cs'); Code = '' },
        @{ Name = 'module-b'; Items = '<CohesionSharedSource Include="ModuleOwner" Module="B" />'; Files = @('Third.cs'); Code = '' },
        @{ Name = 'two-modules'; Items = '<CohesionSharedSource Include="ModuleOwner" Module="A" /><CohesionSharedSource Include="ModuleOwner" Module="B" />'; Files = @('First.cs', 'Second.cs', 'Third.cs'); Code = '' },
        @{ Name = 'duplicate-module'; Items = '<CohesionSharedSource Include="ModuleOwner" Module="A" /><CohesionSharedSource Include="ModuleOwner" Module="A" />'; Files = @('First.cs', 'Second.cs'); Code = '' },
        @{ Name = 'no-items'; Items = ''; Files = @(); Code = '' },
        @{ Name = 'missing-module'; Items = '<CohesionSharedSource Include="ModuleOwner" />'; Code = 'COHSHR003' },
        @{ Name = 'unknown-module'; Items = '<CohesionSharedSource Include="ModuleOwner" Module="Unknown" />'; Code = 'COHSHR004' },
        @{ Name = 'unknown-project'; Items = '<CohesionSharedSource Include="Unknown" Module="A" />'; Code = 'COHSHR001' },
        @{ Name = 'traversal'; Items = '<CohesionSharedSource Include="ModuleOwner" Module="../A" />'; Code = 'COHSHR003' },
        @{ Name = 'wildcard'; Items = '<CohesionSharedSource Include="ModuleOwner" Module="*" />'; Code = 'COHSHR003' }
    )
    foreach ($taskCase in $taskCases) {
        $taskResultPath = Join-Path $taskFixture ($taskCase.Name + '.txt')
        $taskProjectPath = Join-Path $taskFixture ($taskCase.Name + '.proj')
        $taskTargets = [Security.SecurityElement]::Escape((Join-Path $taskRepository 'build/Targets/Build.SharedFiles.targets'))
        $taskRoot = [Security.SecurityElement]::Escape($taskFixture + [IO.Path]::DirectorySeparatorChar)
        Set-Content -LiteralPath $taskProjectPath -Value @"
<Project>
  <PropertyGroup><CohesionRepositoryDirectory>$taskRoot</CohesionRepositoryDirectory></PropertyGroup>
  <ItemGroup>$($taskCase.Items)</ItemGroup>
  <Import Project="$taskTargets" />
  <Target Name="Verify" DependsOnTargets="ResolveCohesionSharedSource">
    <WriteLinesToFile File="$taskResultPath" Lines="@(Compile->'%(FullPath)|%(Link)')" Overwrite="true" />
  </Target>
</Project>
"@
        $taskArguments = @($taskProjectPath, '-t:Verify', '-nologo', '-verbosity:quiet')
        if ([IO.Path]::GetFileNameWithoutExtension($MSBuildPath) -eq 'dotnet') { $taskArguments = @('msbuild') + $taskArguments }
        $taskOutput = (& $MSBuildPath @taskArguments 2>&1 | Out-String)
        if ($taskCase.Code) {
            if ($LASTEXITCODE -eq 0 -or !$taskOutput.Contains($taskCase.Code)) { throw "$($taskCase.Name) did not fail with $($taskCase.Code): $taskOutput" }
        } else {
            if ($LASTEXITCODE -ne 0) { throw "$($taskCase.Name) failed: $taskOutput" }
            $taskLines = if (Test-Path -LiteralPath $taskResultPath) { @(Get-Content -LiteralPath $taskResultPath) } else { @() }
            $taskNames = @($taskLines | ForEach-Object { [IO.Path]::GetFileName(($_ -split '\|')[0]) } | Sort-Object)
            if (($taskNames -join '|') -ne (($taskCase.Files | Sort-Object) -join '|')) { throw "$($taskCase.Name) included unexpected files: $taskLines" }
            foreach ($taskLine in $taskLines) {
                if (($taskLine -split '\|')[1] -notmatch '^shared[\\/][AB][\\/]') { throw "Missing module in Link: $taskLine" }
            }
        }
        Write-Output "PASS $($taskCase.Name)"
    }
} finally {
    $taskResolvedFixture = [IO.Path]::GetFullPath($taskFixture)
    if (!$taskResolvedFixture.StartsWith($taskTemporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($taskResolvedFixture) -notlike 'cohesion-shared-source-*') { throw 'Refusing cleanup outside the temporary fixture directory.' }
    if (Test-Path -LiteralPath $taskResolvedFixture) { Remove-Item -LiteralPath $taskResolvedFixture -Recurse -Force }
}
