

function Get-RandomForeground {
    [System.ConsoleColor]$color = 1, 2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 14 | Get-Random
    return $color
}


function Install-PowerShellCore {

    Write-Host "    .. Checking for PowerShell Core"

    $Version = "7.4.6"
    $Path = ("C:\Program Files\PowerShell\" + $Version.Split('.')[0])
    $IsInstalled = Test-Path "$Path\pwsh.exe"

    if ($IsInstalled -eq $false) {
        $Architecture = if ([Environment]::Is64BitOperatingSystem) { "win-x64" } else { "win-x86" }
        $Assets = "https://github.com/PowerShell/PowerShell/releases/download/v$Version/PowerShell-$Version-$Architecture.msi"
        $DownloadTo = "$env:TEMP\PowerShell-$Version$arch.msi"
        
        Invoke-WebRequest -Uri $Assets -OutFile $DownloadTo

        if (Test-Path $DownloadTo) {
            Write-Host "Download complete. Installing PowerShell Core..." -ForegroundColor Cyan
            Start-Process msiexec.exe -ArgumentList "/i `"$DownloadTo`" /quiet /norestart" -Wait
            Remove-Item $DownloadTo -Force
        }
    }
}

function Install-VisualStudioCode {
    Write-Host "    .. Checking for Visual Studio Code"
}

function Install-DotNet {
    Write-Host "    .. Checking for ,NET"
}

function Install-Tooling {
    Write-Host "Installing Tooling" -ForegroundColor (Get-RandomForeground)
    Install-PowerShellCore
    Install-DotNet
    Install-VisualStudioCode
    Write-Host ""


}

Install-Tooling