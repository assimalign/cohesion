#Requires -Version 5.1
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    $modulePath = Join-Path $PSScriptRoot '..\CohesionPackaging.psm1'
    Import-Module $modulePath -Force

    function Initialize-CohesionTestRepository {
        param(
            [Parameter(Mandatory)]
            [string] $Path
        )

        foreach ($directory in @(
                '.github/workflows',
                'libraries',
                'resources',
                'sdks',
                'analyzers',
                'tooling',
                'extensions')) {
            $null = New-Item -Path (Join-Path $Path $directory) -ItemType Directory -Force
        }
    }

    function Add-CohesionTestProject {
        param(
            [Parameter(Mandatory)]
            [string] $RepositoryDirectory,

            [Parameter(Mandatory)]
            [string] $Root,

            [Parameter(Mandatory)]
            [string] $Name,

            [ValidateSet('Default', 'False', 'True', 'Test')]
            [string] $Packability = 'Default',

            [ValidateSet('Project', 'None', 'Obj', 'Bin')]
            [string] $Source = 'Project'
        )

        $relativeDirectory = "$Root/Test/$Name/src"
        $projectDirectory = Join-Path $RepositoryDirectory ($relativeDirectory -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        $null = New-Item -Path $projectDirectory -ItemType Directory -Force

        $property = switch ($Packability) {
            'False' { '<IsPackable>false</IsPackable>' }
            'True' { '<IsPackable>true</IsPackable>' }
            default { '' }
        }
        $testItem = if ($Packability -eq 'Test') {
            '<ItemGroup><CohesionPackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup>'
        }
        else {
            ''
        }
        $projectContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>$property</PropertyGroup>
  $testItem
</Project>
"@
        $projectPath = Join-Path $projectDirectory "$Name.csproj"
        Set-Content -LiteralPath $projectPath -Value $projectContent -NoNewline

        if ($Source -eq 'Project') {
            Set-Content -LiteralPath (Join-Path $projectDirectory 'Class1.cs') -Value 'internal class Class1 { }' -NoNewline
        }
        elseif ($Source -in @('Obj', 'Bin')) {
            $generatedDirectory = Join-Path $projectDirectory $Source.ToLowerInvariant()
            $null = New-Item -Path $generatedDirectory -ItemType Directory -Force
            Set-Content -LiteralPath (Join-Path $generatedDirectory 'Generated.cs') -Value 'internal class Generated { }' -NoNewline
        }

        return "$relativeDirectory/$Name.csproj"
    }

    function Write-CohesionTestMatrix {
        param(
            [Parameter(Mandatory)]
            [string] $RepositoryDirectory,

            [Parameter(Mandatory)]
            [string[]] $Project,

            [string] $WorkflowName = 'release.yml'
        )

        $quotedProject = @($Project | ForEach-Object { "`"$_`"" }) -join ', '
        $workflow = @"
name: Test matrix
jobs:
  build:
    strategy:
      matrix:
        projects: [$quotedProject]
    steps:
      - name: Build
        run: dotnet build
"@
        Set-Content `
            -LiteralPath (Join-Path $RepositoryDirectory ".github/workflows/$WorkflowName") `
            -Value $workflow `
            -NoNewline
    }

    function Write-CohesionTestAreaMatrix {
        param(
            [Parameter(Mandatory)]
            [string] $RepositoryDirectory,

            [Parameter(Mandatory)]
            [string] $Project
        )

        $workflow = @"
name: Test area matrix
jobs:
  build:
    strategy:
      matrix:
        projects: ["$Project"]
    steps:
      - uses: ./.github/actions/build
        with:
          area: "libraries"
          category: "Test"
          project: "`${{ matrix.projects }}"
"@
        Set-Content `
            -LiteralPath (Join-Path $RepositoryDirectory '.github/workflows/library-test.yml') `
            -Value $workflow `
            -NoNewline
    }

    function Write-CohesionTestReleaseLibrary {
        param(
            [Parameter(Mandatory)]
            [string] $Entry
        )

        InModuleScope CohesionPackaging -Parameters @{ Entry = $Entry } {
            param($Entry)
            $script:CohesionReleaseLibrary = @($Entry)
        }
    }

    function Initialize-CohesionTestInventory {
        InModuleScope CohesionPackaging {
            $script:CohesionReleaseLibrary = @()
            $script:CohesionReleaseSdk = @()
            $script:CohesionReleaseFramework = @()
            $script:CohesionReleaseRuntimeIdentifier = @()
            $script:CohesionReleaseSourcelessPackage = @()
            $script:CohesionReleaseUnpublishedDependency = @()
            $script:CohesionCiMatrixExclusion = [ordered]@{}
        }
    }
}

AfterAll {
    Remove-Module CohesionPackaging -Force -ErrorAction SilentlyContinue
}

Describe 'CohesionPackaging workflow classification' {
    It 'uses the signed-off non-matrix workflow set in order' {
        InModuleScope CohesionPackaging {
            @($script:CohesionNonMatrixWorkflow) | Should -Be @(
                'release.yml',
                'release-inventory.yml',
                'analyzers.yml',
                'sdk-smoke.yml',
                'credential-guard.yml'
            )
        }
    }

    It 'tolerates listed non-matrix workflows that are absent' {
        $repository = Join-Path $TestDrive 'missing-workflows'
        Initialize-CohesionTestRepository -Path $repository
        Initialize-CohesionTestInventory

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } | Should -Not -Throw
    }

    It 'does not exempt framework.yml from matrix parsing' {
        $repository = Join-Path $TestDrive 'framework-workflow'
        Initialize-CohesionTestRepository -Path $repository
        Initialize-CohesionTestInventory
        Set-Content -LiteralPath (Join-Path $repository '.github/workflows/framework.yml') -Value 'name: Framework' -NoNewline

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage '*framework.yml*parseable area/category/projects matrix*'
    }
}

Describe 'CohesionPackaging packable source-bearing blind-spot guard' {
    BeforeEach {
        $repository = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        Initialize-CohesionTestRepository -Path $repository
        Initialize-CohesionTestInventory
    }

    It 'reports an unmatrixed project under every guarded root' {
        $projectPath = @{}
        foreach ($root in @('libraries', 'resources', 'sdks', 'analyzers', 'tooling', 'extensions')) {
            $name = "Test.$root"
            $projectPath[$root] = Add-CohesionTestProject `
                -RepositoryDirectory $repository `
                -Root $root `
                -Name $name
        }

        $message = $null
        try {
            Assert-CohesionReleaseInventory -RepositoryDirectory $repository
        }
        catch {
            $message = $_.Exception.Message
        }

        $message | Should -Not -BeNullOrEmpty
        foreach ($relativePath in $projectPath.Values) {
            $message | Should -Match ([regex]::Escape($relativePath))
        }
    }

    It 'accepts a conventional project named by a non-matrix static workflow' {
        $name = 'Test.MatrixCovered'
        $null = Add-CohesionTestProject -RepositoryDirectory $repository -Root 'libraries' -Name $name
        Write-CohesionTestMatrix `
            -RepositoryDirectory $repository `
            -Project $name `
            -WorkflowName 'analyzers.yml'

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } | Should -Not -Throw
    }

    It 'does not let one matrix name cover two same-named projects' {
        $name = 'Test.DuplicateName'
        $firstPath = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'tooling' `
            -Name $name
        $secondPath = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'extensions' `
            -Name $name
        Write-CohesionTestMatrix -RepositoryDirectory $repository -Project $name

        $message = $null
        try {
            Assert-CohesionReleaseInventory -RepositoryDirectory $repository
        }
        catch {
            $message = $_.Exception.Message
        }

        $message | Should -Match ([regex]::Escape($firstPath))
        $message | Should -Match ([regex]::Escape($secondPath))
    }

    It 'does not treat a commented matrix fragment as coverage' {
        $name = 'Test.CommentedMatrix'
        $relativePath = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'tooling' `
            -Name $name
        Set-Content `
            -LiteralPath (Join-Path $repository '.github/workflows/release.yml') `
            -Value "name: Comments only`n# projects: [`"$name`"]" `
            -NoNewline

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*$relativePath*"
    }

    It 'does not treat a commented entry inside a matrix as coverage' {
        $name = 'Test.CommentedMatrixEntry'
        $relativePath = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'tooling' `
            -Name $name
        Set-Content `
            -LiteralPath (Join-Path $repository '.github/workflows/release.yml') `
            -NoNewline `
            -Value @"
name: Matrix with a commented entry
jobs:
  build:
    strategy:
      matrix:
        projects: [
          # "$name",
          "Test.RealEntry"
        ]
"@

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*$relativePath*"
    }

    It 'does not treat a projects key outside strategy.matrix as coverage' {
        $name = 'Test.EnvironmentProjects'
        $relativePath = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'tooling' `
            -Name $name
        Set-Content `
            -LiteralPath (Join-Path $repository '.github/workflows/release.yml') `
            -Value "name: Environment value`nenv:`n  projects: [`"$name`"]" `
            -NoNewline

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*$relativePath*"
    }

    It 'does not treat matrix-shaped text in a block scalar as coverage' {
        $name = 'Test.ScriptText'
        $relativePath = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'tooling' `
            -Name $name
        Set-Content `
            -LiteralPath (Join-Path $repository '.github/workflows/release.yml') `
            -NoNewline `
            -Value @"
name: Script text
jobs:
  build:
    strategy:
      matrix:
        commands:
          - |
              strategy:
                matrix:
                  projects: ["$name"]
    steps:
      - run: |
          strategy:
            matrix:
              projects: ["$name"]
      - "run": &script |
          strategy:
            matrix:
              projects: ["$name"]
"@

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*$relativePath*"
    }

    It 'accepts an exact exclusion with a non-empty reason' {
        $relativePath = Add-CohesionTestProject -RepositoryDirectory $repository -Root 'tooling' -Name 'Test.Excluded'
        InModuleScope CohesionPackaging -Parameters @{ RelativePath = $relativePath } {
            param($RelativePath)
            $script:CohesionCiMatrixExclusion[$RelativePath] = 'Fixture exercises an intentional CI exclusion.'
        }

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } | Should -Not -Throw
    }

    It 'rejects an exclusion with a blank reason' {
        $relativePath = Add-CohesionTestProject -RepositoryDirectory $repository -Root 'tooling' -Name 'Test.BlankReason'
        InModuleScope CohesionPackaging -Parameters @{ RelativePath = $relativePath } {
            param($RelativePath)
            $script:CohesionCiMatrixExclusion[$RelativePath] = '   '
        }

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage '*has no reason*'
    }

    It 'rejects an exclusion after the project enters a matrix' {
        $name = 'Test.StaleExclusion'
        $relativePath = Add-CohesionTestProject -RepositoryDirectory $repository -Root 'tooling' -Name $name
        Write-CohesionTestMatrix -RepositoryDirectory $repository -Project $name
        InModuleScope CohesionPackaging -Parameters @{ RelativePath = $relativePath } {
            param($RelativePath)
            $script:CohesionCiMatrixExclusion[$RelativePath] = 'Fixture begins outside CI.'
        }

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage '*project now appears in a workflow matrix*'
    }

    It 'ignores explicitly non-packable, test, and source-less projects' {
        Set-Content `
            -LiteralPath (Join-Path $repository 'analyzers/Directory.Build.props') `
            -Value '<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>' `
            -NoNewline
        $null = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'analyzers' `
            -Name 'Test.InheritedNonPackable'
        $null = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'libraries' `
            -Name 'Test.NonPackable' `
            -Packability 'False'
        $null = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'resources' `
            -Name 'Test.TestProject' `
            -Packability 'Test'
        $null = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'sdks' `
            -Name 'Test.SourceLess' `
            -Source 'None'
        $null = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'extensions' `
            -Name 'Test.ObjOnly' `
            -Source 'Obj'
        $null = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'tooling' `
            -Name 'Test.BinOnly' `
            -Source 'Bin'

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } | Should -Not -Throw
    }

    It 'treats conditional or commented false metadata as potentially packable' {
        $name = 'Test.ConditionalPackability'
        $relativePath = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'tooling' `
            -Name $name
        $projectPath = Join-Path $repository ($relativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        Set-Content -LiteralPath $projectPath -NoNewline -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <!-- <IsPackable>false</IsPackable> -->
  <PropertyGroup>
    <IsPackable Condition="'$(Configuration)' == 'Debug'">false</IsPackable>
  </PropertyGroup>
  <Choose>
    <When Condition="'$(TargetFramework)' == 'net10.0'">
      <PropertyGroup>
        <IsPackable>false</IsPackable>
      </PropertyGroup>
    </When>
  </Choose>
</Project>
'@

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*$relativePath*"
    }

    It 'ignores IsPackable metadata outside evaluated project property groups' {
        $name = 'Test.UnrelatedPackability'
        $relativePath = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'tooling' `
            -Name $name
        $projectPath = Join-Path $repository ($relativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        Set-Content -LiteralPath $projectPath -NoNewline -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Widget Include="example">
      <IsPackable>false</IsPackable>
    </Widget>
  </ItemGroup>
  <Target Name="SetTargetProperty">
    <PropertyGroup>
      <IsPackable>false</IsPackable>
    </PropertyGroup>
  </Target>
</Project>
'@

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*$relativePath*"
    }

    It 'recognizes explicit SDK imports as packable SDK-style projects' {
        $name = 'Test.ExplicitSdkImport'
        $relativePath = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'tooling' `
            -Name $name
        $projectPath = Join-Path $repository ($relativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        Set-Content -LiteralPath $projectPath -NoNewline -Value @'
<Project>
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
</Project>
'@

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*$relativePath*"
    }

    It 'honors a nearest props boundary that does not import its parent' {
        Set-Content `
            -LiteralPath (Join-Path $repository 'Directory.Build.props') `
            -Value '<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>' `
            -NoNewline
        $name = 'Test.ShadowedProps'
        $relativePath = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'tooling' `
            -Name $name
        $projectDirectory = Split-Path -Parent (
            (Join-Path $repository ($relativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)))
        Set-Content -LiteralPath (Join-Path $projectDirectory 'Directory.Build.props') -Value '<Project />' -NoNewline

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*$relativePath*"
    }

    It 'honors a project-level packable override' {
        Set-Content `
            -LiteralPath (Join-Path $repository 'Directory.Build.props') `
            -Value '<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>' `
            -NoNewline
        $relativePath = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'extensions' `
            -Name 'Test.PackableOverride' `
            -Packability 'True'

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*$relativePath*"
    }

    It 'fails when a formerly source-less project gains its first source file' {
        $name = 'Test.FirstSource'
        $relativePath = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'extensions' `
            -Name $name `
            -Source 'None'

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } | Should -Not -Throw

        $projectPath = Join-Path $repository ($relativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        Set-Content -LiteralPath (Join-Path (Split-Path -Parent $projectPath) 'Class1.cs') `
            -Value 'internal class Class1 { }' `
            -NoNewline

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*$relativePath*"
    }
}

