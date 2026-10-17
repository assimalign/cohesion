# Binary row ownership: second fix pass for #1110

Base: `ba469cccba2a2972e87d10d3e51ec51cd47befb9`. The blanket result copy in
`5a229b11e` protected shared producers but also copied every freshly decoded storage value.
The replacement removes that loop from SqlMaterializedResultSet and establishes ownership
at shared producers and reuse sites. No public surface, visibility grant, reference, wire
format or storage format changes.

## Ownership audit

SqlExpressionEvaluator's row-delivery mode copies constants (including immutable folded
results), statement parameters, scalar calls, scalar subqueries and retained group slots.
Ordinary evaluation stays read-only: predicates, ordering, function arguments and SQL bodies
do not copy binary intermediates. CASE and COALESCE deliver only their selected result;
identity casts and argument coercions preserve the operand's delivery. Application casts
detach their converted result when delivering it. This covers both hand-written and typed
native scalar/aggregate functions without changing their public contracts.

DefaultValue restores a delivery copy for a converted cached literal, including missing
ADD COLUMN tails. Expression DEFAULTs use evaluator delivery. INSERT/UPDATE value expressions
use that same mode when supplying their rows. A SQL function body's cached constant is
computed read-only and copied once as the call's delivered result.

A decoded column's first projection transfers its array; later projections of the same
array copy it. Join outer rows can fan out and buffered inner rows survive across outer
iterations. A column ownership map marks those retained sources: projection delivery copies
only selected binary cells, including under CASE/COALESCE/identity casts. Predicates,
discarded candidates and unprojected columns do not copy. An index join's inner read is
fresh and transfers directly. Group output slots detach on each projection,
including two projections of one aggregate slot. Subquery slots likewise detach per use.

System-view enumerators were checked in SqlPlanExecutor.SystemViews: all INFORMATION_SCHEMA
and COHESION_SCHEMA values are strings or immutable scalars. COLUMN_DEFAULT renders binary
defaults as hex text, never as their cached arrays. Binary expressions projected over these
relations use the ordinary delivery rules.

## Storage proof

Scan and SeekRows both call DecodeRow, then SqlRowCodec.Decode and ReadValue. Binary and
JsonBinary components use DatabaseKeyReader.ReadBinary and KeyComponentEncoding.ReadEscaped.
ReadEscaped's nonempty List.ToArray returns a new array on each decode; no decoded-value
cache is involved. Its empty List.ToArray returns Array.Empty<byte>(), the one shared
storage case found. SqlRowCodec.ReadBinary replaces only that empty value with a new array.
No extra copy is added for nonempty stored payloads.

The storage regression reads one stored row twice by scan, then twice through `seek:ix_b`,
projecting only the indexed binary column. It checks identity, contents, mutation isolation
and empty payloads. Index coverage here does not imply a heap-free executor: SeekRows fetches
the row by the entry reference and decodes it. The separate codec regression bypasses result
construction and proves two decodes own their Binary/JsonBinary payloads already.

## Regression comparison

The fifteen new cases in SqlBinaryOwnershipTests and eleven existing binary cases from
SqlTypedLiteralTests passed against both implementations: **26 passed, 0 failed, 0 skipped**
in Debug with `DOTNET_TieredCompilation=0`. The baseline's final run compiled the added tests
with `BuildProjectReferences=false` against the already-built, unchanged `ba469cccb` SQL
assembly, before building the production edits. The saved baseline assembly and benchmark
binaries remain under `_out/1110-pass2-baseline-*`.

The matrix covers empty/nonempty storage scans and covered seeks, direct codec reads,
folded/nonfolded immutable functions, stable/volatile functions, cached SQL CASE/COALESCE
bodies, retained native aggregate results, application casts, stored scalar subqueries,
both system-view families, repeated projections and scan/index join fanout. Existing tests
retain parameter mutation after ExecuteAsync, mutation through GetBytes, later rows and
calls, and DEFAULT-driven INSERTs after result mutation.

Focused command (same filter on baseline and replacement):

