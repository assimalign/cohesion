# Library performance work: iteration 3

Continues `codex/library-performance` in both repositories on 2026-10-21. The retained change is
named feature lookup in `libraries/Connections/Assimalign.Cohesion.Connections/`.
Iteration 1 and 2 changes remain present. Public APIs, package dependencies and committed version
pins remain unchanged.

## Scope correction

The owner added `libraries/OpenTelemetry/` to the exclusions during this iteration. All of this
iteration's OpenTelemetry implementation and test changes were removed. No OpenTelemetry benchmark
or result change is included in the deliverable. Work item
[#1682](https://github.com/assimalign/cohesion/issues/1682) was moved to Backlog with a deferral note
and removed from this branch's issue-closing manifest. ApplicationModel, App and Amqp also remain
excluded. The scope and inventory in iteration 1 now reflect the additional exclusion.

## Finding and retained change

`ConnectionFeatureCollection.Get(name)` checked its local immutable snapshot and then delegated
through the inner collection's interface. Each built-in wrapper repeated that dispatch and name
validation. The existing typed lookup already has an allocation-free traversal and is unchanged.

Named lookup now keeps its local check in the small entry point and walks exact built-in inner
collections in a private loop. At a custom implementation or subclass, it calls the object's
interface `Get(name)`. That preserves subclasses that intentionally expose a different named view
from their private base collection. A tombstone still stops traversal; each level reads its current
published snapshot, so replacement and removal remain visible without cache invalidation state.

Impact is low per lookup but repeated on connection-feature access paths. Time remains
O(visited layers multiplied by local entries), while stack use for built-in traversal becomes O(1).
Both versions allocate zero bytes per lookup. No retained cache, extra instance field, generic type
cache, lock or public member is introduced. The package DESIGN records the traversal and custom
dispatch boundary. The private helper follows the existing iterator declaration so its compiler-
generated state-machine attribute identity is preserved under strict API validation.

## Tracking and evidence

[#1683](https://github.com/assimalign/cohesion/issues/1683) is a planned performance item under
Connections #324. It adds optimization criteria rather than restoring violated delivered behavior;
no product defect was found in this investigation. Its Project #13 Area is `Net`, matching the
owning epic's existing board value; the code area is named Connections. Status remains In progress
until the implementation merges.

Benchmarks use isolated NuGet packages, not project references. The baseline and candidate both
include the earlier Core improvements. Versions are `10.0.0-preview.1.perf.connections.before`
and `10.0.0-preview.1.perf.connections.final`, derived through `CohesionPatchVersion`.
The first matrix measures all eight existing lookup cases plus four new absent-name cases at
0, 1, 4 and 16 layers. Existing benchmark identifiers and inputs remain unchanged. The new absent
case complements the existing named miss, which hits a tombstone in the outermost collection.

Machine: Windows 11 ARM64, Snapdragon X X1E80100, SDK 10.0.402 / runtime 10.0.13;
BenchmarkDotNet 0.15.8 with the repository's in-process harness. The first pair uses one launch,
five warmups and 12 measured iterations, baseline then candidate. A reverse-order repeat uses
the same 12 cases, three warmups and eight measured iterations for each package. Small absolute
costs and unchanged-control variation limit any extrapolation to application throughput.

### Retained repeat results

| Workload | Median before | Median after | Allocation in both |
| --- | ---: | ---: | ---: |
| Inherited named hit, one wrapper | 4.9 ns | 4.0 ns | 0 B |
| Inherited named hit, four wrappers | 12.9 ns | 11.9 ns | 0 B |
| Fully absent name, one wrapper | 4.2 ns | 3.7 ns | 0 B |
| Fully absent name, four wrappers | 12.2 ns | 10.1 ns | 0 B |
| Fully absent name, 16 wrappers | 39.3 ns | 28.7 ns | 0 B |

The repeated one-wrapper hit improves about 19%; four-/16-wrapper absences improve about
17% / 27%. The smaller four-wrapper hit difference is treated cautiously. Sixteen wrappers are
a stress depth rather than an expected deployment topology. Both comparisons report zero
regressions at the benchmark script's 10% timing / exact allocation thresholds. The four unchanged
typed-lookup controls stay within about 4% on the repeat; the initial almost-10% movement in one
control does not persist. Bare-collection and outer-tombstone control timings also vary between
runs, so no systematic speedup is claimed for those paths. Allocation remains exactly zero.

Unedited full JSON for both pairs, comparisons and reproduction instructions are saved in
`cohesion-benchmarks/benchmarks/libraries/Connections/results/2026-10-21-performance-iteration-03/`.
The primary comparison uses the reverse-order repeat; the initial results remain visible under
`initial/`. No dry-run timing is used as performance evidence.

## Validation

The five new named-lookup compatibility cases pass against the original implementation as well
as the candidate. They cover custom subclass interface dispatch, local shadowing and tombstones,
case-sensitive lookup, absent names and live inner replacement across 0/1/4/16 layers.

Release suites pass: Connections 94, InMemory 78, StreamAbort 3, StreamLimits 3, Tls 5 and
Security 46: **229 passed, zero failures or skips**. Existing concurrent mutation/read and
allocation-free typed-lookup tests remain green. TRX files and build logs are retained under the
ignored `.codex-temp/` folder. The final package build has zero warnings and errors and passes
strict SDK API validation, including parameter names and attributes. No suppression was added.

The final packaged ARM64 NativeAOT consumer publishes and executes successfully. It exercises
16-layer named/typed lookup, ordinal misses, tombstones, replacement and custom subclass dispatch.
The owner-installed C++ tools also resolved the earlier iterations' native-linking blocker:
the existing Key/Glob/MemoryCache/Route consumer and timeout consumer both publish and pass as
`win-arm64` native executables. These are focused consumer checks, not a claim that every package
or unrelated existing AOT issue has been exhaustively validated.

Repository checks: ✅ internal implementation only; ✅ existing dependency/version configuration
and AOT settings preserved; ✅ field/layout/using conventions reviewed; ✅ no new exception text,
reflection serialization or public members; ✅ package DESIGN updated; ✅ package-based benchmarks
and original evidence retained; ✅ affected suites and strict API checks; ✅ native consumer execution;
✅ clean `git diff --check` and no changes in all four excluded areas in either repository.
New project, shared-source, extension-member, diagnostic-provider and public-API checklist sections
are not applicable to the retained production change.

The next unmeasured candidate from the remaining inventory is HTTP header/Accept parsing.
