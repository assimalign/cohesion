#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

BeforeAll {
    $consumerSettingsScript = Join-Path $PSScriptRoot '../sdks/New-SdkConsumerGlobalJson.ps1'
}

Describe 'Isolated SDK consumer settings' {
    BeforeEach {
        $repository = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $feed = Join-Path $repository 'feed'
        $output = Join-Path $repository 'consumer'
        $version = '10.0.0-preview.1'
        $sdkIds = @(
            'Assimalign.Cohesion.Sdk'
            'Assimalign.Cohesion.Sdk.Gateway'
            'Assimalign.Cohesion.Sdk.Web'
            'Assimalign.Cohesion.Sdk.Database'
        )
        New-Item -ItemType Directory -Path $feed -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $repository 'global.json') `
            -Value '{"sdk":{"version":"10.0.300","rollForward":"latestFeature"}}'
        foreach ($sdkId in $sdkIds) {
            Set-Content -LiteralPath (Join-Path $feed "$sdkId.$version.nupkg") -Value ''
        }
    }

    It 'pins the Gateway consumer from the feed when repository Cohesion pins are absent' {
        & $consumerSettingsScript -RepositoryDirectory $repository -PackageDirectory $feed `
            -Version $version -SdkPackageId $sdkIds -OutputDirectory $output

        $settings = Get-Content -LiteralPath (Join-Path $output 'global.json') -Raw | ConvertFrom-Json
        $settings.sdk.version | Should -Be '10.0.300'
        $settings.sdk.rollForward | Should -Be 'latestFeature'
        @($settings.'msbuild-sdks'.PSObject.Properties).Count | Should -Be 4
        foreach ($sdkId in $sdkIds) {
            $settings.'msbuild-sdks'.$sdkId | Should -Be $version
        }
    }

    It 'rejects an unavailable SDK version before writing consumer settings' {
        Remove-Item -LiteralPath (Join-Path $feed "Assimalign.Cohesion.Sdk.Gateway.$version.nupkg")

        { & $consumerSettingsScript -RepositoryDirectory $repository -PackageDirectory $feed `
            -Version $version -SdkPackageId $sdkIds -OutputDirectory $output } |
            Should -Throw '*Assimalign.Cohesion.Sdk.Gateway*was not produced*'
        Test-Path -LiteralPath (Join-Path $output 'global.json') | Should -BeFalse
    }
}
