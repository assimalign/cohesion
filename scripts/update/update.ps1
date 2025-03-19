[CmdletBinding()]
Param(
    [ValidateSet('project-references', ErrorMessage = 'Valid Commands: "project-references"')]
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Command,
    [Parameter(Position=1, Mandatory=$false)]
    [string[]]$Arguments
)


switch ($Command) {
    'project-references' {
        . $PSScriptRoot/update-project-references.ps1 
    }
}