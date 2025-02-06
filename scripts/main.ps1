[CmdletBinding()]
Param(
    [ValidateSet('setup', 'new', 'build', 'publish', 'rename')]
    [Parameter(Mandatory = $false, Position = 0)]
    [string]$Command,
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

# Import Utils
Import-Module -Name "$PSScriptRoot\Utils" -Force

# Define CLI Scripts
$Scripts = @{
    setup   = "$PSScriptRoot/setup/setup.ps1"
    new     = "$PSScriptRoot/new/new.ps1"
    rename  = "$PSScriptRoot/rename/rename.ps1"
    build   = "$PSScriptRoot/build/build.ps1"
    publish = "$PSScriptRoot/publish/publish.ps1"
}

# Run Command, if any
if ($null -ne $Command) {
    . $Scripts[$Command] @Arguments
}