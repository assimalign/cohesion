# SQL benchmark harness

This executable measures the SQL cases in the engine extensibility design's section 10. It uses
public engine APIs and supports Release JIT runs and NativeAOT publishing. Build `build/Tasks`
in the selected configuration before using the harness in a fresh worktree.

For the provisioning case, run from the repository root:

```powershell
$env:MSBUILDDISABLENODEREUSE = '1'
dotnet run --project resources/Database/Assimalign.Cohesion.Database.Sql/samples/Assimalign.Cohesion.Database.Sql.Benchmarks/Assimalign.Cohesion.Database.Sql.Benchmarks.csproj -c Release -- --cases Q8 --rows 1 --iterations 5 --label local
```

`--rows` controls the statement cases' setup and does not change Q8's 50-table schema. Each case
runs one unreported warm-up followed by `--iterations` measured samples. The output reports their
individual wall times, median, best, process CPU times, allocations, and GC collection counts
and pause durations. A best-of-five-medians campaign runs this command five times and takes the
smallest reported `median_ns`, retaining all five reports and the exact command lines.

Q8 creates a file-backed database with 50 tables before measurement. Each measured Q8 sample is
the complete engine `BuildAsync`: declare and compile the schema, create the engine, open and
recover the database, bind its persisted definitions, and provision the already-applied schema.
Engine disposal is outside the timed region.

Q8 also reports Q8P separately. Q8P times `SqlDatabase.ApplySchemaAsync` immediately after each
Build, over that already-open database and the same precompiled schema reused across iterations.
Its schema has the same canonical identity as the declaration passed to Build. Compilation and
initial hash computation happen before measurement; Q8P excludes engine creation, opening,
recovery, and binding. It exercises the same provisioning entry point that Build uses, but its
reported duration is an isolated already-applied apply measurement, not a timed segment captured
inside Build. Q8 remains the complete Build measurement. Each report keeps its own GC counts.

`--cases Q8C` adds an immutable registered-function CHECK to every table and reports the
corresponding Q8CP apply measurement. `SqlBenchmarkBaseline=true` compiles out the E2-only
function cases so the shared harness can also run against pre-E2 baselines; B2's base already
contains E2 and does not need that property.

## B2 measurements (#1355, 2026-10-11)

Release NativeAOT, `win-arm64`, .NET SDK 10.0.401, Windows 10.0.28000.0, 12 logical CPUs,
workstation GC. The base engine was `2bf42d94b3a200662fb7e22c54f55607d9af4b94`; its binary was
published before the B2 implementation edits, with the same Q8/Q8P measurement regions added to
the harness. The B2 binary contains commits `3df67c16c` and `a7a20570f` plus this harness change.
The later GC-reporting correction restores each sample's own counters and changes neither timed
region. No branch switch or separate worktree was used.

The first campaign measured the base before implementation and B2 after validation. Full-Build
timing moved with host load, so a second campaign alternated the retained base binary and B2
binary five times. Each invocation has one warm-up and five measured iterations. Both campaigns
are retained below for the isolated public apply and complete Build comparisons.
These are measured elapsed times, not a claim that the host was otherwise idle.

| Campaign | Case | Base best median (ms) | B2 best median (ms) |
|---|---|---:|---:|
| Initial, separated | Q8P isolated apply | 0.0595 | 0.0046 |
| Initial, separated | Q8 complete Build | 7.5989 | 9.7058 |
| Alternating | Q8P isolated apply | 0.1063 | **0.0027** |
| Alternating | Q8 complete Build | 10.2314 | 9.7064 |

All five alternating medians, in invocation order, in milliseconds:

| Case | Base | B2 |
|---|---|---|
| Q8P | 0.1697, 0.1088, 0.1553, 0.1366, 0.1063 | 0.0027, 0.0045, 0.0072, 0.0074, 0.0060 |
| Q8 | 10.2314, 10.3892, 10.6509, 11.7185, 10.8545 | 10.0135, 9.7064, 10.2450, 10.6881, 11.8219 |

Q8P's per-call median allocation was 44,376 bytes at the base and 608 bytes in B2, including
harness overhead. Q8P is the isolated public apply measurement described above; Q8 reports
complete Build separately. Q8P alone does not establish the gate for provisioning inside Build.
Both `Sql.AotSample` and `Sql.Benchmarks` published in Release with **zero IL2xxx/IL3xxx warnings**.

Exact publication commands, run from `C:\cw\b2` (the first before implementation edits):

