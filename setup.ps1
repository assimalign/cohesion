$BasePath = $PSScriptRoot

Import-Module Assimalign.Cohesion.DevScripts -Force

$Solutions = @(
    "$BasePath\Assimalign.Cohesion.slnx" ,
    "$BasePath\frameworks\Assimalign.Cohesion.Frameworks.slnx"
)

$Solutions | ForEach-Object {

    New-CohesionDotnetSolution `
        -SolutionPath $_ `
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
        -Force `
        -Verbose
}
