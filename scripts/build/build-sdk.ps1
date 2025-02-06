param (
    [switch]$IsDebug,
    [switch]$IsRelease
)

$RepoRoot = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..")

function Get-BuildArgs {
    param (
        [string]$Command,
        [string]$Project,
        [string]$ProjectName
    )
    
    $Arguments = @()

    if ($null -ne $Command) { $Arguments += $Command }

    $Arguments += $Project
    $Arguments += "--output $RepoRoot\.out\$ProjectName"

    if ($IsRelease) { $Arguments += "--configuration Release" }
    elseif ($IsDebug) { $Arguments += "--configuration Debug" }

    return ([System.String]::Join(" ", $Arguments))
}
function Build-Libraries {

    

    Get-ChildItem -Path ([System.IO.Path]::GetFullPath("$PSScriptRoot\..\..\libraries")) -Directory |
    ForEach-Object {
        $Directory = $_.FullName

        Get-ChildItem -Path $Directory -Directory |
        ForEach-Object {
            $ProjectName = $_.Name

            Write-Host "$ProjectName" -ForegroundColor Cyan

            # Get the Project Path
            $ProjectPath = [System.IO.Path]::Combine(
                $_.FullName,
                "src",
                "$ProjectName.csproj"
            )

            # Get the Project Tests
            $ProjectTestPath = [System.IO.Path]::Combine(
                $_.FullName,
                "tests",
                "$ProjectName.Tests.csproj"
            )
            $BuildMessage = "    Build ".PadRight(20, ' ')
            Write-Host $BuildMessage -NoNewline

            $Build = Start-Process 'dotnet' `
                -ArgumentList (Get-BuildArgs -Command 'build' -Project $ProjectPath -ProjectName $ProjectName) `
                -NoNewWindow `
                -Wait `
                -PassThru
            
            # Check for build errors
            if ($Build.ExitCode -eq 1) {
                Write-Host " Error" -ForegroundColor Red
            } else {
                Write-Host " Succeeded" -ForegroundColor Green
            }

            $TestMessage = "    Test ".PadRight(20, ' ')
            Write-Host $TestMessage -NoNewline

            $Test = $Build = Start-Process 'dotnet' `
                -ArgumentList (Get-BuildArgs -Command 'test' -Project $ProjectTestPath -ProjectName $ProjectName) `
                -NoNewWindow `
                -Wait 

            # Check for test errors
            if ($Test.ExitCode -eq 1) {
                Write-Host " Error" -ForegroundColor Red
            } else {
                Write-Host " Succeeded" -ForegroundColor Green
            }

            Write-Host ""
        }
    }
}

function Build-Sdk {

}

Build-Libraries