```powershell
$env:MSBUILDDISABLENODEREUSE='1'
$env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
dotnet publish resources/Database/Assimalign.Cohesion.Database.Sql/samples/Assimalign.Cohesion.Database.Sql.Benchmarks/Assimalign.Cohesion.Database.Sql.Benchmarks.csproj -c Release -r win-arm64 -o C:/cw/b2/_out/b2/baseline
dotnet publish resources/Database/Assimalign.Cohesion.Database.Sql/samples/Assimalign.Cohesion.Database.Sql.AotSample/Assimalign.Cohesion.Database.Sql.AotSample.csproj -c Release -r win-arm64 -o C:/cw/b2/_out/b2/aot-sample
dotnet publish resources/Database/Assimalign.Cohesion.Database.Sql/samples/Assimalign.Cohesion.Database.Sql.Benchmarks/Assimalign.Cohesion.Database.Sql.Benchmarks.csproj -c Release -r win-arm64 -o C:/cw/b2/_out/b2/after
```

Exact measurement invocations (initial campaigns, then the alternating campaign):

```powershell
1..5 | ForEach-Object { & C:/cw/b2/_out/b2/baseline/Assimalign.Cohesion.Database.Sql.Benchmarks.exe --cases Q8 --rows 0 --iterations 5 --label "base-$_" }
1..5 | ForEach-Object { & C:/cw/b2/_out/b2/after/Assimalign.Cohesion.Database.Sql.Benchmarks.exe --cases Q8 --rows 0 --iterations 5 --label "b2-$_" }
1..5 | ForEach-Object {
    & C:/cw/b2/_out/b2/baseline/Assimalign.Cohesion.Database.Sql.Benchmarks.exe --cases Q8 --rows 0 --iterations 5 --label "base-paired-$_"
    & C:/cw/b2/_out/b2/after/Assimalign.Cohesion.Database.Sql.Benchmarks.exe --cases Q8 --rows 0 --iterations 5 --label "b2-paired-$_"
}
```

The worktree logs are `_out/b2/q8-before.log`, `q8-after.log`, `q8-before-paired.log` and
`q8-after-paired.log`; publication logs are `baseline-publish.log`, `publish-AotSample-Release.log`
and `publish-Benchmarks-Release.log` in that directory.

## Direct measurements inside Build

A third campaign measured three spans inside `SqlDeclaredDatabase.ProvisionAsync`, using source
archives of base `2bf42d94b3a200662fb7e22c54f55607d9af4b94` and B2
`48aa69372d8d19960c36ec073e036507abcf8304`. Both archives used the same current harness. The
benchmark-only source replacement adds timestamps around the actual Build path; it changes no
production source and uses each archive's own `bin` and `obj` directories.

| Span inside Build | Base best median (ms) | B2 best median (ms) |
|---|---:|---:|
| ApplySchemaAsync reconciliation | 0.3881 | 0.0065 |
| After OpenOrCreateAsync through validation and reconciliation | 0.4004 | 0.0079 |
| Complete ProvisionAsync, including OpenOrCreateAsync | 7.6384 | 6.4633 |

All five paired invocation medians, in milliseconds, from `_out/b2/q8-phase-results.json`:

| Round | Base apply | B2 apply | Base post-open | B2 post-open | Base complete phase | B2 complete phase |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 0.6286 | 0.0096 | 0.6507 | 0.0109 | 12.7268 | 6.4633 |
| 2 | 0.3881 | 0.0076 | 0.4004 | 0.0088 | 22.2186 | 23.2200 |
| 3 | 0.6937 | 0.0070 | 0.7158 | 0.0083 | 20.1354 | 7.1198 |
| 4 | 0.7069 | 0.0089 | 0.7308 | 0.0107 | 21.6820 | 7.3962 |
| 5 | 0.4754 | 0.0065 | 0.4893 | 0.0079 | 7.6384 | 8.7716 |

The reconciliation and post-open spans are under 1 ms. The owner clarified on 2026-10-11 that
B2's Q8 gate is provisioning reconciliation, so it is met. The complete phase, including database
opening, recovery, and binding, is measured above as a separate budget.

The instrumentation stops all three timers before printing its `B2_PHASE6` line. That printing
still occurs inside the harness's surrounding Q8 Build timer, so instrumented Q8 totals include
console overhead. Use the original uninstrumented Q8 numbers above for complete Build comparisons.
The initial provision emits `already_applied=0`; the following warm-up and five measured Builds
emit six `already_applied=1` lines. Filter to those six lines, discard the first as warm-up, convert
ticks to milliseconds using the emitted frequency, and take the middle sorted value of the five.
The best-of-five-medians value is the minimum of the five invocation medians for each span.

