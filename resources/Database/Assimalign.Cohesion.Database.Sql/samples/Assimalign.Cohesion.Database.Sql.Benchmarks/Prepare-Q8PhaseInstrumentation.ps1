param(
    [Parameter(Mandatory = $true)]
    [string] $TreeRoot,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-zA-Z0-9_-]+$')]
    [string] $Label,
    [string] $OutputRoot = (Join-Path ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../..'))) '_out/b2'),
    [switch] $CopyCurrentHarness
)

$ErrorActionPreference = 'Stop'
$worktreeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../..'))
$ignoredRoot = [System.IO.Path]::GetFullPath((Join-Path $worktreeRoot '_out')).TrimEnd('\', '/')
$instrumentationRoot = [System.IO.Path]::GetFullPath($OutputRoot).TrimEnd('\', '/')
if ($instrumentationRoot -ne $ignoredRoot -and !$instrumentationRoot.StartsWith($ignoredRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be beneath the repository ignored _out directory: $ignoredRoot"
}
$archiveRoot = (Resolve-Path -LiteralPath $TreeRoot).Path.TrimEnd('\', '/')
if (!$archiveRoot.StartsWith($instrumentationRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "TreeRoot must be an archive beneath $instrumentationRoot; tracked source trees are refused."
}
if (Test-Path -LiteralPath (Join-Path $archiveRoot '.git')) {
    throw "TreeRoot must be an extracted source archive without .git: $archiveRoot"
}
$sourceRelative = 'resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/SqlDeclaredDatabase.cs'
$benchmarkRelative = 'resources/Database/Assimalign.Cohesion.Database.Sql/samples/Assimalign.Cohesion.Database.Sql.Benchmarks'
$sourcePath = Join-Path $archiveRoot $sourceRelative
$source = [System.IO.File]::ReadAllText($sourcePath).Replace("`r`n", "`n")

function Replace-ExactlyOnce([string] $Text, [string] $Before, [string] $After) {
    $first = $Text.IndexOf($Before, [System.StringComparison]::Ordinal)
    if ($first -lt 0 -or $Text.IndexOf($Before, $first + $Before.Length, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Expected exactly one instrumentation anchor: $Before"
    }
    return $Text.Substring(0, $first) + $After + $Text.Substring($first + $Before.Length)
}

$entry = @'
    internal async ValueTask ProvisionAsync(SqlDatabaseEngine engine, CancellationToken cancellationToken)
    {
'@
$entry = $entry.Replace("`r`n", "`n")
$source = Replace-ExactlyOnce $source $entry ($entry + "`n        long b2PhaseStarted = System.Diagnostics.Stopwatch.GetTimestamp();`n        long b2ApplyElapsed = 0;")

$opened = '        SqlDatabase database = await OpenOrCreateAsync(engine, cancellationToken).ConfigureAwait(false);'
$source = Replace-ExactlyOnce $source $opened ($opened + "`n        long b2PostOpenStarted = System.Diagnostics.Stopwatch.GetTimestamp();")

$apply = @'
            _result = Mode == SqlProvisioningMode.Verify
                ? await database.VerifySchemaAsync(Schema, cancellationToken).ConfigureAwait(false)
                : await database.ApplySchemaAsync(Schema, cancellationToken).ConfigureAwait(false);
'@
$apply = $apply.Replace("`r`n", "`n")
if ($source.Contains('database.SchemaProvisioner.ApplyAsync(Schema, cancellationToken, !RunMigrationsInline)')) {
    $apply = $apply.Replace('database.ApplySchemaAsync(Schema, cancellationToken)', 'database.SchemaProvisioner.ApplyAsync(Schema, cancellationToken, !RunMigrationsInline)')
}
$source = Replace-ExactlyOnce $source $apply ("            long b2ApplyStarted = System.Diagnostics.Stopwatch.GetTimestamp();`n" + $apply + "`n            b2ApplyElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - b2ApplyStarted;")

$end = @'
    }

    private async ValueTask<SqlDatabase> OpenOrCreateAsync(SqlDatabaseEngine engine, CancellationToken cancellationToken)
'@
$end = $end.Replace("`r`n", "`n")
$report = @'
        long b2PhaseEnded = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Schema is not null && string.Equals(Name.ToString(), "q8", StringComparison.OrdinalIgnoreCase))
        {
            // Benchmark-only copy: output occurs after all three timers have stopped.
            System.Console.WriteLine(System.FormattableString.Invariant(
                $"B2_PHASE6 already_applied={(_result!.Value.WasAlreadyApplied ? 1 : 0)} total_ticks={b2PhaseEnded - b2PhaseStarted} post_open_ticks={b2PhaseEnded - b2PostOpenStarted} apply_ticks={b2ApplyElapsed} frequency={System.Diagnostics.Stopwatch.Frequency}"));
        }

'@
$report = $report.Replace("`r`n", "`n")
$source = Replace-ExactlyOnce $source $end ($report + "`n" + $end)

$outputDirectory = Join-Path $instrumentationRoot "phase-timer/$Label"
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$instrumentedSource = Join-Path $outputDirectory 'SqlDeclaredDatabase.cs'
[System.IO.File]::WriteAllText($instrumentedSource, $source.Replace("`n", "`r`n"), [System.Text.UTF8Encoding]::new($false))

if ($CopyCurrentHarness) {
    foreach ($name in @('Program.cs', 'Q8Schema.cs', 'Assimalign.Cohesion.Database.Sql.Benchmarks.csproj')) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $worktreeRoot $benchmarkRelative) $name) -Destination (Join-Path (Join-Path $archiveRoot $benchmarkRelative) $name)
    }
}

$metadata = [ordered]@{
    TreeRoot = $archiveRoot
    Label = $Label
    OriginalSource = $sourcePath
    OriginalSourceSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    InstrumentedSource = $instrumentedSource
    InstrumentedSourceSha256 = (Get-FileHash -LiteralPath $instrumentedSource -Algorithm SHA256).Hash
    Targets = (Join-Path $PSScriptRoot 'Q8PhaseInstrumentation.targets')
    HarnessCopiedFromCurrentWorktree = [bool]$CopyCurrentHarness
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDirectory 'preparation.json') -Encoding utf8
$metadata | ConvertTo-Json

# Build build/Tasks in Release in the archive first, then publish the archived benchmark with:
# -p:CustomAfterMicrosoftCommonTargets=<absolute Q8PhaseInstrumentation.targets path>
# -p:B2PhaseTimerSource=<absolute generated SqlDeclaredDatabase.cs path>
# Both properties are global and reach Database.Sql through ProjectReference; the target ignores
# every other project. Archive bin/obj paths keep this assembly out of production test/pack objects.
# Each --cases Q8 --iterations 5 invocation emits seven B2_PHASE6 lines: initial apply (0),
# warmup already-applied Build (1), and five measured already-applied Builds (1). Retain exactly
# those final five, convert ticks / frequency * 1000 to milliseconds, and take their median.
# Run five alternating base/HEAD process pairs and report the minimum per-process median for
# each span, retaining all process medians. Full-Q8 numbers from these instrumented builds include
# Console.WriteLine overhead; use the existing uninstrumented campaign for full-Build comparison.