```powershell
$env:MSBUILDDISABLENODEREUSE='1'
$env:DOTNET_TieredCompilation='0'
dotnet test resources/Database/Assimalign.Cohesion.Database.Sql/tests/Assimalign.Cohesion.Database.Sql.Tests.csproj --filter 'FullyQualifiedName~SqlBinaryOwnershipTests|FullyQualifiedName~SqlTypedLiteralTests.Select_BinaryResults|FullyQualifiedName~SqlTypedLiteralTests.Select_EmptyBinary|FullyQualifiedName~SqlTypedLiteralTests.Insert_BinaryLiteralDefault' --disable-build-servers
```

## Allocations

The original QB measurement projects four 256-byte constants over `t`; **927.9 B/row was
the allocation before copying those constants, not a stored-binary measurement**. QBS adds
one stored 256-byte BINARY cell in a one-column table, with the same setup exclusion,
GetValue consumption, one warm-up, five samples and 100,000 rows. Each case must be compared
against itself because source schemas and projection widths differ.

Windows arm64, .NET SDK 10.0.401, Release framework-dependent JIT, workstation GC,
`DOTNET_TieredCompilation=0`:

| Case | Blanket fix at ba469cccb, B/row | Source delivery, B/row | Difference |
| --- | ---: | ---: | ---: |
| QBS: stored 256-byte cell | 1711.1 | **1431.1** | **-280.0**, one redundant array removed |
| QB: four projected 256-byte constants | 2047.9 | **2047.9** | Each cell still pays exactly one 280-byte copy |

All five samples agreed to 0.1 B/row; the measuring thread never hopped, and process-wide
allocation agreed within 0.1 B/row. QB's 1120 B/row above its historical pre-copy 927.9 is
exactly four arrays (1024 payload bytes and 96 array overhead). QBS includes its stored
record and the existing escaped-payload decoder's buffer allocations; comparing that
workload with QB's pre-copy constant value would conflate different source rows. No timing
claim is made on the shared host.

The baseline ran `dotnet run --project <benchmark.csproj> -c Release -- --cases QB,QBS
--rows 100000 --iterations 5 --label ba469cccb-blanket`; the replacement ran
`dotnet _out/1110-pass2-after-jit/Assimalign.Cohesion.Database.Sql.Benchmarks.dll --cases
QB,QBS --rows 100000 --iterations 5 --label source-delivery`, after copying the Release
output into that isolated directory. Logs: `_out/1110-pass2-baseline-alloc.log` and
`_out/1110-pass2-after-alloc.log`.

The harness prints `aot=True` for these JIT launches because its PublishAot build sets the
IsDynamicCodeSupported runtime feature to false. That header reports dynamic-code support,
not how the executable was launched; it was not used to classify these measurements.

## Gate environment

The ordinary Debug and Release solution commands failed in the unchanged Database.Testing
fixture with COHDBSDK101: the global package cache's Database SDK/framework references
pre-date its AddDatabase/Schema declaration. This also appears in the earlier branch's
`_out/1110-build-database-debug.log`; it is not introduced by source delivery.

The gate uses a workspace-only SDK overlay at `_out/dotnet/sdk/10.0.401/sdks` (current
Database SDK task/targets; installed .NET SDK directories and packs reached through local
junctions). `_out/1110-pass2-source-fixture.targets`, imported through
CustomBeforeMicrosoftCommonTargets, adds current worktree references to Database.Hosting,
Database.Sql.Tcp and Database.ApplicationModel only for Database.SampleHost. Implicit
Cohesion framework references are disabled for this build, so the fixture validates and
compiles against those source assemblies. Its schema compilation succeeds with hash
`288b7fe9bf0ae21361d1aedc432100640792b80657ad197b01a22a1bc8d8fd8a`.
No repository project or global package installation was changed.

```powershell
$env:MSBUILDDISABLENODEREUSE='1'
$env:MSBuildSDKsPath="$PWD/_out/dotnet/sdk/10.0.401/sdks"
dotnet build resources/Database/Assimalign.Cohesion.Database.slnx -c Debug --no-restore -m:4 --disable-build-servers -p:CohesionAutoIncludeAppFramework=false -p:CustomBeforeMicrosoftCommonTargets=C:/cw/1110/_out/1110-pass2-source-fixture.targets
# Repeat with -c Release.
```

