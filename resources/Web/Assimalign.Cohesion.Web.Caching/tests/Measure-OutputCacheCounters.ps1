# Measures the actual middleware with and without the rejected hit/miss counter prototype.
# Run from a clean editing session; both production files are restored byte-for-byte in finally.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$middlewarePath = Join-Path $PSScriptRoot '../src/Internal/OutputCacheMiddleware.cs'
$sourcePath = Join-Path $PSScriptRoot '../src/Internal/EventSource/OutputCacheEventSource.cs'
$projectPath = Join-Path $PSScriptRoot 'Assimalign.Cohesion.Web.Caching.Tests.csproj'
$middlewareBytes = [IO.File]::ReadAllBytes($middlewarePath)
$sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
$middleware = [IO.File]::ReadAllText($middlewarePath)
$source = [IO.File]::ReadAllText($sourcePath)
$priorBenchmark = $env:COHESION_CACHE_COUNTER_BENCHMARK
$priorVariant = $env:COHESION_CACHE_COUNTER_VARIANT
$priorTiered = $env:DOTNET_TieredCompilation
$encoding = [Text.UTF8Encoding]::new($false)

function Replace-Exact([string] $content, [string] $marker, [string] $replacement) {
    if (-not $content.Contains($marker)) { throw "Counter prototype no longer matches marker: $marker" }
    return $content.Replace($marker, $replacement)
}

$candidateSource = Replace-Exact $source '    private long _declinedEntries;' @'
    private long _cacheHits;
    private long _cacheMisses;
    private long _declinedEntries;
'@
$candidateSource = Replace-Exact $candidateSource '    private PollingCounter? _declinedEntriesCounter;' @'
    private PollingCounter? _cacheHitsCounter;
    private PollingCounter? _cacheMissesCounter;
    private PollingCounter? _declinedEntriesCounter;
'@
$candidateSource = Replace-Exact $candidateSource '    internal long DeclinedEntries' @'
    internal long CacheHits => Interlocked.Read(ref _cacheHits);
    internal long CacheMisses => Interlocked.Read(ref _cacheMisses);

    [NonEvent]
    public void CacheHit() => Interlocked.Increment(ref _cacheHits);

    [NonEvent]
    public void CacheMiss() => Interlocked.Increment(ref _cacheMisses);

    internal long DeclinedEntries
'@
$candidateSource = Replace-Exact $candidateSource '            _declinedEntriesCounter ??=' @'
            _cacheHitsCounter ??= new PollingCounter("cache-hits", this, () => CacheHits) { DisplayName = "Output cache hits" };
            _cacheMissesCounter ??= new PollingCounter("cache-misses", this, () => CacheMisses) { DisplayName = "Output cache misses" };
            _declinedEntriesCounter ??=
'@
$candidateMiddleware = Replace-Exact $middleware '            await ServeAsync(context, hit).ConfigureAwait(false);' @'
            OutputCacheEventSource.Log.CacheHit();
            await ServeAsync(context, hit).ConfigureAwait(false);
'@
$candidateMiddleware = Replace-Exact $candidateMiddleware '        // A custom server may omit successful-completion notification.' @'
        OutputCacheEventSource.Log.CacheMiss();

        // A custom server may omit successful-completion notification.
'@

Push-Location $repository
try {
    $env:COHESION_CACHE_COUNTER_BENCHMARK = '1'
    $env:DOTNET_TieredCompilation = '0'
    dotnet build build/Tasks -c Release -m:1 -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Release build tasks failed.' }

    foreach ($variant in @('baseline-1', 'candidate-1', 'baseline-2', 'candidate-2')) {
        $env:COHESION_CACHE_COUNTER_VARIANT = $variant
        if ($variant.StartsWith('candidate')) {
            [IO.File]::WriteAllText($sourcePath, ($candidateSource -replace '(?<!\r)\n', "`r`n"), $encoding)
            [IO.File]::WriteAllText($middlewarePath, ($candidateMiddleware -replace '(?<!\r)\n', "`r`n"), $encoding)
        }
        else {
            [IO.File]::WriteAllBytes($sourcePath, $sourceBytes)
            [IO.File]::WriteAllBytes($middlewarePath, $middlewareBytes)
        }
        dotnet test $projectPath -c Release -m:1 --filter FullyQualifiedName~OutputCacheCounterBenchmarkTests --logger 'console;verbosity=detailed'
        if ($LASTEXITCODE -ne 0) { throw "Benchmark $variant failed." }
    }
}
finally {
    [IO.File]::WriteAllBytes($sourcePath, $sourceBytes)
    [IO.File]::WriteAllBytes($middlewarePath, $middlewareBytes)
    $env:COHESION_CACHE_COUNTER_BENCHMARK = $priorBenchmark
    $env:COHESION_CACHE_COUNTER_VARIANT = $priorVariant
    $env:DOTNET_TieredCompilation = $priorTiered
    Pop-Location
}

# Keep --no-build consumers on the restored baseline after a successful measurement.
# If measurement failed, rebuild normally before using --no-build.
dotnet build $projectPath -c Release -m:1 --no-restore -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Rebuilding the restored baseline failed.' }