### Reproduce the direct campaign

Run from `C:\cw\b2` with fresh archive directories beneath this worktree's ignored `_out/b2`.
These commands archive the two revisions without switching branches or creating other worktrees,
copy the shared current harness, generate the instrumentation, bootstrap the Release build tasks,
and publish both NativeAOT executables with the benchmark-only compile-item replacement.

The recorded publication runner used `_out/b2/Q8PhaseInstrumentation.targets`, byte-identical to
the tracked target beside this README. The tracked preparation helper adds path guards for its
permanent location; its generated base and B2 C# copies were verified byte-identical to the ones
used in this campaign. The commands below copy that target to the recorded path and use the
tracked helper. The environment and publish arguments match `_out/b2/run-phase-publish.ps1`.

```powershell
Set-Location C:/cw/b2
$ErrorActionPreference = 'Stop'
$env:MSBUILDDISABLENODEREUSE='1'
$env:NUGET_PACKAGES='C:\cw\b2\_out\b2\nuget-packages'
$env:NUGET_HTTP_CACHE_PATH='C:\cw\b2\_out\b2\nuget-http-cache'
$env:NUGET_SCRATCH='C:\cw\b2\_out\b2\nuget-scratch'
$env:DOTNET_CLI_HOME='C:\cw\b2\_out\b2\dotnet-home'
$env:TEMP='C:\cw\b2\_out\b2\temp'
$env:TMP=$env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
$benchmark='resources/Database/Assimalign.Cohesion.Database.Sql/samples/Assimalign.Cohesion.Database.Sql.Benchmarks'
$project="$benchmark/Assimalign.Cohesion.Database.Sql.Benchmarks.csproj"
foreach ($directory in @('_out/b2', $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_SCRATCH, $env:DOTNET_CLI_HOME, $env:TEMP)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
git archive --format=tar --output=C:/cw/b2/_out/b2/base-source.tar 2bf42d94b3a200662fb7e22c54f55607d9af4b94
if ($LASTEXITCODE -ne 0) { throw 'Base archive failed' }
git archive --format=tar --output=C:/cw/b2/_out/b2/b2-source.tar 48aa69372d8d19960c36ec073e036507abcf8304
if ($LASTEXITCODE -ne 0) { throw 'B2 archive failed' }
New-Item -ItemType Directory -Path _out/b2/base-source | Out-Null
tar -xf C:/cw/b2/_out/b2/base-source.tar -C C:/cw/b2/_out/b2/base-source
if ($LASTEXITCODE -ne 0) { throw 'Base extraction failed' }
New-Item -ItemType Directory -Path _out/b2/b2-source | Out-Null
tar -xf C:/cw/b2/_out/b2/b2-source.tar -C C:/cw/b2/_out/b2/b2-source
if ($LASTEXITCODE -ne 0) { throw 'B2 extraction failed' }
Copy-Item -LiteralPath "$benchmark/Q8PhaseInstrumentation.targets" -Destination _out/b2/Q8PhaseInstrumentation.targets
foreach ($label in @('base','b2')) {
    & "$benchmark/Prepare-Q8PhaseInstrumentation.ps1" -TreeRoot "C:/cw/b2/_out/b2/$label-source" -Label $label -OutputRoot C:/cw/b2/_out/b2 -CopyCurrentHarness
    Push-Location "C:/cw/b2/_out/b2/$label-source"
    try {
        dotnet build build/Tasks/Assimalign.Cohesion.Build.Tasks.csproj -c Release *> "C:/cw/b2/_out/b2/bootstrap-phase-$label.log"
        if ($LASTEXITCODE -ne 0) { throw "Release bootstrap failed: $label" }
        dotnet publish $project -c Release -r win-arm64 -o "C:/cw/b2/_out/b2/phase-$label" '-p:CustomAfterMicrosoftCommonTargets=C:/cw/b2/_out/b2/Q8PhaseInstrumentation.targets' "-p:B2PhaseTimerSource=C:/cw/b2/_out/b2/phase-timer/$label/SqlDeclaredDatabase.cs" *> "C:/cw/b2/_out/b2/publish-phase-$label.log"
        if ($LASTEXITCODE -ne 0) { throw "NativeAOT publish failed: $label" }
        if (Select-String -Path "C:/cw/b2/_out/b2/publish-phase-$label.log" -Pattern 'warning IL[23][0-9]{3}') { throw 'IL warning in instrumented harness' }
    }
    finally { Pop-Location }
}
```

