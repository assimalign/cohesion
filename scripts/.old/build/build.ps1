[CmdletBinding()]
Param(
    [ValidateSet('sdk', 'service', 'library', 'extension')]
    [Parameter(Position = 0, Mandatory = $true)]
    [string]$Command,
    
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

$Scripts = @{
    sdk = "$PSScriptRoot/build-sdk.ps1"
    service = "$PSScriptRoot/build-service.ps1"
    library = "$PSScriptRoot/build-library.ps1"
    extension = "$PSScriptRoot/build-extension.ps1"
}

# Format Params
$Params = Convert-ArgumentsToHashTable -Arguments $Arguments

# Run Script
. $Scripts[$Command] @Params