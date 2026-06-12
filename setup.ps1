$BasePath = $PSScriptRoot

Import-Module Assimalign.Cohesion.DevScripts -Force


$Solutions = @(
    # "$BasePath\Assimalign.Cohesion.slnx",
    "$BasePath\frameworks\Assimalign.Cohesion.Frameworks.slnx",
    "$BasePath\libraries\Assimalign.Cohesion.Libraries.slnx",
    "$BasePath\resources\Assimalign.Cohesion.Resources.slnx",
    "$BasePath\sdks\Assimalign.Cohesion.Sdk.slnx"
)


Get-ChildItem -Path "$BasePath\libraries" -Directory | ForEach-Object {
    $Path = $_.FullName + "\Assimalign.Cohesion." + $_.Name + ".slnx"
    $Solutions += $Path
}

Get-ChildItem -Path "$BasePath\resources" -Directory | ForEach-Object {
    $Path = $_.FullName + "\Assimalign.Cohesion." + $_.Name + ".slnx"
    $Solutions += $Path
}

$Solutions | ForEach-Object {
    $RootFolder = [System.IO.Directory]::GetParent($_).Name
    if ($_ -eq "$BasePath\Assimalign.Cohesion.slnx") {
        $RootFolder = "cohesion"
    }

    New-CohesionDotnetSolution `
        -SolutionPath $_ `
        -SolutionRootFolder $RootFolder `
        -IgnorePaths @(
            "$BasePath\_out"
            "$BasePath\.claude"
            "$BasePath\.vscode"
            "$BasePath\.dotnet-cli-home"
            "**\sdks\Assimalign.Cohesion.Sdk*\**\*.props"
            "**\sdks\Assimalign.Cohesion.Sdk*\**\*.targets"
            "**\build\Targets\**"
            "**\build\Build.props"
            "**\build\Build.targets"
        ) `
        -IncludeReferences `
        -Force `
        -Verbose
}

# New-CohesionDotnetSolution `
#         -SolutionPath "C:\Source\repos\assimalign\cohesion\resources\Web\Assimalign.Cohesion.Web.slnx" `
#         -IgnorePaths @(
#             "$BasePath\_out"
#             "$BasePath\.claude"
#             "$BasePath\.vscode"
#             "$BasePath\.dotnet-cli-home"
#             "**\sdks\Assimalign.Cohesion.Sdk*\**\*.props"
#             "**\sdks\Assimalign.Cohesion.Sdk*\**\*.targets"
#             "**\build\Targets\**"
#             "**\build\Build.props"
#             "**\build\Build.targets"
#         ) `
#         -IncludeReferences `
#         -Force `
#         -Verbose