Describe 'CohesionPackaging inventory and area-matrix equality' {
    BeforeEach {
        $repository = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        Initialize-CohesionTestRepository -Path $repository
        Initialize-CohesionTestInventory
        $projectName = 'Test.Equality'
        $null = Add-CohesionTestProject `
            -RepositoryDirectory $repository `
            -Root 'libraries' `
            -Name $projectName
    }

    It 'accepts an aligned inventory and per-area matrix' {
        Write-CohesionTestReleaseLibrary -Entry "libraries/Test/$projectName"
        Write-CohesionTestAreaMatrix -RepositoryDirectory $repository -Project $projectName

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } | Should -Not -Throw
    }

    It 'rejects an inventory project absent from per-area CI' {
        Write-CohesionTestReleaseLibrary -Entry "libraries/Test/$projectName"

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage '*no per-area CI workflow builds or tests it*'
    }

    It 'rejects a packable per-area CI project absent from the inventory' {
        Write-CohesionTestAreaMatrix -RepositoryDirectory $repository -Project $projectName

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage '*release inventory does not ship it*'
    }
}

Describe 'CohesionPackaging framework producer convention' {
    BeforeAll {
        function Add-CohesionTestFrameworkProducer {
            param(
                [Parameter(Mandatory)]
                [string] $RepositoryDirectory,

                [Parameter(Mandatory)]
                [string] $RelativePath,

                [Parameter(Mandatory)]
                [string] $Framework,

                [Parameter(Mandatory)]
                [ValidateSet('Ref', 'Runtime')]
                [string] $Kind,

                [string] $AssemblyName
            )

            $projectPath = Join-Path $RepositoryDirectory ($RelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
            $null = New-Item -Path (Split-Path -Parent $projectPath) -ItemType Directory -Force
            $assemblyNameProperty = if ($AssemblyName) { "<AssemblyName>$AssemblyName</AssemblyName>" } else { '' }
            Set-Content -LiteralPath $projectPath -NoNewline -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    $assemblyNameProperty
    <IsPackable>true</IsPackable>
    <CohesionFrameworkName>$Framework</CohesionFrameworkName>
    <CohesionFrameworkKind>$Kind</CohesionFrameworkKind>
  </PropertyGroup>
</Project>
"@
        }

        function Add-CohesionTestFrameworkFamily {
            param(
                [Parameter(Mandatory)]
                [string] $RepositoryDirectory,

                [Parameter(Mandatory)]
                [string] $Framework
            )

            Add-CohesionTestFrameworkProducer `
                -RepositoryDirectory $RepositoryDirectory `
                -RelativePath (Get-CohesionFrameworkProjectPath -Framework $Framework -Kind Refs) `
                -Framework $Framework `
                -Kind Ref `
                -AssemblyName "$Framework.Refs"
            Add-CohesionTestFrameworkProducer `
                -RepositoryDirectory $RepositoryDirectory `
                -RelativePath (Get-CohesionFrameworkProjectPath -Framework $Framework -Kind Runtime) `
                -Framework $Framework `
                -Kind Runtime `
                -AssemblyName $Framework
        }

        function Set-CohesionTestReleaseFramework {
            param(
                [string[]] $Framework
            )

            InModuleScope CohesionPackaging -Parameters @{ Framework = $Framework } {
                param($Framework)
                $script:CohesionReleaseFramework = @($Framework)
            }
        }
    }

    BeforeEach {
        $repository = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        Initialize-CohesionTestRepository -Path $repository
        Initialize-CohesionTestInventory
    }

    It 'maps <Framework> <Kind> to <Expected>' -TestCases @(
        @{ Framework = 'Assimalign.Cohesion.App'; Kind = 'Refs'; Expected = 'libraries/App/Assimalign.Cohesion.App.Refs/src/Assimalign.Cohesion.App.Refs.csproj' }
        @{ Framework = 'Assimalign.Cohesion.App'; Kind = 'Runtime'; Expected = 'libraries/App/Assimalign.Cohesion.App.Runtime/src/Assimalign.Cohesion.App.Runtime.csproj' }
        @{ Framework = 'Assimalign.Cohesion.App.Web'; Kind = 'Refs'; Expected = 'resources/Web/Assimalign.Cohesion.Web.Refs/src/Assimalign.Cohesion.Web.Refs.csproj' }
        @{ Framework = 'Assimalign.Cohesion.App.Web'; Kind = 'Runtime'; Expected = 'resources/Web/Assimalign.Cohesion.Web.Runtime/src/Assimalign.Cohesion.Web.Runtime.csproj' }
    ) {
        Get-CohesionFrameworkProjectPath -Framework $Framework -Kind $Kind | Should -BeExactly $Expected
    }

    It 'rejects <Framework> as a framework family name' -TestCases @(
        @{ Framework = 'Assimalign.Cohesion.Web' }
        @{ Framework = 'Assimalign.Cohesion.App.' }
        @{ Framework = 'Assimalign.Cohesion.App.Web.Hosting' }
    ) {
        { Get-CohesionFrameworkProjectPath -Framework $Framework -Kind Runtime } |
            Should -Throw -ExpectedMessage '*is not a shared-framework family name*'
    }

    It 'accepts App and area producers at their conventional paths' {
        Set-CohesionTestReleaseFramework -Framework 'Assimalign.Cohesion.App', 'Assimalign.Cohesion.App.Test'
        Add-CohesionTestFrameworkFamily -RepositoryDirectory $repository -Framework 'Assimalign.Cohesion.App'
        Add-CohesionTestFrameworkFamily -RepositoryDirectory $repository -Framework 'Assimalign.Cohesion.App.Test'

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } | Should -Not -Throw
    }

    It 'rejects a producer named for its framework instead of its area' {
        Set-CohesionTestReleaseFramework -Framework 'Assimalign.Cohesion.App.Test'
        Add-CohesionTestFrameworkFamily -RepositoryDirectory $repository -Framework 'Assimalign.Cohesion.App.Test'
        Add-CohesionTestFrameworkProducer `
            -RepositoryDirectory $repository `
            -RelativePath 'resources/Test/Assimalign.Cohesion.App.Test.Runtime/src/Assimalign.Cohesion.App.Test.Runtime.csproj' `
            -Framework 'Assimalign.Cohesion.App.Test' `
            -Kind Runtime `
            -AssemblyName 'Assimalign.Cohesion.App.Test'

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*'resources/Test/Assimalign.Cohesion.App.Test.Runtime/src/Assimalign.Cohesion.App.Test.Runtime.csproj'*must be 'resources/Test/Assimalign.Cohesion.Test.Runtime/src/Assimalign.Cohesion.Test.Runtime.csproj'*"
    }

    It 'rejects a producer left in the retired frameworks folder' {
        Set-CohesionTestReleaseFramework -Framework 'Assimalign.Cohesion.App.Test'
        Add-CohesionTestFrameworkFamily -RepositoryDirectory $repository -Framework 'Assimalign.Cohesion.App.Test'
        Add-CohesionTestFrameworkProducer `
            -RepositoryDirectory $repository `
            -RelativePath 'frameworks/Assimalign.Cohesion.App.Test.Refs/src/Assimalign.Cohesion.App.Test.Refs.csproj' `
            -Framework 'Assimalign.Cohesion.App.Test' `
            -Kind Ref `
            -AssemblyName 'Assimalign.Cohesion.App.Test.Refs'

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*'frameworks/Assimalign.Cohesion.App.Test.Refs/src/Assimalign.Cohesion.App.Test.Refs.csproj'*must be*"
    }

    It 'rejects a Refs producer whose assembly name follows the project instead of the framework' {
        Set-CohesionTestReleaseFramework -Framework 'Assimalign.Cohesion.App.Test'
        Add-CohesionTestFrameworkProducer `
            -RepositoryDirectory $repository `
            -RelativePath (Get-CohesionFrameworkProjectPath -Framework 'Assimalign.Cohesion.App.Test' -Kind Refs) `
            -Framework 'Assimalign.Cohesion.App.Test' `
            -Kind Ref
        Add-CohesionTestFrameworkProducer `
            -RepositoryDirectory $repository `
            -RelativePath (Get-CohesionFrameworkProjectPath -Framework 'Assimalign.Cohesion.App.Test' -Kind Runtime) `
            -Framework 'Assimalign.Cohesion.App.Test' `
            -Kind Runtime `
            -AssemblyName 'Assimalign.Cohesion.App.Test'

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage '*must declare <AssemblyName>Assimalign.Cohesion.App.Test.Refs</AssemblyName>*'
    }

    It 'rejects a producer whose family the release does not ship' {
        Add-CohesionTestFrameworkFamily -RepositoryDirectory $repository -Framework 'Assimalign.Cohesion.App.Test'

        { Assert-CohesionReleaseInventory -RepositoryDirectory $repository } |
            Should -Throw -ExpectedMessage "*belongs to family 'Assimalign.Cohesion.App.Test', which the release inventory does not ship*"
    }
}
