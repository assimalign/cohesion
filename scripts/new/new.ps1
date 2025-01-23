[CmdletBinding()]
Param(
    [ValidateSet(
        'library',
        'service',
        'tool'
    )]
    [Parameter(Position=0, Mandatory=$true)]
    [string]$Command,
    
    [Parameter(Position=1, ValueFromRemainingArguments=$true)]
    [string[]]$Arguments
)

$Params = Convert-ArgumentsToHashTable -Arguments $Arguments

switch ($Command) {
    'service' {
        . $PSScriptRoot/new-service.ps1 @Params
    }
    'library' {
        . $PSScriptRoot/new-library.ps1 @Params
    }
}