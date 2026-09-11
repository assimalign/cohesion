#Requires -Version 7.0
<#
.SYNOPSIS
    Validates Cohesion SDK and shared-framework packages through isolated consumers.

.DESCRIPTION
    Creates package-only consumers for the base, Web, and Database SDKs plus an analyzer-bearing
    base-SDK profile. Each consumer is built, published self-contained for the supplied runtime
    identifier, and run. The analyzer profile also proves that generated output arrived through
    the SDK -> framework targeting-pack chain.

.PARAMETER PackageDirectory
    Directory containing the Cohesion SDK and framework .nupkg files to validate.

.PARAMETER Version
    Exact Cohesion package version the generated global.json pins.

.PARAMETER RuntimeIdentifier
    Host runtime identifier used for self-contained publication.

.PARAMETER WorkingDirectory
    Optional parent for the isolated consumer workspace. Defaults to RUNNER_TEMP in CI and
    _out/sdk-smoke locally.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $RuntimeIdentifier,

    [string] $WorkingDirectory
)

$ErrorActionPreference = 'Stop'

$versionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$'
if ($Version -notmatch $versionPattern) {
    throw "Version '$Version' must use MAJOR.MINOR.PATCH[-PRERELEASE]."
}
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    throw 'RuntimeIdentifier cannot be empty.'
}

$packageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$repositoryDirectory = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $WorkingDirectory) {
    $WorkingDirectory = if ($env:RUNNER_TEMP) {
        Join-Path $env:RUNNER_TEMP 'cohesion-sdk-smoke'
    }
    else {
        Join-Path $repositoryDirectory '_out/sdk-smoke'
    }
}

$workingDirectory = [System.IO.Path]::GetFullPath($WorkingDirectory)
$workspace = Join-Path $workingDirectory ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspace -Force | Out-Null

$requiredPackageId = @(
    'Assimalign.Cohesion.Sdk'
    'Assimalign.Cohesion.Sdk.Web'
    'Assimalign.Cohesion.Sdk.Database'
    'Assimalign.Cohesion.App.Ref'
    "Assimalign.Cohesion.App.Runtime.$RuntimeIdentifier"
    'Assimalign.Cohesion.App.Web.Ref'
    "Assimalign.Cohesion.App.Web.Runtime.$RuntimeIdentifier"
    'Assimalign.Cohesion.App.Database.Ref'
    "Assimalign.Cohesion.App.Database.Runtime.$RuntimeIdentifier"
)
$missingPackage = @(
    $requiredPackageId |
        Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $packageDirectory "$($_).$Version.nupkg") -PathType Leaf)
        }
)
if ($missingPackage.Count -gt 0) {
    throw "SDK smoke feed is missing required packages: $($missingPackage -join ', ')."
}

$nugetPath = Join-Path $workspace 'NuGet.Config'
[xml] $nuget = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="cohesion-smoke" value="placeholder" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="cohesion-smoke">
      <package pattern="Assimalign.Cohesion.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
'@
$localSource = @($nuget.configuration.packageSources.add) |
    Where-Object { $_.key -eq 'cohesion-smoke' } |
    Select-Object -First 1
$localSource.value = $packageDirectory
$nuget.Save($nugetPath)

$repositoryGlobalJson = Get-Content -LiteralPath (Join-Path $repositoryDirectory 'global.json') -Raw |
    ConvertFrom-Json
$sdkVersions = [ordered]@{
    'Assimalign.Cohesion.Sdk' = $Version
    'Assimalign.Cohesion.Sdk.Web' = $Version
    'Assimalign.Cohesion.Sdk.Database' = $Version
}
$consumerGlobalJson = [ordered]@{
    sdk = $repositoryGlobalJson.sdk
    'msbuild-sdks' = $sdkVersions
}
$consumerGlobalJson |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $workspace 'global.json') -Encoding utf8

$coreProgramTemplate = @'
using System;

using Assimalign.Cohesion;

Console.WriteLine("Cohesion SDK smoke: __PROFILE__:" + typeof(AppEnvironment).FullName);
'@
$analyzerProgram = @'
using System;

using Assimalign.Cohesion.ObjectMapping;

Console.WriteLine("Cohesion SDK smoke: Analyzer:" + typeof(UserMapper).FullName);

internal sealed class SourceUser
{
    public string Name { get; set; } = string.Empty;

    public int Id { get; set; }
}

internal sealed class TargetUser
{
    public string DisplayName { get; set; } = string.Empty;

    public int UserId { get; set; }
}

internal partial class UserMapper : MapperProfile<TargetUser, SourceUser>
{
    protected override void Configure(MapperProfileDescriptor<TargetUser, SourceUser> descriptor) =>
        descriptor
            .MapMember(target => target.DisplayName, source => source.Name)
            .MapMember(target => target.UserId, source => source.Id);
}
'@

