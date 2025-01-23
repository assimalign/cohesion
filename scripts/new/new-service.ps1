[CmdletBinding()]
Param(
    [Parameter(Mandatory = $true)]
    [string]$Name
)

# Get Full Path of project
$Path = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..\libraries\$Name")

if (-not (Test-Path $Path)) {
    New-Item $Path -ItemType Directory
}

