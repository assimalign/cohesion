#Requires -Version 5.1
<#
.SYNOPSIS
    Scaffolds a Cohesion domain (SDK + Framework family) for one or more
    resource categories under the resources/ folder.

.DESCRIPTION
    For each resource name (e.g. "Scheduler", "IdentityHub", ...), creates
    seven files following the existing Sdk.Web / Sdk.Database conventions:

        sdks/Assimalign.Cohesion.Sdk.<Name>/
            Sdk/Sdk.props
            Sdk/Sdk.targets
            Targets/Sdk.<Name>.props
            Targets/Sdk.<Name>.targets
            Tasks/Assimalign.Cohesion.Sdk.<Name>.Tasks.csproj

        frameworks/Assimalign.Cohesion.App.<Name>.Refs/src/Assimalign.Cohesion.App.<Name>.Refs.csproj
        frameworks/Assimalign.Cohesion.App.<Name>.Runtime/src/Assimalign.Cohesion.App.<Name>.Runtime.csproj

    The framework starts with just the umbrella assembly (App.<Name>.dll).
    Categorize libraries from resources/<Name>/ into this framework by
    adding <CohesionFrameworkAssembly> entries to the corresponding
    ItemGroup in frameworks/Assimalign.Cohesion.App.props (a separate
    script step appends an empty placeholder ItemGroup for each new
    framework so you can fill them in incrementally).

    Existing files are not overwritten unless -Force is passed.