The exact paired measurement and median extraction from `_out/b2/run-phase-measure.ps1`:

```powershell
$ErrorActionPreference = 'Stop'
$env:TEMP='C:\cw\b2\_out\b2\temp'
$env:TMP=$env:TEMP
$results = @()
foreach ($round in 1..5) {
    foreach ($label in @('base','b2')) {
        $log="C:/cw/b2/_out/b2/q8-phase-$label-$round.log"
        & "C:/cw/b2/_out/b2/phase-$label/Assimalign.Cohesion.Database.Sql.Benchmarks.exe" --cases Q8 --rows 0 --iterations 5 --label "$label-phase-$round" *> $log
        if ($LASTEXITCODE -ne 0) { Get-Content $log -Tail 10; throw 'Phase benchmark failed' }
        $measurements = @(Get-Content $log | Where-Object { $_ -match '^B2_PHASE6 already_applied=1 total_ticks=(\d+) post_open_ticks=(\d+) apply_ticks=(\d+) frequency=(\d+)$' } | ForEach-Object {
            [pscustomobject]@{Total=[double]$Matches[1]*1000/[double]$Matches[4]; PostOpen=[double]$Matches[2]*1000/[double]$Matches[4]; Apply=[double]$Matches[3]*1000/[double]$Matches[4]}
        })
        if ($measurements.Count -ne 6) { throw "Expected 6 already-applied records, got $($measurements.Count) in $log" }
        $samples=@($measurements | Select-Object -Skip 1)
        $result=[pscustomobject]@{Label=$label;Round=$round;TotalMedianMs=($samples.Total | Sort-Object)[2];PostOpenMedianMs=($samples.PostOpen | Sort-Object)[2];ApplyMedianMs=($samples.Apply | Sort-Object)[2]}
        $results += $result
        $result | ConvertTo-Json -Compress
    }
}
$results | ConvertTo-Json | Set-Content -LiteralPath C:/cw/b2/_out/b2/q8-phase-results.json
```

The direct publication logs are `_out/b2/publish-phase-base.log` and `publish-phase-b2.log`;
the ten per-process logs are `q8-phase-{base,b2}-{1..5}.log` in the same directory. Both direct
NativeAOT publications also reported zero IL2xxx/IL3xxx warnings.

## Review-fix verification (2026-10-11)

A fresh Release NativeAOT campaign measured review revision
`da23b378fbaf346ad02234299cf1e2d2fff5d906` against the retained base executables from
`2bf42d94b3a200662fb7e22c54f55607d9af4b94`. The review revision was archived below
`_out/b2-review/src`, so its build outputs were isolated from the main worktree's concurrent test
gates. The archived base and review `Program.cs`, `Q8Schema.cs` and benchmark project file were
byte-identical. This used .NET SDK 10.0.401, `win-arm64`, workstation GC, Windows 10.0.28000 and
12 logical processors. Other verification work ran on the same host; the complete-phase and
complete-Build results therefore include host contention. Historical measurements above are retained.

| Span | Base best median (ms) | Review best median (ms) |
|---|---:|---:|
| ApplySchemaAsync reconciliation inside Build | 0.4077 | 0.0085 |
| After OpenOrCreateAsync through validation and reconciliation | 0.4192 | 0.0103 |
| Complete ProvisionAsync, including OpenOrCreateAsync | 10.2540 | 10.9840 |
| Complete Build, uninstrumented Q8 | 13.8179 | 12.0604 |
| Public apply after Build, uninstrumented Q8P | 0.0727 | 0.0038 |

B2's reconciliation gate remains met. Opening and recovery remain a separate budget. Q8P's
median per-call allocation, including harness overhead, was 44,376 bytes at the base and 640 bytes
after the review fixes, compared with 608 bytes in the historical B2 campaign above. The 32-byte
increase matches the new DDL interlock lease acquired before the hash/generation skip; acquiring
and disposing that lease adds constant work without a catalog walk or hash. All five alternating
invocation medians for the direct spans, in milliseconds:

| Round | Base apply | Review apply | Base post-open | Review post-open | Base complete phase | Review complete phase |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 0.6885 | 0.0115 | 0.7090 | 0.0136 | 19.4474 | 11.6845 |
| 2 | 0.5580 | 0.0107 | 0.5773 | 0.0128 | 12.8206 | 13.8134 |
| 3 | 0.6399 | 0.0085 | 0.6560 | 0.0103 | 88.1281 | 10.9840 |
| 4 | 0.4077 | 0.0087 | 0.4192 | 0.0108 | 10.2540 | 19.5885 |
| 5 | 0.5660 | 0.0102 | 0.5834 | 0.0121 | 21.8429 | 12.3962 |

