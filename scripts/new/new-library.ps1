[CmdletBinding()]
Param(
    [Alias('n')]
    [Parameter(Mandatory = $true)]
    [string]$Name,

    [Alias('s')]
    [Parameter(Mandatory = $true)]
    [string]$Service
)

# Capture current location
$MyLocation = Get-Location

# Get Full Path of project
$Path = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..\libraries\$Service")

if (-not (Test-Path $Path)) {
    Write-Error ("Invalid Service '$Service'. Valid Services: " + ((Get-ChildItem "..\libraries" -Directory).Name -join ','))
    return
}

$Items = @(
    @{ Type = 'classlib'; Suffix = $null; Directory = 'src' }
    @{ Type = 'xunit'; Suffix = 'Tests'; Directory = 'tests' }
    @{ Type = 'console'; Suffix = 'Benchmarks'; Directory = 'benchmarks' }
    @{ Type = 'sln'; Suffix = $null; Directory = $null }
)

$Items | ForEach-Object {
    $CliArgs = "new " + $_.Type + " -n $Name"

    if (-not [System.String]::IsNullOrEmpty($_.Suffix)) {
        $CliArgs = $CliArgs + "." + $_.Suffix
    }
    if (-not [system.String]::IsNullOrEmpty($_.Directory)) {
        $CliArgs = $CliArgs + " -o $Path\$Name\" + $_.Directory
    }
    else {
        $CliArgs = $CliArgs + " -o $Path\$Name"
    }

    Start-Process dotnet -ArgumentList $CliArgs -Wait -NoNewWindow -PassThru
}


Set-Location "$Path\$Name"
Get-ChildItem -Include *.csproj -Recurse -File | ForEach-Object {
    $SolutionFolder = $_.Directory.BaseName
    $ProjectPath = [System.IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName)

    $SolutionFolder
    $ProjectPath
    Start-Process dotnet -NoNewWindow -Wait -ArgumentList "sln $Name.sln add $ProjectPath --solution-folder $SolutionFolder"
}

New-Item 'README.md' -ItemType File

Set-Location $MyLocation.Path