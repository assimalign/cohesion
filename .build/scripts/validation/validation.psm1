

# This ensure that package versions are being updated view Directory.Build.props in the root folder
function Test-PackageVersionCheck {

    $config = Get-GitConfig -Path "$PSScriptRoot\..\..\..\.git\config"

    $config.Branch | ForEach-Object {
        $_
    }
}