The separate uninstrumented campaign's five invocation medians, in milliseconds:

| Case | Base | Review |
|---|---|---|
| Q8P | 0.1823, 0.0727, 0.0940, 0.1241, 0.1554 | 0.0068, 0.0093, 0.0047, 0.0038, 0.0065 |
| Q8 | 48.2038, 45.2927, 13.8179, 14.2857, 14.0024 | 16.0448, 14.6620, 12.8571, 14.0046, 12.0604 |

The Release `Sql.AotSample`, normal `Sql.Benchmarks` and instrumented `Sql.Benchmarks`
publications all reported **zero IL2xxx/IL3xxx diagnostics**. The published NativeAOT sample
passed **18 of 18 smoke checks** in process and over the wire. Exact publication commands follow;
the benchmark-only replacement and archived original source timestamps are refreshed after the
normal publications so incremental compilation cannot reuse the uninstrumented assembly.

```powershell
Set-Location C:/cw/b2
$env:MSBUILDDISABLENODEREUSE='1'
$output='C:/cw/b2/_out/b2-review'
$source="$output/src"
$env:NUGET_PACKAGES='C:/cw/b2/_out/b2/nuget-packages'
$env:NUGET_HTTP_CACHE_PATH="$output/nuget-http-cache"
$env:NUGET_SCRATCH="$output/nuget-scratch"
$env:DOTNET_CLI_HOME="$output/dotnet-home"
$env:TEMP="$output/temp"
$env:TMP=$env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
$benchmark='resources/Database/Assimalign.Cohesion.Database.Sql/samples/Assimalign.Cohesion.Database.Sql.Benchmarks'
$project="$benchmark/Assimalign.Cohesion.Database.Sql.Benchmarks.csproj"
$sample='resources/Database/Assimalign.Cohesion.Database.Sql/samples/Assimalign.Cohesion.Database.Sql.AotSample/Assimalign.Cohesion.Database.Sql.AotSample.csproj'
foreach ($directory in @($output, $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_SCRATCH, $env:DOTNET_CLI_HOME, $env:TEMP)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
git archive --format=tar "--output=$output/native-source.tar" da23b378fbaf346ad02234299cf1e2d2fff5d906
New-Item -ItemType Directory -Path $source | Out-Null
tar -xf "$output/native-source.tar" -C $source
Copy-Item -LiteralPath "$benchmark/Q8PhaseInstrumentation.targets" -Destination "$output/Q8PhaseInstrumentation.targets"
& "$benchmark/Prepare-Q8PhaseInstrumentation.ps1" -TreeRoot $source -Label review -OutputRoot $output
Push-Location $source
try {
    dotnet build build/Tasks/Assimalign.Cohesion.Build.Tasks.csproj -c Release *> "$output/native-bootstrap-release.log"
    dotnet publish $sample -c Release -r win-arm64 -o "$output/aot-sample" *> "$output/native-publish-aot-sample.log"
    dotnet publish $project -c Release -r win-arm64 -o "$output/benchmarks" *> "$output/native-publish-benchmarks.log"
    (Get-Item -LiteralPath "$output/phase-timer/review/SqlDeclaredDatabase.cs").LastWriteTimeUtc=[DateTime]::UtcNow
    (Get-Item -LiteralPath 'resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/SqlDeclaredDatabase.cs').LastWriteTimeUtc=[DateTime]::UtcNow
    dotnet publish $project -c Release -r win-arm64 -o "$output/phase-review" "-p:CustomAfterMicrosoftCommonTargets=$output/Q8PhaseInstrumentation.targets" "-p:B2PhaseTimerSource=$output/phase-timer/review/SqlDeclaredDatabase.cs" *> "$output/native-publish-phase-review.log"
}
finally { Pop-Location }
& "$output/aot-sample/Assimalign.Cohesion.Database.Sql.AotSample.exe" *> "$output/native-aot-sample-smoke.log"
foreach ($round in 1..5) {
    & C:/cw/b2/_out/b2/phase-base/Assimalign.Cohesion.Database.Sql.Benchmarks.exe --cases Q8 --rows 0 --iterations 5 --label "base-review-phase-$round" *> "$output/q8-phase-base-$round.log"
    & "$output/phase-review/Assimalign.Cohesion.Database.Sql.Benchmarks.exe" --cases Q8 --rows 0 --iterations 5 --label "review-review-phase-$round" *> "$output/q8-phase-review-$round.log"
}
foreach ($round in 1..5) {
    & C:/cw/b2/_out/b2/baseline/Assimalign.Cohesion.Database.Sql.Benchmarks.exe --cases Q8 --rows 0 --iterations 5 --label "base-review-whole-$round" *> "$output/q8-whole-base-$round.log"
    & "$output/benchmarks/Assimalign.Cohesion.Database.Sql.Benchmarks.exe" --cases Q8 --rows 0 --iterations 5 --label "review-review-whole-$round" *> "$output/q8-whole-review-$round.log"
}
```

