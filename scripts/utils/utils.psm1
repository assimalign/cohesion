function Convert-ArgumentsToHashTable {
    param (
        [string[]]$Arguments
    )
    $Params = @{}

    for ($i = 0; $i -lt $Arguments.Length; ) {
        
        $ParamName = $Arguments[$i]
        $ParamValue = $null

        # Set Parameter Key -{Parameter Key}
        if ($ParamName.StartsWith('-')) {
            $ParamName = $ParamName.TrimStart('-')
        }

        $HasNext = ($i + 1) -lt $Arguments.Length 

        # If no value is next then assume switch
        if ($HasNext -eq $false) {
            $i++;
            $Params.Add($ParamName, $true)
            continue
        }

        # Check for ending ':'
        if ($ParamName.EndsWith(':')) {
            $ParamName = $ParamName.TrimEnd(':') 
        }

        $ParamValue = $Arguments[$i + 1]
        
        # If there is next value, but start with '-', then assume current value is switch
        if ($ParamValue.StartsWith('-')) {
            $i++;
            $Params.Add($ParamName, $true)
            continue
        }
        else {
            $i += 2
            $Params.Add($ParamName, $ParamValue)
        }
    }
    
    return $Params
}
function Test-Module {
    param (
        [Parameter(Mandatory = $true)]
        [string]$Name
    )
    Get-Module -List |
    ForEach-Object {
        if ($_.Name -eq $Name) {
            return $true
        }
    }
    return $false
}
function Test-PowerShellCore {

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

    return $IsInstalled
}

Export-ModuleMember -Function Convert-ArgumentsToHashTable, Test-PowerShellCore, Test-Module
