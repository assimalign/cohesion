[CmdletBinding()]
Param(
    [ValidateSet('setup', 'new', 'build', 'rename')]
    [Parameter(Mandatory=$true, Position = 0)]
    [string]$Command,
    [Parameter(Position=1,ValueFromRemainingArguments=$true)]
    [string[]]$Arguments
)

# Import Utils
Import-Module -Name "$PSScriptRoot\Utils" -Force

switch ($Command) {
    'setup' { 
        . $PSScriptRoot/setup/setup.ps1 @Arguments 
    }
    'new' { 
        . $PSScriptRoot/new/new.ps1 @Arguments
    }
    'rename' { 
        . $PSScriptRoot/rename/rename.ps1 @Arguments
    }
    'build' {
        . $PSScriptRoot/build/build.ps1 @Arguments
    }
}