$profiles = @(
    [pscustomobject]@{
        Name = 'SdkSmoke.App'
        Sdk = 'Assimalign.Cohesion.Sdk'
        Program = $coreProgramTemplate.Replace('__PROFILE__', 'App')
        ExpectedOutput = 'Cohesion SDK smoke: App:Assimalign.Cohesion.AppEnvironment'
        Analyzer = $false
    }
    [pscustomobject]@{
        Name = 'SdkSmoke.Web'
        Sdk = 'Assimalign.Cohesion.Sdk.Web'
        Program = $coreProgramTemplate.Replace('__PROFILE__', 'Web')
        ExpectedOutput = 'Cohesion SDK smoke: Web:Assimalign.Cohesion.AppEnvironment'
        Analyzer = $false
    }
    [pscustomobject]@{
        Name = 'SdkSmoke.Database'
        Sdk = 'Assimalign.Cohesion.Sdk.Database'
        Program = $coreProgramTemplate.Replace('__PROFILE__', 'Database')
        ExpectedOutput = 'Cohesion SDK smoke: Database:Assimalign.Cohesion.AppEnvironment'
        Analyzer = $false
    }
    [pscustomobject]@{
        Name = 'SdkSmoke.Analyzer'
        Sdk = 'Assimalign.Cohesion.Sdk'
        Program = $analyzerProgram
        ExpectedOutput = 'Cohesion SDK smoke: Analyzer:UserMapper'
        Analyzer = $true
    }
)

$projectTemplate = @'
<Project Sdk="{0}">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    {1}
  </PropertyGroup>
</Project>
'@

$env:NUGET_PACKAGES = Join-Path $workspace '.nuget/packages'
$env:DOTNET_CLI_HOME = Join-Path $workspace '.dotnet'

foreach ($profile in $profiles) {
    $profileDirectory = Join-Path $workspace $profile.Name
    New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
    Set-Content `
        -LiteralPath (Join-Path $profileDirectory 'Program.cs') `
        -Value $profile.Program `
        -Encoding utf8

    $extraProperties = if ($profile.Analyzer) {
        '<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles><NoWarn>$(NoWarn);CA2252</NoWarn>'
    }
    else {
        ''
    }
    $project = $projectTemplate -f $profile.Sdk, $extraProperties
    $projectPath = Join-Path $profileDirectory "$($profile.Name).csproj"
    Set-Content -LiteralPath $projectPath -Value $project -Encoding utf8

    Write-Host "::group::$($profile.Name) build"
    & dotnet build $projectPath --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "$($profile.Name) build failed with exit code $LASTEXITCODE."
    }
    Write-Host '::endgroup::'
}

$analyzerProfile = $profiles | Where-Object Analyzer | Select-Object -First 1
$analyzerDirectory = Join-Path $workspace $analyzerProfile.Name
$generated = @(
    Get-ChildItem `
        -LiteralPath (Join-Path $analyzerDirectory 'obj') `
        -Recurse `
        -Filter '*.MapperProfile.g.cs' `
        -File `
        -ErrorAction SilentlyContinue
)
if ($generated.Count -ne 1) {
    throw "MapperProfileGenerator produced $($generated.Count) *.MapperProfile.g.cs files; expected exactly one through the SDK package chain."
}
$generatedSource = Get-Content -LiteralPath $generated[0].FullName -Raw
if (-not $generatedSource.Contains('TryConfigureGenerated', [System.StringComparison]::Ordinal)) {
    throw "Generated mapper output '$($generated[0].FullName)' is missing TryConfigureGenerated."
}
Write-Host "Generated SDK output: $($generated[0].FullName)"

foreach ($profile in $profiles) {
    $profileDirectory = Join-Path $workspace $profile.Name
    $projectPath = Join-Path $profileDirectory "$($profile.Name).csproj"
    $publishDirectory = Join-Path $profileDirectory "publish/$RuntimeIdentifier"

    Write-Host "::group::$($profile.Name) self-contained publish"
    & dotnet publish `
        $projectPath `
        --configuration Release `
        --runtime $RuntimeIdentifier `
        --self-contained true `
        --output $publishDirectory `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "$($profile.Name) publish failed with exit code $LASTEXITCODE."
    }
    Write-Host '::endgroup::'

    $appHostName = if ($IsWindows) { "$($profile.Name).exe" } else { $profile.Name }
    $appHost = Join-Path $publishDirectory $appHostName
    if (-not (Test-Path -LiteralPath $appHost -PathType Leaf)) {
        throw "$($profile.Name) apphost was not found at '$appHost'."
    }

    $output = (& $appHost | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "$($profile.Name) apphost exited with code $LASTEXITCODE."
    }
    if ($output -ne $profile.ExpectedOutput) {
        throw "$($profile.Name) output was '$output'; expected '$($profile.ExpectedOutput)'."
    }
    Write-Host "$($profile.Name) output asserted: $output"
}

Write-Host "SDK consumer smoke passed for $RuntimeIdentifier at version $Version."
