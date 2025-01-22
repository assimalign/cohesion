# $Content = "<Solution>"
# $Folders = @(
#     'Libraries'
# )
# $Folders | ForEach-Object {

#     $Content = $Content + [System.Environment]::NewLine
#     $Content = $Content + " <Folder Name=""/$_/"" />"

#     $Groupings = @{}

#     $Items = Get-ChildItem -Path "$PSScriptRoot/$_" -Include '*.csproj', '*.props', '*.targets', '*.md' -Exclude '*.nuget.g.props', '*.nuget.g.targets'  -Recurse
#     $Items | ForEach-Object {
#         $Path = $_.FullName.Replace("$PSScriptRoot/", "")

#         if ($_.Name.EndsWith('csproj')) {

#         }
#     }
# }


# $Content = $Content + [System.Environment]::NewLine
# $Content = $Content + "</Solution>"

# $Content


$Items = Get-ChildItem -Path ./services/security/Identity -Include *.csproj -Exclude *.Tests.csproj, *.Benchmarks.csproj -Recurse
$Items | ForEach-Object {
    $Folder = $_.Name.Replace(".csproj", "")
    $Source = $_.Directory.FullName + "\*"
    $Destination = "$PSScriptRoot\libraries\Identity\$Folder\src"

    New-Item "$PSScriptRoot/libraries/Identity/$Folder\src" -ItemType Directory
    New-Item "$PSScriptRoot/libraries/Identity/$Folder\tests" -ItemType Directory
    New-Item "$PSScriptRoot/libraries/Identity/$Folder\benchmarks" -ItemType Directory

    Copy-Item -Path $Source -Destination $Destination -Recurse
}