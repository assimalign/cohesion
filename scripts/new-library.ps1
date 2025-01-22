param (
    [Parameter(Mandatory = $true)]
    [string]$Name,
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        'ApiManager',
        'ConfigurationStore',
        'Core',
        'Database',
        'Dns',
        'EventHub',
        'Identity',
        'IoTHub',
        'LoadBalancer',
        'LogSpace',
        'MessageHub',
        'NatGateway',
        'Net',
        'NotificationHub',
        'OpenTelemetry',
        'SecretStore',
        'Web'
    )]
    [string]$Service
)

Set-Location $PSScriptRoot

Start-Process dotnet -NoNewWindow -Wait -ArgumentList "new classlib -n $Name -o ../libraries/$Service/$Name/src"
Start-Process dotnet -NoNewWindow -Wait -ArgumentList "new xunit -n $Name.Tests -o ../libraries/$Service/$Name/tests"
Start-Process dotnet -NoNewWindow -Wait -ArgumentList "new console -n $Name.Benchmarks -o ../libraries/$Service/$Name/benchmarks"
Start-Process dotnet -NoNewWindow -Wait -ArgumentList "new sln --output ../libraries/$Service/$Name"

Set-Location "../libraries/$Service/$Name"

Start-Process dotnet -NoNewWindow -Wait -ArgumentList "sln $Name.sln add (ls -r **\*.csproj)"
