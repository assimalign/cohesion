Import-Module Assimalign.Cohesion.DevScripts -Force


function Get-GroupingPathsLike {
    param(
        [string]$Path,
        [string] $Filter
    )

    $Items = (Get-ChildItem -Path $Path -Directory | Where-Object {
            $_.Name -like $Filter
        }).Name

    return $Items
}


$BasePath = $PSScriptRoot
$IgnorePaths = @(
    "$BasePath\_out"
    "**\.claude\worktrees\**"
    "$BasePath\.vscode"
    "$BasePath\.dotnet-cli-home"
    "**\sdks\Assimalign.Cohesion.Sdk*\**\*.props"
    "**\sdks\Assimalign.Cohesion.Sdk*\**\*.targets"
    "**\build\Targets\**"
    "**\build\Build.props"
    "**\build\Build.targets"
)
$Solutions = @(
    # "$BasePath\Assimalign.Cohesion.slnx",
    @{ Path = "$BasePath\frameworks\Assimalign.Cohesion.Frameworks.slnx" },
    @{ Path = "$BasePath\libraries\Assimalign.Cohesion.Libraries.slnx" },
    @{ Path = "$BasePath\resources\Assimalign.Cohesion.Resources.slnx" },
    @{ Path = "$BasePath\sdks\Assimalign.Cohesion.Sdk.slnx" }
)



Get-ChildItem -Path "$BasePath\libraries" -Directory | ForEach-Object {
    $Solutions += @{ Path = $_.FullName + "\Assimalign.Cohesion." + $_.Name + ".slnx" }
}

Get-ChildItem -Path "$BasePath\resources" -Directory | ForEach-Object {
    $FullPath = $_.FullName
    $Name = $_.Name
    switch ($Name) {
        "Database" {
            $Solutions += @{
                Path     = $FullPath + "\Assimalign.Cohesion." + $Name + ".slnx"
                Grouping = @(
                    @{ Folder = "Models/Sql"; Paths = Get-GroupingPathsLike -Path $FullPath -Filter 'Assimalign.Cohesion.Database.Sql*' }
                    @{ Folder = "Models/Blob"; Paths = Get-GroupingPathsLike -Path $FullPath -Filter 'Assimalign.Cohesion.Database.Blob*' }
                    @{ Folder = "Models/Documents"; Paths = Get-GroupingPathsLike -Path $FullPath -Filter 'Assimalign.Cohesion.Database.Documents*' }
                    @{ Folder = "Models/Graph"; Paths = Get-GroupingPathsLike -Path $FullPath -Filter 'Assimalign.Cohesion.Database.Graph*' }
                    @{ Folder = "Models/KeyValuePair"; Paths = Get-GroupingPathsLike -Path $FullPath -Filter 'Assimalign.Cohesion.Database.KeyValuePair*' }
                    @{ Folder = "Models/Cache"; Paths = Get-GroupingPathsLike -Path $FullPath -Filter 'Assimalign.Cohesion.Database.Cache*' }
                    @{ Folder = "Core"; Paths = (Get-ChildItem -Path $FullPath -Directory |  Where-Object {
                            $_.Name -notlike 'Assimalign.Cohesion.Database.Sql*' -and
                            $_.Name -notlike 'Assimalign.Cohesion.Database.Blob*' -and
                            $_.Name -notlike 'Assimalign.Cohesion.Database.Documents*' -and
                            $_.Name -notlike 'Assimalign.Cohesion.Database.Graph*' -and
                            $_.Name -notlike 'Assimalign.Cohesion.Database.KeyValuePair*' -and
                            $_.Name -notlike 'Assimalign.Cohesion.Database.Cache*'
                        }).Name
                    }
                )
            }
            break
        }

        Default {
            $Solutions += @{ Path = $FullPath + "\Assimalign.Cohesion." + $Name + ".slnx" }
            break
        }
    }
}

$Solutions | ForEach-Object {
    $RootFolder = [System.IO.Directory]::GetParent($_.Path).Name
    if ($_.Path -eq "$BasePath\Assimalign.Cohesion.slnx") {
        $RootFolder = "cohesion"
    }

    if ($_.Grouping.Length -gt 0) {
        New-CohesionDotnetSolution `
            -SolutionPath $_.Path `
            -SolutionRootFolder $RootFolder `
            -IgnorePaths $IgnorePaths `
            -Grouping $_.Grouping `
            -IncludeReferences `
            -Force `
            -Verbose
    }
    else {
        New-CohesionDotnetSolution `
            -SolutionPath $_.Path `
            -SolutionRootFolder $RootFolder `
            -IgnorePaths $IgnorePaths `
            -IncludeReferences `
            -Force `
            -Verbose
    }
}