#region CLI Command
Write-Host "    1 .. Setting Cohesion Development CLI" -ForegroundColor Red

$CohesionCmdRepoPath = [System.IO.Path]::GetFullPath("$PSScriptRoot\..")
$CohesionCmdEntryPoint = [System.IO.Path]::Combine($CohesionCmdRepoPath, "main.ps1")
$CohesionCmd = "function cohesion-dev { param([Parameter(Position = 1, ValueFromRemainingArguments = `$true)][string[]]`$Arguments ); & `"$CohesionCmdEntryPoint`" @Arguments; }"

$Profiles = @(
    "$env:USERPROFILE\Documents\PowerShell\Microsoft.PowerShell_profile.ps1"
    "$env:USERPROFILE\OneDrive\Documents\PowerShell\Microsoft.PowerShell_profile.ps1"
)

$ProfileFound = $false
$Profiles | ForEach-Object {
    Write-Host "        - Checking for Profile" -ForegroundColor Green
    Write-host "          Path: $_" -ForegroundColor Green
    Write-Host "          Found: "  -ForegroundColor Green -NoNewline
    if ((Test-Path $_)) {
        Write-Host "true" -ForegroundColor Cyan
        $ProfileFound = $true
        $Content = Get-Content $_ -Raw

        if (-not $Content.Contains('cohesion-dev')) {
            $Content = $Content + [System.Environment]::NewLine
            $Content = $Content + [System.Environment]::NewLine
            $Content = $Content + $CohesionCmd

            Set-Content -Path $_ -Value $Content
        }
    } else {
        Write-Host "false" -ForegroundColor Red
    }
    Write-Host ""
}

if ($ProfileFound -eq $false) {
    Write-Warning -Message "No PowerShell Profile was found to set the CLI Shortcut"
}

Write-Host ""
#endregion


#region Run Tooling Installations
Write-Host "    2 .. Running Tooling Installations" -ForegroundColor Red

$Installations = @(
    @{ Name = 'Git'; Id = 'Git.Git' }
    @{ Name = '.NET 6'; Id = 'Microsoft.DotNet.SDK.6'; }
    @{ Name = '.NET 7'; Id = 'Microsoft.DotNet.SDK.7'; }
    @{ Name = '.NET 8'; Id = 'Microsoft.DotNet.SDK.8'; }
    @{ Name = '.NET 9'; Id = 'Microsoft.DotNet.SDK.9'; }
    @{ Name = 'LINQPad 8'; Id = 'LINQPad.LINQPad.8'; }
    @{ Name = 'Visual Studio Code'; Id = 'Microsoft.VisualStudioCode'; }
    @{ Name = 'Visual Studio (Community)'; Id = 'Microsoft.VisualStudio.2022.Community'; }
    @{ Name = 'Windows Terminal'; Id = 'Microsoft.WindowsTerminal'; }
    @{ Name = 'Microsoft Windows Subsystem for Linux'; Id = 'Microsoft.WSL'; }
    @{ Name = 'Docker Desktop'; Id = 'Docker.DockerDesktop'; }
    @{ Name = 'Kubernetes (Minikube)'; Id = 'Kubernetes.minikube'; }
    @{ Name = 'Kubernetes (kubectl)'; Id = 'Kubernetes.kubectl' }
)


# Check for required modules

if ((Test-Module -Name 'Microsoft.WinGet.Client') -eq $false) {
    Install-Module -Name 'Microsoft.WinGet.Client' -AcceptLicense -Force -Scope CurrentUser -AllowClobber
}

# Get Formatting 
$Measure = $Installations.Name | ForEach-Object { return "      - $_ Installation".Length } | Measure-Object -Maximum

# Get Installed Packages
$Packages = Get-WinGetPackage -ErrorAction Break

# Begin Installations
$Installations | ForEach-Object {

    $Id = $_.Id
    $Name = $_.Name

    Write-Host "        - $Name Installation ".PadRight($Measure.Maximum + 5, '.') -ForegroundColor Green -NoNewline
    
    $Package = $Packages | Where-Object { $_.Id -eq $Id } | Select-Object -First 1
    $Installed = $Package -ne $null

    if ($Installed) {
        Write-Host " Installed" -ForegroundColor Cyan
    } 
    elseif ($Installed -and $Package.IsUpdateAvailable) {
        Write-Host " Updating" -ForegroundColor Magenta
        Update-WinGetPackage -Id $Id
    }
    else {
        Write-Host " Installing" -ForegroundColor Yellow
        Install-WinGetPackage -Id $Id 
    }
}
#endregion