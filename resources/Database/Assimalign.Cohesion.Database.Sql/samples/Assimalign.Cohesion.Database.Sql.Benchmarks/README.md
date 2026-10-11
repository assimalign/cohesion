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
are retained below; the alternating campaign is the comparison used in the phase's landed note.
These are measured elapsed times, not a claim that the host was otherwise idle.

| Campaign | Case | Base best median (ms) | B2 best median (ms) |
|---|---|---:|---:|
| Initial, separated | Q8P provisioning | 0.0595 | 0.0046 |
| Initial, separated | Q8 complete Build | 7.5989 | 9.7058 |
| Alternating | Q8P provisioning | 0.1063 | **0.0027** |
| Alternating | Q8 complete Build | 10.2314 | 9.7064 |

All five alternating medians, in invocation order, in milliseconds:

| Case | Base | B2 |
|---|---|---|
| Q8P | 0.1697, 0.1088, 0.1553, 0.1366, 0.1063 | 0.0027, 0.0045, 0.0072, 0.0074, 0.0060 |
| Q8 | 10.2314, 10.3892, 10.6509, 11.7185, 10.8545 | 10.0135, 9.7064, 10.2450, 10.6881, 11.8219 |

Q8P's per-call median allocation was 44,376 bytes at the base and 608 bytes in B2, including
harness overhead. The provisioning gate is **under 1 ms**, passed in both campaigns. Q8P is the
isolated public apply measurement described above; Q8 reports complete Build separately.
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