.PARAMETER Name
    One or more resource names. If omitted, auto-discovers from
    resources/* (excluding Web and Database which already exist).

.PARAMETER Force
    Overwrite existing files. Use with care.

.EXAMPLE
    pwsh installer\scripts\New-CohesionDomainScaffold.ps1
        Scaffold every missing resource domain.

.EXAMPLE
    pwsh installer\scripts\New-CohesionDomainScaffold.ps1 -Name Scheduler,IdentityHub
        Scaffold just those two.
#>
[CmdletBinding()]
param(
    [string[]]$Name,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (-not $Name -or $Name.Count -eq 0) {
    $resourcesDir = Join-Path $repoRoot 'resources'
    # Discover from resources/. Skip Web and Database (already scaffolded
    # before this script was introduced) plus anything already on disk
    # as a Sdk.<Name> folder.
    $Name = Get-ChildItem -LiteralPath $resourcesDir -Directory |
        Where-Object { $_.Name -notin @('Web','Database') } |
        Select-Object -ExpandProperty Name
}

# ---------------------------------------------------------------------------
# Templates. Each takes one parameter ($n) - the resource Name. Tabs match
# the existing files' indentation (tabs for csproj/props/targets bodies).
# ---------------------------------------------------------------------------

$TasksCsprojTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<PackageId>Assimalign.Cohesion.Sdk.{NAME}</PackageId>
		<OutDir>$(CohesionOutputPathForSdk)\$(NETCoreSdkVersion)\sdks\$(PackageId)\Tasks</OutDir>
	</PropertyGroup>
	<ItemGroup>
		<CohesionPackageReference Include="Microsoft.Build" />
		<CohesionPackageReference Include="Microsoft.Build.Framework" />
		<CohesionPackageReference Include="Microsoft.Build.Utilities.Core" />
	</ItemGroup>
</Project>
'@

$SdkPropsTemplate = @'
<Project ToolsVersion="14.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
	<!--
		Chain to the base Cohesion SDK (registers all three+ Cohesion
		KnownFrameworkReferences and auto-includes the App framework).
	-->
	<Import Sdk="Assimalign.Cohesion.Sdk" Project="Sdk.props" />

	<!--
		Layer the {NAME}-specific framework on top. Consumer gets
		App + App.{NAME}.

		Note: <Import Sdk> doesn't honor inline-version syntax; consumers
		of Sdk.{NAME} must pin Assimalign.Cohesion.Sdk in their global.json
		alongside Sdk.{NAME}.
	-->
	<ItemGroup Condition="'$(CohesionAutoIncludeAppFramework)' != 'false'">
		<FrameworkReference Include="Assimalign.Cohesion.App.{NAME}" />
	</ItemGroup>
</Project>
'@

$SdkTargetsTemplate = @'
<Project ToolsVersion="14.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
	<!-- Version resolved via consumer's global.json msbuild-sdks pin. -->
	<Import Sdk="Assimalign.Cohesion.Sdk" Project="Sdk.targets" />
</Project>
'@

# Reserved for per-domain build conventions later. Empty by default so the
# files exist and are discoverable; the SDK packs them at Targets/.
$DomainPropsTemplate = @'
<Project>

</Project>
'@

$DomainTargetsTemplate = @'
<Project>

</Project>
'@

$RuntimeCsprojTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<TargetFramework>$(TargetFrameworkLatest)</TargetFramework>
		<RootNamespace>Assimalign.Cohesion</RootNamespace>
		<AssemblyName>Assimalign.Cohesion.App.{NAME}</AssemblyName>

		<ProduceReferenceAssembly>true</ProduceReferenceAssembly>
		<IsAotCompatible>true</IsAotCompatible>

		<PackageId Condition="'$(RuntimeIdentifier)' != ''">Assimalign.Cohesion.App.{NAME}.Runtime.$(RuntimeIdentifier)</PackageId>
		<PackageId Condition="'$(RuntimeIdentifier)' == ''">Assimalign.Cohesion.App.{NAME}.Runtime</PackageId>

		<IsPackable>true</IsPackable>
		<IncludeBuildOutput>false</IncludeBuildOutput>
		<NoWarn>$(NoWarn);NU5128;NU5100;NU5131</NoWarn>

		<CohesionFrameworkName>Assimalign.Cohesion.App.{NAME}</CohesionFrameworkName>
		<CohesionFrameworkKind>Runtime</CohesionFrameworkKind>
	</PropertyGroup>

	<Import Project="..\..\Assimalign.Cohesion.App.props" />
	<ItemGroup>
		<CohesionProjectReference Include="@(CohesionFrameworkAssembly)"
		                          Exclude="$(AssemblyName)" />
	</ItemGroup>

	<Import Project="..\..\Assimalign.Cohesion.App.targets" />
</Project>
'@

$RefsCsprojTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<TargetFramework>$(TargetFrameworkLatest)</TargetFramework>
		<RootNamespace>Assimalign.Cohesion</RootNamespace>

		<EnableDefaultCompileItems>false</EnableDefaultCompileItems>
		<GenerateAssemblyInfo>false</GenerateAssemblyInfo>
		<GenerateDependencyFile>false</GenerateDependencyFile>

		<PackageId>Assimalign.Cohesion.App.{NAME}.Ref</PackageId>
		<IsPackable>true</IsPackable>
		<IncludeBuildOutput>false</IncludeBuildOutput>
		<NoWarn>$(NoWarn);NU5128;NU5100;NU5131</NoWarn>

		<CohesionFrameworkName>Assimalign.Cohesion.App.{NAME}</CohesionFrameworkName>
		<CohesionFrameworkKind>Ref</CohesionFrameworkKind>
	</PropertyGroup>

	<ItemGroup>
		<ProjectReference Include="..\..\Assimalign.Cohesion.App.{NAME}.Runtime\src\Assimalign.Cohesion.App.{NAME}.Runtime.csproj"
			ReferenceOutputAssembly="false"
			SkipGetTargetFrameworkProperties="true"
			UndefineProperties="TargetFramework;RuntimeIdentifier" />
	</ItemGroup>

	<Import Project="..\..\Assimalign.Cohesion.App.props" />
	<Import Project="..\..\Assimalign.Cohesion.App.targets" />
</Project>
'@

function Write-IfNotExists {
    param([string]$Path, [string]$Content)
    if ((Test-Path -LiteralPath $Path) -and -not $Force) {
        Write-Host "  exists: $Path" -ForegroundColor DarkGray
        return
    }
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) {
        [void](New-Item -ItemType Directory -Path $dir -Force)
    }
    # UTF-8 without BOM. PowerShell 5's Set-Content -Encoding utf8 writes
    # a BOM; the .NET API doesn't. csproj/props files with a BOM cause
    # subtle MSBuild quirks on Linux runners, so we avoid it.
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "  wrote:  $Path" -ForegroundColor DarkGray
}

Write-Host "Scaffolding $($Name.Count) Cohesion domain(s): $($Name -join ', ')" -ForegroundColor Cyan
Write-Host ""

foreach ($n in $Name) {
    Write-Host "[$n]" -ForegroundColor Cyan

    $sdkRoot       = Join-Path $repoRoot "sdks\Assimalign.Cohesion.Sdk.$n"
    $frameworkRoot = Join-Path $repoRoot "frameworks"

    # SDK ---------------------------------------------------------------
    Write-IfNotExists -Path (Join-Path $sdkRoot "Tasks\Assimalign.Cohesion.Sdk.$n.Tasks.csproj") `
                     -Content $TasksCsprojTemplate.Replace('{NAME}', $n)

    Write-IfNotExists -Path (Join-Path $sdkRoot "Sdk\Sdk.props") `
                     -Content $SdkPropsTemplate.Replace('{NAME}', $n)

    Write-IfNotExists -Path (Join-Path $sdkRoot "Sdk\Sdk.targets") `
                     -Content $SdkTargetsTemplate.Replace('{NAME}', $n)

    Write-IfNotExists -Path (Join-Path $sdkRoot "Targets\Sdk.$n.props") `
                     -Content $DomainPropsTemplate.Replace('{NAME}', $n)

    Write-IfNotExists -Path (Join-Path $sdkRoot "Targets\Sdk.$n.targets") `
                     -Content $DomainTargetsTemplate.Replace('{NAME}', $n)

    # Framework ---------------------------------------------------------
    Write-IfNotExists -Path (Join-Path $frameworkRoot "Assimalign.Cohesion.App.$n.Runtime\src\Assimalign.Cohesion.App.$n.Runtime.csproj") `
                     -Content $RuntimeCsprojTemplate.Replace('{NAME}', $n)

    Write-IfNotExists -Path (Join-Path $frameworkRoot "Assimalign.Cohesion.App.$n.Refs\src\Assimalign.Cohesion.App.$n.Refs.csproj") `
                     -Content $RefsCsprojTemplate.Replace('{NAME}', $n)
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps (these are not yet automated):" -ForegroundColor DarkGray
Write-Host "  1. Add a KnownFrameworkReference per new framework in" -ForegroundColor DarkGray
Write-Host "     sdks/Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props" -ForegroundColor DarkGray
Write-Host "  2. Add an ItemGroup placeholder per new framework in" -ForegroundColor DarkGray
Write-Host "     frameworks/Assimalign.Cohesion.App.props" -ForegroundColor DarkGray
Write-Host "  3. Add each new framework to `$cohesionFrameworks in" -ForegroundColor DarkGray
Write-Host "     installer/scripts/Install-Local.ps1" -ForegroundColor DarkGray
Write-Host "  4. Add each new project pair to frameworks/Assimalign.Cohesion.Frameworks.slnx" -ForegroundColor DarkGray
