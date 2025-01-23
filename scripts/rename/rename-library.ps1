[CmdletBinding()]
Param(
    [Parameter(Mandatory = $true)]
    [string]$Service,

    [Parameter(Mandatory = $true)]
    [string]$NewName,

    [Parameter(Mandatory = $true)]
    [string]$OldName
)

# Capture current location
$MyLocation = Get-Location


$Path = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..\libraries")

$ProjectName = $OldName.Replace(".csproj", "")

$ProjectRelativePath = [System.IO.Path]::Combine($ProjectName, "src", "$ProjectName.csproj")
$ProjectAbsolutePath = [System.IO.Path]::Combine($Path, $Service, $ProjectRelativePath)
$ProjectRelativeReferenceL1 = "..\..\" + $ProjectRelativePath
$ProjectRelativeReferenceL2 = "..\" + $ProjectRelativeReferenceL1
$ProjectRelativeReferenceL3 = "..\" + $ProjectRelativeReferenceL2
$ProjectRelativeReferenceL4 = "..\" + $ProjectRelativeReferenceL3

$ProjectAbsolutePath
$ProjectRelativePath
$ProjectRelativeReferenceL1
$ProjectRelativeReferenceL2
$ProjectRelativeReferenceL3
$ProjectRelativeReferenceL4

if (-not (Test-Path $ProjectAbsolutePath )) {
    Write-Error "Project $Name does not exists"
    return
}

$Content = Get-Content $ProjectAbsolutePath -Raw

$Start = $Content.IndexOf("<RootNamespace>")
$End = $Content.IndexOf("</RootNamespace>")

$Namespace = $ProjectName

if ($Start -lt $End) {
    $Namespace = $Content.Substring($Start + 15, ($End - $Start - 15))
}

$Namespace
# Capture all projects and 
# Get-ChildItem -Path $Path -Include '*.csproj' -Recurse -File | 
# ForEach-Object {
#     if ($_.FullName.EndsWith($OldName, [System.StringComparison]::OrdinalIgnoreCase )) {
#         return
#     }

#     $Content = Get-Content $_.FullName -Raw

#     if ($Content.Contains("$OldName.csproj")) {
#         $_.FullName
#     }


# }