The ignored runner additionally checked every exit code, rejected IL2xxx/IL3xxx diagnostics, and
required six `already_applied=1` records per direct invocation before using the median extraction
above. Its initial incremental instrumented publication emitted no phase records and was rejected;
the timestamp refresh above forced the successful recompilation used for every reported result.
Publication logs, sample smoke output, the twenty final per-process logs,
`q8-phase-results.json`, `q8-whole-results.json`, executable hashes in `q8-binary-identity.json`,
and harness hashes in `q8-harness-identity.json` are retained under `_out/b2-review`.

## Decision 49a verification (2026-10-11)

The production SQL sources at `ae1071a7e` retain B2's constant-time already-applied skip.
Five fresh Release NativeAOT `win-arm64` processes, each with one warmup and five measured
already-applied Builds of Q8's 50-table schema, produced these invocation medians in milliseconds:

| Round | Reconciliation inside Build | Post-open provisioning | Complete provisioning including open/recovery |
|---|---:|---:|---:|
| 1 | 0.0107 | 0.0118 | 31.7906 |
| 2 | 0.0101 | 0.0112 | 18.3410 |
| 3 | 0.0094 | 0.0103 | 14.8417 |
| 4 | 0.0090 | 0.0099 | 12.8321 |
| 5 | 0.0113 | 0.0124 | 14.5347 |

The best medians are **0.0090 ms** for reconciliation and **0.0099 ms** from open completion
through provisioning/definition validation, both below the 1 ms gate. File open/recovery remains
outside that gate, as in the B2 campaigns above. Tests were running concurrently on the host;
no new before/after speedup claim is made. The executable reported NativeAOT, workstation GC,
Windows 10.0.28000 and 12 processors, using .NET SDK 10.0.401. Publication emitted zero IL2xxx/IL3xxx
diagnostics. This campaign uses the same benchmark-only source replacement and timing boundaries
described above, updated to match 49a's internal provisioner call. The archive and generated timer
are under `_out/q49`, with `publish.log`, `run-{1..5}.log`, `results.json` and
`phase-timer/current/preparation.json`; the tracked helper refuses instrumenting a live worktree.

Reproduce by archiving the revision beneath `_out/q49/src`, building its Release build tasks,
running `Prepare-Q8PhaseInstrumentation.ps1 -TreeRoot C:/cw/b2/_out/q49/src -Label current
-OutputRoot C:/cw/b2/_out/q49`, then publishing the archived benchmark with
`-p:CustomAfterMicrosoftCommonTargets=<Q8PhaseInstrumentation.targets>` and
`-p:B2PhaseTimerSource=C:/cw/b2/_out/q49/phase-timer/current/SqlDeclaredDatabase.cs`.
Run its native executable five times with `--cases Q8 --iterations 5 --rows 1`; retain the final
five `already_applied=1` timing lines per invocation, as the helper's comments specify.

The final campaign at `24691e207` repeats the same archive/publish/run procedure after the
handoff-compensation correction. Its logs and metadata are under `_out/q49-final` (substitute
that directory in the commands above); publication again emitted zero IL2xxx/IL3xxx diagnostics.

| Round | Reconciliation inside Build | Post-open provisioning | Complete provisioning including open/recovery |
|---|---:|---:|---:|
| 1 | 0.0083 | 0.0092 | 12.6106 |
| 2 | 0.0106 | 0.0117 | 39.9903 |
| 3 | 0.0110 | 0.0120 | 14.1243 |
| 4 | 0.0073 | 0.0081 | 11.9434 |
| 5 | 0.0106 | 0.0116 | 12.6566 |

The final best medians are **0.0073 ms** for reconciliation and **0.0081 ms** for post-open
provisioning, below 1 ms in every invocation. Concurrent tests again shared the host.
