[CmdletBinding()]
Param(
    [ValidateSet('local', 'build', ErrorMessage = 'Valid Commands: "local", "build"')]
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Command,
    [Parameter(Position=1, Mandatory=$false)]
    [string[]]$Arguments
)

Write-Host "Running Machine Setup Process" -ForegroundColor Red
Write-Host ""

switch ($Command) {
    'local' { 
        if ($IsWindows) { 
            . $PSScriptRoot/setup-local-windows.ps1 
        }
        elseif ($IsLinux) {
            . $PSScriptRoot/setup-local-linux.ps1
        }
        elseif ($IsMacOS) {
            . $PSScriptRoot/setup-local-macos.ps1
        }
        else { 
            Write-Error "Unsupported Platform"
        }
    }
    'build' { 

    }
}

