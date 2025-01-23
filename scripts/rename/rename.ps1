[CmdletBinding()]
Param(
    [ValidateSet('library', 'service')]
    [Parameter(Position=0, Mandatory=$true)]
    [string]$Command,

    [Parameter(Position=1, ValueFromRemainingArguments=$true)]
    [string[]]$Arguments
)

$Params = Convert-ArgumentsToHashTable -Arguments $Arguments

switch ($Command) {
    'service' {
        . $PSScriptRoot/rename-service.ps1 @Params
    }
    'library' {
        . $PSScriptRoot/rename-library.ps1 @Params
    }
}