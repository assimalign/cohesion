# Library performance work: iteration 1

Started 2026-10-21 from `c2b25e4ac`, with the companion benchmarks checkout at `7a6aa1a`.
Both worktrees use `codex/library-performance`. This is the first measured batch in a larger
area-by-area effort; the inventory below is a review queue, not a claim that every area has
been optimized.

## Boundaries

- Inspect `libraries/` and `resources/Web/` (the repository's actual spelling).
- Exclude `libraries/ApplicationModel/`, `libraries/App/`, `libraries/Amqp/` and `libraries/OpenTelemetry/`
  (OpenTelemetry was added to the exclusions by the owner before iteration 3).
- Preserve public signatures, parameter names, attributes, package boundaries and NativeAOT posture.
- Keep benchmarks package-based. Local measurement packages are isolated prerelease builds derived
  through `CohesionPatchVersion`; neither repository's committed version pin changes.
- Keep only measured improvements. Repeat suspicious timing changes and investigate allocation regressions.

## Changes in this batch

| Area | Change | Cost removed | Correctness evidence |
| --- | --- | --- | --- |
| Cache | Lazily allocate entry token/callback lists; snapshot callbacks into an exact array, sharing the empty array | Three empty list objects per ordinary write | Full cache suite; callback configuration remains a commit-time snapshot |
| Configuration | Validate before formatting the error message; retain immutable string inputs | Validation-message allocations on successful construction; an unnecessary string copy | Full configuration suite; delimiter rejection, null/empty and ordinal identity preserved |
| Core | Cache token length bounds in internal `Glob.GlobPattern`, nested in `Glob` | O(tokens) precheck on every match becomes O(1) | Full Core suite, concurrent parsing, brace groups, immutable token views and one-object cached parsing |
| Web.Routing | Join the catch-all by array range | Temporary catch-all slice array | Full routing suite plus root, repeated/trailing slash and catch-all preservation cases |

The glob cache retains one metadata object per cached pattern. Each returned `Glob` remains the
same size and cached parsing still allocates only that object. Bounds and tokens share the existing
1,024-miss cache-clear policy; the cache does not grow without a bound.

Exception resources were adopted where the touched cache and key code still used literal messages,
as required by `.claude/rules/general-rules.md`. Message text is preserved.

## Defects tracked and fixed

- [#1679](https://github.com/assimalign/cohesion/issues/1679): nullable configuration-key inequality
  returned the wrong result for equal populated keys and mixed null operands. Operators now compare
  underlying values directly; equality and inequality are complements. This changes incorrect results,
  with every public signature preserved. Project placement follows bug policy B4: no delivered leaf
  criterion specifically owned these operators, so the correction is an ordinary item under Configuration,
  labelled `bug`.
- [#1680](https://github.com/assimalign/cohesion/issues/1680): an expiration callback from a replaced
  entry could remove its successor. Subscriptions now share a binding to their original `StoredEntry`,
  and exact-value removal prevents a stale callback from invalidating a replacement. The issue is a
  native bug child of delivered invalidation story #396.

Both regressions were reproduced before their fixes. The token test invokes the retired callback
during unsubscription, making the race deterministic. Synchronous notifications before commit retain
their documented behavior. No public API change was needed for either fix.

## Measurement and validation

Machine: Windows 11 ARM64, Snapdragon X 12-core X1E80100, .NET SDK 10.0.402, runtime 10.0.13.
Measurements use BenchmarkDotNet 0.15.8 with the repository harness and its in-process toolchain.
Inputs and setup are unchanged between packages. These are local microbenchmarks, not cross-machine
or end-to-end throughput claims.

The initial 15-case comparison uses three warmups and eight measured iterations. Glob and routing
receive a second comparison with five warmups and 15 iterations. Both splitter candidates regressed
matching time and were removed; the final Web measurement retains only catch-all range joining.
Exact reports and commands live beside the affected benchmark areas in the companion
repository. Timing is assessed alongside deterministic allocated bytes; no dry-run timings are used.

| Retained result | Before | After | Interpretation |
| --- | ---: | ---: | --- |
| Ordinary/TTL/sized cache replacement allocation | 400 B | 304 B | 24% less; timing change is small |
| Key from a string allocation | 312 B | 0 B | Immutable input retained; validation does not format a successful-path message |
| Key from a span allocation | 312 B | 56 B | Input ownership still requires a copy |
| Three-segment path parse allocation | 1,000 B | 232 B | 76.8% less; median time falls from 188.6-196.9 ns to 63.1-68.2 ns |
| Fixed-pattern glob matching, 64 paths | 6.66 us | 2.47 us | About 63% less time, still allocation-free |
| Variable-pattern glob matching, 64 paths | 6.49 us | 5.92 us | Smaller repeat-run improvement; treat cautiously |
| Catch-all match allocation | 528 B | 480 B | 48 B less; unchanged-control timing variation prevents a throughput claim |

Full affected suites: Cache 65 passed, Configuration 232 passed, Core 475 passed plus one existing
skip, and Routing 398 passed: **1,170 passed, one skipped**. Two benchmark classes are added without
renaming existing series. The per-area comparison tables report no regression at the benchmark
script's 10% timing / exact allocation thresholds for the retained measurements.

Strict SDK package validation compares the four changed packages with their baseline, including
public API shape, parameter names and attributes. Internal cache representations and private helpers
may change; no public API change is included or requested for this batch.

NativeAOT publication was attempted for a focused `win-arm64` consumer covering Key, Glob, MemoryCache
and Route. Managed compilation and execution succeeded, but native linking is blocked by missing
Desktop C++/ARM64 linker tools. Native publication is therefore unverified on this machine. Existing
ConfigurationBinder IL2070/IL3050 warnings were present in the baseline and remain tracked by
[#1220](https://github.com/assimalign/cohesion/issues/1220); this batch adds no suppression.

## Inventory and subsequent passes

Every in-scope area has a companion benchmark suite. Counts below are benchmark classes at the
start of this iteration; the two new classes add focused key-construction and route-path coverage.
Only the four areas above have implementation changes and measured results in this batch. The other
rows identify the next question to measure, not confirmed bottlenecks or permission to change semantics.

| Area | Existing benchmark classes | Next investigation |
| --- | ---: | --- |
| Cache | 8 | Commit delegate/closure cost, then size-limit compaction under realistic contention |
| Configuration | 11 | Carry key-construction gains through complete provider lookups and loads; address existing AOT work separately |
| Connections | 10 | Layered feature lookup and shadowing, then pipe copying; preserve wrapper/subclass behavior |
| Content | 17 | BMFF write-plan prefix materialization and binary read copies; coordinate with existing baseline work #1549 |
| Core | 6 | Glob fixed/variable patterns and cached parsing; retain parser concurrency and brace semantics |
| DependencyInjection | 5 | Runtime-engine resolution and scope allocations under AOT-safe registrations |
| Dns | 4 | Name compression and message construction; preserve pointer checks and wire conformance |
| FileSystem | 9 | Directory resolution and recursive deletion snapshots; distinguish required mutation snapshots from redundant copies |
| Hosting | 6 | Start/stop allocation and resource mount parsing; avoid optimizing infrequent lifecycle work ahead of request paths |
| Http | 13 | Header/Accept parsing, form content-type splitting and HTTP/1 request parsing |
| IdentityModel | 10 | Successful validation diagnostic collections and signed-token parsing; keep signature checks and validation results identical |
| Logging | 5 | Fan-out and event forwarding first; filter-rule staging lists occur at logger creation, not each log entry |
| ObjectMapping | 5 | Sequence mapping capacity and dispatch; preserve eager mapping and enumeration semantics |
| ObjectPool | 4 | Contended rent/return and retention limits; the existing fast slot is already allocation-free when reused |
| ObjectValidation | 4 | Nested/collection validation first; expression-name splitting belongs to profile-build measurements |
| OpenApi | 8 | JSON/YAML read/write and document transforms; attribute materialization is mostly construction work |
| Resilience | 9 | Synchronous completion in timeout strategies currently reaches task conversion, delay and WhenAny; verify cancellation races before changing it |
| Security | 3 | Purpose-chain derivation and key-document parsing; keep authenticated-encryption behavior and key ownership intact |

The next experiments are timeout synchronous completion and layered connection-feature lookup.
Public API alternatives are deferred for joint review if an
experiment proves they are necessary; no such change is necessary for the current batch.

The timeout investigation continues in [iteration 2](LIBRARY_PERFORMANCE_ITERATION_02.md), on the
same branches. Layered connection-feature lookup remains the next candidate. The owner excluded
OpenTelemetry before iteration 3; no OpenTelemetry implementation changes are retained on this branch.

With the ARM64 C++ tools installed during iteration 3, the existing Key/Glob/MemoryCache/Route
smoke consumer successfully published for `win-arm64` and its native executable passed. The
native-linking environment limitation recorded above is resolved.

Layered named connection-feature lookup is measured in
[iteration 3](LIBRARY_PERFORMANCE_ITERATION_03.md). HTTP header/Accept parsing remains a candidate
for the next in-scope pass.
