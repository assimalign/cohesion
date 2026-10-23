#Requires -Version 7.0
<#
.SYNOPSIS
    Updates repository solutions without a compiled PowerShell module.
.DESCRIPTION
    Preserves existing solution folders and file items, removes missing entries, adds projects
    beneath each solution, and includes their transitive project references. Package-only SDK
    consumers, template content, build output, and the unshipped Database Studio are excluded.
    References are read from XML; MSBuild expressions are not evaluated.
.PARAMETER SolutionPath
    Solutions to update, relative to RepositoryRoot or absolute. Defaults to every repository solution.
.PARAMETER Check
    Reports drift without writing and exits with code 1 when updates are needed.
.EXAMPLE
    pwsh scripts/Update-CohesionSolutions.ps1 -SolutionPath fixtures/Assimalign.Cohesion.Fixtures.slnx
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string[]] $SolutionPath,
    [switch] $Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$repositoryPrefix = $RepositoryRoot.TrimEnd('/', '\') + [IO.Path]::DirectorySeparatorChar

function Get-SolutionInput {
    param([string] $Directory)
    foreach ($entry in Get-ChildItem -LiteralPath $Directory) {
        if ($entry.PSIsContainer) {
            if ($entry.Name -in @('bin', 'obj', '_out', 'node_modules', 'SdkSmoke', 'content',
                    'Assimalign.Cohesion.Database.Studio') -or $entry.Name.StartsWith('.') -or
                ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
            Get-SolutionInput -Directory $entry.FullName
        }
        elseif ($entry.Extension -in @('.csproj', '.slnx')) { $entry }
    }
}

$inputs = @(Get-SolutionInput -Directory $RepositoryRoot)
$projects = @($inputs | Where-Object Extension -EQ '.csproj' | Sort-Object FullName)
$projectIndex = @{}
foreach ($project in $projects) {
    if (-not $projectIndex.ContainsKey($project.BaseName)) { $projectIndex[$project.BaseName] = $project.FullName }
}
if (-not $SolutionPath) {
    $SolutionPath = @($inputs | Where-Object Extension -EQ '.slnx' | Sort-Object FullName | ForEach-Object FullName)
}

function Add-SolutionEntry {
    param([xml] $Document, [string] $Kind, [string] $Path, [string] $FolderName)
    $folder = @($Document.DocumentElement.SelectNodes('Folder') | Where-Object { $_.GetAttribute('Name') -eq $FolderName })
    if ($folder.Count -eq 0) {
        $node = $Document.CreateElement('Folder')
        $node.SetAttribute('Name', $FolderName)
        [void]$Document.DocumentElement.AppendChild($node)
    }
    else { $node = $folder[0] }
    $entry = $Document.CreateElement($Kind)
    $entry.SetAttribute('Path', $Path)
    [void]$node.AppendChild($entry)
}

$hasDrift = $false
foreach ($solution in $SolutionPath) {
    $fullPath = [IO.Path]::GetFullPath($solution, $RepositoryRoot)
    if (-not $fullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Solution must be inside RepositoryRoot: $fullPath"
    }
    $directory = Split-Path -Parent $fullPath
    $prefix = $directory + [IO.Path]::DirectorySeparatorChar
    $folderRoot = '/' + (Split-Path -Leaf $directory) + '/'
    [xml]$document = Get-Content -LiteralPath $fullPath -Raw
    $changed = $false
    $included = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($document.SelectNodes('/Solution//Project | /Solution//File'))) {
        $entryPath = [IO.Path]::GetFullPath((Join-Path $directory $entry.GetAttribute('Path')))
        if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
            [void]$entry.ParentNode.RemoveChild($entry)
            $changed = $true
        }
        elseif ($entry.Name -eq 'Project') { [void]$included.Add($entryPath) }
    }
    $queue = [Collections.Generic.Queue[string]]::new()
    foreach ($project in $projects) {
        if ($project.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $queue.Enqueue($project.FullName)
        }
    }
    # Retained projects outside the solution directory also contribute their dependency closure.
    foreach ($project in $included) { $queue.Enqueue($project) }
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($queue.Count -gt 0) {
        $project = $queue.Dequeue()
        if (-not $visited.Add($project)) { continue }
        if ($included.Add($project)) {
            $relative = [IO.Path]::GetRelativePath($directory, $project).Replace('\', '/')
            $folderName = if ($project.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                $folderRoot + [IO.Path]::GetDirectoryName($relative).Replace('\', '/').Trim('/') + '/'
            }
            else { $folderRoot + '_Dependencies/' }
            Add-SolutionEntry $document 'Project' $relative $folderName
            $changed = $true
        }
        [xml]$projectXml = Get-Content -LiteralPath $project -Raw
        foreach ($reference in $projectXml.SelectNodes('/Project/ItemGroup/*')) {
            $name = $reference.GetAttribute('Include')
            if (-not $name -or $name -match '[$@*;]') { continue }
            if ($reference.Name -in @('CohesionProjectReference', 'CohesionPrivateProjectReference',
                    'CohesionAnalyzerReference', 'CohesionAnalyzerAsProjectReference', 'CohesionSharedSource')) {
                if ($projectIndex.ContainsKey($name)) { $queue.Enqueue($projectIndex[$name]) }
            }
            elseif ($reference.Name -eq 'ProjectReference') {
                $referencePath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $project) $name))
                if ($referencePath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
                    (Test-Path -LiteralPath $referencePath -PathType Leaf)) { $queue.Enqueue($referencePath) }
            }
        }
    }
    foreach ($folder in @($document.SelectNodes('/Solution/Folder[not(*)]'))) {
        [void]$folder.ParentNode.RemoveChild($folder)
        $changed = $true
    }
    if (-not $changed) { continue }
    $hasDrift = $true
    Write-Host "Solution needs update: $([IO.Path]::GetRelativePath($RepositoryRoot, $fullPath))"
    if (-not $Check -and $PSCmdlet.ShouldProcess($fullPath, 'Update solution entries')) {
        $settings = [Xml.XmlWriterSettings]::new()
        $settings.Indent = $true
        $settings.IndentChars = '  '
        $settings.OmitXmlDeclaration = $true
        $settings.Encoding = [Text.UTF8Encoding]::new($false)
        $writer = [Xml.XmlWriter]::Create($fullPath, $settings)
        try { $document.Save($writer) } finally { $writer.Dispose() }
    }
}
if ($Check -and $hasDrift) { exit 1 }
