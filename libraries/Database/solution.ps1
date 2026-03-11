
$PathBase = $PSScriptRoot
$Models = @(
    "Sql"
    "Documents"
    "Graph"
    "KeyValuePair"
    "Blob"
    "Cache"
)

$ModelGroups = $Models | ForEach-Object {
    $Group = $_
    $Paths = Get-ChildItem $PathBase | Where-Object {
        $_.Name -Like "Assimalign.Cohesion.Database.$Group*"
    } | ForEach-Object { return ($_.Name + "/") }
    return @{
        Folder = "Models/$Group"
        Paths  = $Paths
    }
}
$Excludes = $ModelGroups | ForEach-Object { $_.Paths }
$Other = @(@{
        Folder = "Core"
        Paths  = Get-ChildItem $PathBase | Where-Object {
            $Excludes.Contains(($_.Name + "/")) -eq $false
        } | ForEach-Object { return ($_.Name + "/") }
    })

$Groupings = $Other + $ModelGroups

New-CohesionDotnetSolution `
    -SolutionPath "C:\Source\repos\assimalign\cohesion\libraries\Database\Assimalign.Cohesion.Database.slnx" `
    -Force `
    -Grouping $Groupings