Both final solution builds passed (Debug: 103 warnings/0 errors; Release: 7 warnings/0 errors).
Build.Tasks passed first in both configurations with 0 warnings/0 errors. Existing warnings
include the DependencyInjection/Configuration trim analysis and the empty Refs assembly.
Database.Testing was built as part of the solution but its suite was never run.

## Full test gates

Every entry lists Passed / Failed / Skipped, from the TRX counters. Sql runs with
`DOTNET_TieredCompilation=0`; every invocation passes the test csproj explicitly.

| Suite | Debug | Release |
| --- | ---: | ---: |
| Sql.Language | 2307 / 0 / 0 | 2307 / 0 / 0 |
| Sql | 3178 / 0 / 0 | 3178 / 0 / 0 |
| Sql.Client | 1251 / 0 / 0 | 1251 / 0 / 0 |
| Sql.Schema | 426 / 0 / 0 | 426 / 0 / 0 |
| Database | 209 / 0 / 0 | 209 / 0 / 0 |
| Database.Client | 69 / 0 / 0 | 69 / 0 / 0 |
| Database.Hosting | 130 / 0 / 0 | 130 / 0 / 0 |
| Sdk.Database | 405 / 0 / 0 | 405 / 0 / 0 |
| Total | **7975 / 0 / 0** | **7975 / 0 / 0** |

Logs are `_out/1110-pass2-test-<suite>-<configuration>.log`; TRX files are under
`_out/1110-pass2-results/`. The dependency graph `-Check` passed without regeneration.
The ordinary SDK is used for NativeAOT publication; the source overlay is only needed
for the solution's stale-package fixture. An initial publish through that overlay failed
because it does not contain an MSBuild task host; the installed SDK supplies the host.

Both SQL NativeAOT samples published successfully in Release for win-arm64 with **zero
IL2xxx/IL3xxx warnings**. The published AotSample passed **29/29** in-process/wire checks;
the published benchmark passed its QB/QBS smoke with 100 rows and one measured iteration.
Publication logs are `_out/1110-pass2-publish-AotSample.log` and
`_out/1110-pass2-publish-Benchmarks.log`; the corresponding smoke logs are
`_out/1110-pass2-native-<sample>.log`.

```powershell
# Use the installed .NET SDK, without MSBuildSDKsPath pointing at the fixture overlay.
$env:MSBUILDDISABLENODEREUSE='1'
$env:PATH += ';C:/Program Files (x86)/Microsoft Visual Studio/Installer'
dotnet publish resources/Database/Assimalign.Cohesion.Database.Sql/samples/Assimalign.Cohesion.Database.Sql.AotSample/Assimalign.Cohesion.Database.Sql.AotSample.csproj -c Release -r win-arm64 -m:4 --disable-build-servers -o _out/1110-pass2-native/AotSample
dotnet publish resources/Database/Assimalign.Cohesion.Database.Sql/samples/Assimalign.Cohesion.Database.Sql.Benchmarks/Assimalign.Cohesion.Database.Sql.Benchmarks.csproj -c Release -r win-arm64 -m:4 --disable-build-servers -o _out/1110-pass2-native/Benchmarks
```

## Escalations and follow-ups

No boundary questions or architectural deviations. Deviates from interface-first
(database-area.md), following the area's standing concrete-first rule; no new public type.
The requested approximately 928 B/row storage target uses QB's pre-copy constant workload;
the recommendation is to compare QBS with its own baseline and require removal of exactly
one 280-byte array per stored 256-byte value, while retaining one copy per projected constant.

Refresh the local SDK/framework package feed outside this work item to restore the plain
solution command (fixture Program.cs:20 uses the current declaration; global.json pins the
cached SDK version). Install-Local.ps1 was not run. The benchmark's dynamic-code/AOT header
at samples/Assimalign.Cohesion.Database.Sql.Benchmarks/Program.cs:87 is a reporting follow-up;
its label should describe the feature it actually reads.
