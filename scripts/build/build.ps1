[CmdletBinding()]
Param(
    [ValidateSet(
        'sdk',
        'service',
        'library',
        'extension'
    )]
    [Parameter(Position=0, Mandatory=$true)]
    [string]$Command,
    
    [Parameter(Position=1, ValueFromRemainingArguments=$true)]
    [string[]]$Arguments
)

$Params = Convert-ArgumentsToHashTable -Arguments $Arguments

switch ($Command) {
    'sdk' {
        . $PSScriptRoot/build.ps1
    }
    'service' {
        . $PSScriptRoot/build.ps1
    }
    'library' {
        . $PSScriptRoot/build.ps1
    }
    'extension' {
        . $PSScriptRoot/build.ps1
    }
}