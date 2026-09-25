#Requires -Version 5.1
<#
.SYNOPSIS
    Scaffolds a Cohesion domain (SDK + Framework family + ApplicationModel)
    for one or more resource categories under the resources/ folder.

.DESCRIPTION
    For each resource name (e.g. "Scheduler", "IdentityHub", ...), creates
    twelve files following the existing Web / Database conventions:

        sdks/Assimalign.Cohesion.Sdk.<Name>/
            Sdk/Sdk.props
            Sdk/Sdk.targets
            Targets/Sdk.<Name>.props
            Targets/Sdk.<Name>.targets
            Tasks/src/Assimalign.Cohesion.Sdk.<Name>.Tasks.csproj

        resources/<Name>/Directory.Build.props

        resources/<Name>/Assimalign.Cohesion.<Name>.Refs/
            Directory.Build.props
            src/Assimalign.Cohesion.<Name>.Refs.csproj

        resources/<Name>/Assimalign.Cohesion.<Name>.Runtime/
            Directory.Build.props
            src/Assimalign.Cohesion.<Name>.Runtime.csproj

        resources/<Name>/Assimalign.Cohesion.<Name>.ApplicationModel/
            src/Assimalign.Cohesion.<Name>.ApplicationModel.csproj
            src/<Name>ResourceControlPlane.cs

    The two framework producers follow the naming convention in
    .claude/rules/build-system.md ("Framework producer projects"): the
    project is named for its area, while the assembly, package, and
    framework names keep the App segment (Assimalign.Cohesion.App.<Name>,
    Assimalign.Cohesion.App.<Name>.Ref, Assimalign.Cohesion.App.<Name>.Runtime.<rid>).

    The framework's membership list is the Runtime producer's
    Directory.Build.props; the Refs producer's Directory.Build.props imports
    it, so both producers read one list. It starts with just the umbrella
    assembly (Assimalign.Cohesion.App.<Name>). Add the area's libraries to it
    as <CohesionFrameworkAssembly> entries, or as
    <CohesionFrameworkPrivateAssembly> for a runtime-only implementation
    detail. That file imports the area's own Directory.Build.props, which
    the scaffold writes too when the area has none.

    Existing files are not overwritten unless -Force is passed.

