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

$UpdateReferences = $false
$Items = @(
    @{ Type = 'classlib'; Suffix = $null; Directory = 'src' }
    @{ Type = 'xunit'; Suffix = 'Tests'; Directory = 'tests' }
    @{ Type = 'sln'; Suffix = $null; Directory = $null; Args = '--format slnx' }
)

Set-Location "$Path\$Name"

$Items | ForEach-Object {

    $Type = $_.Type
    $Suffix = $_.Suffix
    $Directory = $_.Directory
    $Args = $_.Args

    switch ($_.Type) {
        { $_ -eq "classlib" -or $_ -eq "xunit" } {
            $CliArgs = "new " + $Type + " -o $Path\$Name\" + $Directory + " -n $Name" + ([system.String]::IsNullOrEmpty($Suffix) ? "" : ".$Suffix")
            $ItemPath = "$Path\$Name\" + "$Directory\$Name"  + ([system.String]::IsNullOrEmpty($Suffix) ? "" : ".$Suffix") + ".csproj"
            break;
        }
        "sln" {
            $CliArgs = "new " + $Type + " -o $Path\$Name\" +" -n $Name" + " " + $Args
            $ItemPath = "$Path\$Name\$Name" + ".slnx"
            break;
        }
    }

    Write-Host "Running Command: dotnet $CliArgs"
    
    if ((Test-Path $ItemPath) -eq $false) {

        if ($ItemPath.EndsWith('.csproj')) {
            $UpdateReferences = $true
        }

        Start-Process dotnet -ArgumentList $CliArgs -Wait -NoNewWindow -PassThru
    }
}



Get-ChildItem -Include *.csproj -Recurse -File | ForEach-Object {
    $SolutionFolder = $_.Directory.BaseName
    $ProjectPath = [System.IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName)

    $SolutionFolder
    $ProjectPath
    Start-Process dotnet -NoNewWindow -Wait -ArgumentList "sln $Name.slnx add $ProjectPath --solution-folder $SolutionFolder"
}

if ((Test-Path "$Path\$Name\README.md") -eq $false) {
    New-Item 'README.md' -ItemType File
}

if ($UpdateReferences -eq $true) {
$UpdateRefs = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\update\update-project-references.ps1")
. $UpdateRefs
}


Set-Location $MyLocation.Path