.PARAMETER Name
    One or more resource names. If omitted, auto-discovers from
    resources/* (excluding Web and Database which already exist).

.PARAMETER Force
    Overwrite existing files. Use with care. The Runtime producer's
    Directory.Build.props is never overwritten: it holds the hand-curated
    membership list.

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
# Templates. Each takes one parameter ($n) - the resource Name - except the
# area's Directory.Build.props, which is the same in every area. Tabs match
# the existing files' indentation (tabs for csproj/props/targets bodies;
# the producers' Directory.Build.props use four spaces, noted below).
# ---------------------------------------------------------------------------

$TasksCsprojTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<RootNamespace>Assimalign.Cohesion.Sdk.{NAME}.Tasks</RootNamespace>
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
	<!-- The sibling ApplicationModel SDK resolves at this area's exact frozen version. -->
	<Import Project="Sdk.targets" Sdk="Assimalign.Cohesion.Sdk.ApplicationModel" Version="$(CohesionVersion)" Condition="'$(CohesionApplicationModel)' == 'enabled'" />
	<!-- Enabled projects receive the base targets through ApplicationModel; disabled projects import them here. -->
	<Import Sdk="Assimalign.Cohesion.Sdk" Project="Sdk.targets" Condition="'$(_CohesionApplicationModelSdkImported)' != 'true'" />
</Project>
'@

# The area SDK identifies the declarative package and default control-plane
# factory used by enabled resource projects. Endpoint, lifecycle, and other
# kind-specific defaults remain explicit per-domain build conventions.
$DomainPropsTemplate = @'
<Project>
	<PropertyGroup>
		<CohesionResourceApplicationModel Condition="'$(CohesionResourceApplicationModel)' == ''">Assimalign.Cohesion.{NAME}.ApplicationModel</CohesionResourceApplicationModel>
		<CohesionResourceControlPlaneType Condition="'$(CohesionResourceControlPlaneType)' == ''">Assimalign.Cohesion.{NAME}.ApplicationModel.{NAME}ResourceControlPlane</CohesionResourceControlPlaneType>
	</PropertyGroup>
</Project>
'@

$DomainTargetsTemplate = @'
<Project>

</Project>
'@

$RuntimeCsprojTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		
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

	<Import Project="$(CohesionRepositoryDirectory)libraries\App\Assimalign.Cohesion.App.props" />
	<ItemGroup>
		<CohesionProjectReference Include="@(CohesionFrameworkAssembly)"
		                          Exclude="$(AssemblyName)" />
	</ItemGroup>

	<Import Project="$(CohesionRepositoryDirectory)libraries\App\Assimalign.Cohesion.App.targets" />
</Project>
'@

$RefsCsprojTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		
		<RootNamespace>Assimalign.Cohesion</RootNamespace>
		<AssemblyName>Assimalign.Cohesion.App.{NAME}.Refs</AssemblyName>

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
		<ProjectReference Include="..\..\Assimalign.Cohesion.{NAME}.Runtime\src\Assimalign.Cohesion.{NAME}.Runtime.csproj"
			ReferenceOutputAssembly="false"
			SkipGetTargetFrameworkProperties="true"
			UndefineProperties="TargetFramework;RuntimeIdentifier" />
	</ItemGroup>

	<Import Project="$(CohesionRepositoryDirectory)libraries\App\Assimalign.Cohesion.App.props" />
	<Import Project="$(CohesionRepositoryDirectory)libraries\App\Assimalign.Cohesion.App.targets" />
</Project>
'@

# The area's own Directory.Build.props, which the Runtime producer's imports.
$AreaPropsTemplate = @'
<Project>
	<Import Project="..\Directory.Build.props" />
</Project>
'@

# The framework's membership list. Unlike the other templates these two use
# four-space indentation, matching every area's copy.
$RuntimePropsTemplate = @'
<Project>
    <!--
        Assimalign.Cohesion.App.{NAME} membership: every assembly the framework ships.

        CohesionFrameworkAssembly items are public: listed in the Ref pack's FrameworkList.xml
        and shipped in the Runtime pack. CohesionFrameworkPrivateAssembly items are runtime-only:
        shipped in the Runtime pack and listed in RuntimeList.xml, but absent from the Ref pack.
        The first entry is the framework's umbrella assembly, this producer's own output.

        This Runtime producer references every entry. Its sibling Refs producer imports this
        file as its own Directory.Build.props, so both producers read this one list.
        Rules: .claude/rules/build-system.md, "Framework membership".
    -->
    <Import Project="..\Directory.Build.props" />

    <ItemGroup>
        <CohesionFrameworkAssembly Include="Assimalign.Cohesion.App.{NAME}" />
    </ItemGroup>
</Project>
'@

$RefsPropsTemplate = @'
<Project>
    <!--
        The Refs producer packs the framework its Runtime sibling defines: importing that
        sibling's Directory.Build.props, which imports the parent chain, gives this project
        the same Assimalign.Cohesion.App.{NAME} membership list.
    -->
    <Import Project="..\Assimalign.Cohesion.{NAME}.Runtime\Directory.Build.props" />
</Project>
'@

$ApplicationModelCsprojTemplate = @'
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<RootNamespace>Assimalign.Cohesion.ApplicationModel</RootNamespace>
		<CohesionApplicationModelGuard>true</CohesionApplicationModelGuard>
	</PropertyGroup>
	<ItemGroup>
		<CohesionProjectReference Include="Assimalign.Cohesion.ApplicationModel" />
		<CohesionProjectReference Include="Assimalign.Cohesion.Hosting.Resources" />
	</ItemGroup>
</Project>
'@

$ResourceControlPlaneTemplate = @'
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.{NAME}.ApplicationModel;

/// <summary>
/// Creates the default control plane shared by enabled {NAME} resources.
/// </summary>
public static class {NAME}ResourceControlPlane
{
    /// <summary>
    /// Creates a new isolated {NAME} resource control plane.
    /// </summary>
    /// <returns>The {NAME} area's default resource control plane.</returns>
    public static IResourceControlPlane Create()
    {
        return ResourceControlPlane.Create();
    }
}
'@

function Write-IfNotExists {
    # -NeverOverwrite keeps an existing file even under -Force.
    param([string]$Path, [string]$Content, [switch]$NeverOverwrite)
    if ((Test-Path -LiteralPath $Path) -and ($NeverOverwrite -or -not $Force)) {
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
    $areaRoot      = Join-Path $repoRoot "resources\$n"
    $applicationModelRoot = Join-Path $areaRoot "Assimalign.Cohesion.$n.ApplicationModel"

    # SDK ---------------------------------------------------------------
    Write-IfNotExists -Path (Join-Path $sdkRoot "Tasks\src\Assimalign.Cohesion.Sdk.$n.Tasks.csproj") `
                     -Content $TasksCsprojTemplate.Replace('{NAME}', $n)

    Write-IfNotExists -Path (Join-Path $sdkRoot "Sdk\Sdk.props") `
                     -Content $SdkPropsTemplate.Replace('{NAME}', $n)

    Write-IfNotExists -Path (Join-Path $sdkRoot "Sdk\Sdk.targets") `
                     -Content $SdkTargetsTemplate.Replace('{NAME}', $n)

    Write-IfNotExists -Path (Join-Path $sdkRoot "Targets\Sdk.$n.props") `
                     -Content $DomainPropsTemplate.Replace('{NAME}', $n)

    Write-IfNotExists -Path (Join-Path $sdkRoot "Targets\Sdk.$n.targets") `
                     -Content $DomainTargetsTemplate.Replace('{NAME}', $n)

    # Framework producers (named for the area; build-system.md) ----------
    Write-IfNotExists -Path (Join-Path $areaRoot "Directory.Build.props") `
                     -Content $AreaPropsTemplate

    Write-IfNotExists -Path (Join-Path $areaRoot "Assimalign.Cohesion.$n.Runtime\Directory.Build.props") `
                     -Content $RuntimePropsTemplate.Replace('{NAME}', $n) -NeverOverwrite

    Write-IfNotExists -Path (Join-Path $areaRoot "Assimalign.Cohesion.$n.Runtime\src\Assimalign.Cohesion.$n.Runtime.csproj") `
                     -Content $RuntimeCsprojTemplate.Replace('{NAME}', $n)

    Write-IfNotExists -Path (Join-Path $areaRoot "Assimalign.Cohesion.$n.Refs\Directory.Build.props") `
                     -Content $RefsPropsTemplate.Replace('{NAME}', $n)

    Write-IfNotExists -Path (Join-Path $areaRoot "Assimalign.Cohesion.$n.Refs\src\Assimalign.Cohesion.$n.Refs.csproj") `
                     -Content $RefsCsprojTemplate.Replace('{NAME}', $n)

    # ApplicationModel -------------------------------------------------
    Write-IfNotExists -Path (Join-Path $applicationModelRoot "src\Assimalign.Cohesion.$n.ApplicationModel.csproj") `
                     -Content $ApplicationModelCsprojTemplate.Replace('{NAME}', $n)

    Write-IfNotExists -Path (Join-Path $applicationModelRoot "src\${n}ResourceControlPlane.cs") `
                     -Content $ResourceControlPlaneTemplate.Replace('{NAME}', $n)
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps (these are not yet automated):" -ForegroundColor DarkGray
Write-Host "  1. Add a KnownFrameworkReference per new framework in" -ForegroundColor DarkGray
Write-Host "     sdks/Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props" -ForegroundColor DarkGray
Write-Host "  2. List each new framework's members after its umbrella assembly in" -ForegroundColor DarkGray
Write-Host "     resources/<Name>/Assimalign.Cohesion.<Name>.Runtime/Directory.Build.props" -ForegroundColor DarkGray
Write-Host "  3. Add each new framework and SDK to the release inventory in" -ForegroundColor DarkGray
Write-Host "     installer/scripts/modules/CohesionPackaging.psm1" -ForegroundColor DarkGray
Write-Host "  4. Add each new producer pair and the new Directory.Build.props files to" -ForegroundColor DarkGray
Write-Host "     resources/<Name>/Assimalign.Cohesion.<Name>.slnx, resources/Assimalign.Cohesion.Resources.slnx," -ForegroundColor DarkGray
Write-Host "     and the root Assimalign.Cohesion.slnx" -ForegroundColor DarkGray
Write-Host "  5. Add each new ApplicationModel project to the resource solutions and release inventory" -ForegroundColor DarkGray
