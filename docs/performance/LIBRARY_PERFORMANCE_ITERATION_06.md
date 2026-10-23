# Library performance work: iteration 6

Continues `codex/library-performance` in both repositories on 2026-10-21. This iteration
investigates the request path in `resources/Web/Assimalign.Cohesion.Web.Serialization`.
ApplicationModel, App, Amqp and OpenTelemetry remain excluded. All earlier retained
iterations remain present. Public signatures, dependencies and committed version pins
are unchanged.

## Findings and changes

Registry lookup iterated each codec's `IReadOnlyList<HttpMediaType>` through its enumerable
interface. Array-backed advertisements allocated an enumerator per registration, per
lookup. Negotiation did the same for the registered writers, their media types, and the
lists traversed during suffix fallback and refusal checks. Indexed traversal removes
those enumerators while preserving registration order, specificity and matching rules.
Lookup remains O(r × m); collection and de-duplication retain their existing worst-case
quadratic behavior in the number of advertised representations. This work changes
traversal cost, not matching algorithms.

Negotiated writes split the existing `Vary` string into an array and substring tokens,
then trimmed each token into another string when whitespace was present. They now
scan trimmed spans. The result still preserves existing text, ignores case for `Accept`,
recognizes only whole tokens, honors `*`, and appends `, Accept` when needed. Header
scanning stays linear in the existing value, without per-token allocations.

Per-call impact is small, but these operations repeat on typed request/response bodies
and negotiated writes. Only internal implementation changes are retained. Custom codec
advertisements are deliberately **not cached**: the public contract does not promise
immutable `MediaTypes`. The registry snapshots registrations at build time and keeps
reading each codec's current media-type list. No public API change is needed and no
additional correctness defect was discovered in this investigation.

## Tracking

[#1688](https://github.com/assimalign/cohesion/issues/1688) tracks this planned performance
work under delivered negotiation story #149. It remains In progress until merge and is
included in the branch closing manifest. The delivered parent stays closed.

## Measurement design

Package-only benchmarks compare `10.0.0-preview.1.perf.serialization.before` and
`10.0.0-preview.1.perf.serialization.after`, built through `CohesionPatchVersion`. Both
include previous retained iteration improvements; only the three serialization source
files differ. Core, Http, Web and Web.Serialization are packed into the local feed for
both versions. No source references or benchmark pin changes are introduced.

Eighteen new standalone cases cover reader/writer lookup with 1 or 8 registrations,
negotiation with absent/exact/suffix/rejected Accept values, and negotiated writes over
six Vary shapes. Matching registrations are last in the lookup fixtures. Negotiated
writes use a completed no-op writer to isolate registry/header work, not JSON throughput.
Setup asserts selected registrations, negotiation outcomes and exact Vary text.
Eight existing JSON read/write cases include four unchanged ASP.NET Core counterparts
as environmental controls, with the same source-generated contracts and streams.

Machine: Windows 11 ARM64, Snapdragon X X1E80100, SDK 10.0.402 / runtime 10.0.13,
BenchmarkDotNet 0.15.8 with the repository's in-process harness. The full matrix runs
candidate then baseline, one launch, three warmups and eight measured iterations.
Builds, tests and native compilation finish before measurements begin. Medians and
allocation bytes describe local in-process workloads; they do not establish an
end-to-end HTTP throughput gain. Execution time is the acceptance gate: memory savings
do not justify a repeatable slowdown.

## Results

The 26-case full matrix completed without failed measurements. The comparison script
reports zero regressions at its default 10% time / exact-allocation thresholds. The
execution-time acceptance decision also checks smaller increases rather than treating
that threshold as permission to trade speed for memory.

| Workload | Median before | Median after | Bytes before | Bytes after |
| --- | ---: | ---: | ---: | ---: |
| Reader lookup, 1 registration | 12.7 ns | 7.1 ns | 32 | 0 |
| Reader lookup, 8 registrations | 109.0 ns | 58.2 ns | 256 | 0 |
| Writer lookup, 1 registration | 12.5 ns | 7.1 ns | 32 | 0 |
| Writer lookup, 8 registrations | 98.3 ns | 55.8 ns | 256 | 0 |
| No Accept, 1 writer | 39.0 ns | 30.3 ns | 216 | 152 |
| No Accept, 8 writers | 351.1 ns | 309.3 ns | 656 | 368 |
| Exact Accept, 8 writers | 552.6 ns | 553.1 ns | 1,232 | 944 |
| Suffix fallback, 8 writers | 700.4 ns | 619.9 ns | 1,320 | 976 |
| JSON read, 32 characters | 432.3 ns | 410.7 ns | 312 | 280 |
| JSON write, 32 characters | 331.2 ns | 323.5 ns | 648 | 616 |
| JSON write, 4,096 characters | 678.9 ns | 745.3 ns | 648 | 616 |
| Negotiated write, existing `Origin, Accept-Encoding, ACCEPT` | 253.4 ns | 236.1 ns | 880 | 504 |
| Negotiated write, existing `Origin, Accept-Encoding` | 249.0 ns | 255.2 ns | 880 | 592 |

Lookup is 43–47% faster in this matrix and removes all measured lookup allocations.
Negotiation saves 64–344 bytes per call. Negotiated writes save 96–376 bytes, depending
on the existing header, and JSON read/write each save 32 bytes. These figures include
the fixture work and the unchanged remaining parser/serializer allocations.

### Timing rechecks

The first matrix's 4,096-character JSON write is **9.8% slower**, despite the allocation
saving. The eight-writer exact negotiation and Vary append case also have small positive
time deltas. All are investigated rather than accepted solely because they fall below
the script's threshold.

Longer runs reverse the full matrix's order: baseline then candidate, five warmups and
twelve measured iterations. The JSON write repeat measures **676.8 ns → 672.6 ns**;
its unchanged ASP.NET Core counterpart measures **546.5 ns → 540.7 ns**. The original
JSON slowdown does not repeat, so this work claims the 32-byte saving with approximately
unchanged large-payload execution time, not a serializer throughput improvement.
The eight-writer exact repeat measures **498.5 ns → 457.3 ns**, so its original 0.1%
increase also does not repeat. All five cases in that repeat pass the comparison gate.

The focused two-case Vary repeat measures **243.3 ns → 198.2 ns** when appending to
`Origin, Accept-Encoding` and **257.9 ns → 193.1 ns** when preserving an existing Accept
token. Its original append increase also does not repeat. The same source/package is
used throughout; there are no code changes between the full run and the repeats.

The retained changes show **no repeatable execution-time regression in the measured
workloads**. Exact percentages for small negotiation/header paths vary by run; the
allocation reductions are deterministic. The original measurements remain visible,
including the apparent slowdowns that prompted longer runs.

Raw results and generated comparisons are in the benchmark repository at
`benchmarks/resources/Web/results/2026-10-21-performance-iteration-06/`:

- `before/` and `after/`: full 26-case matrix, four reports per side.
- `repeat/`: five-case negotiation/JSON recheck, two reports per side.
- `vary-repeat/`: two-case Vary recheck, one report per side.

All fourteen raw full JSON reports are copied without modification and verified against
their source SHA-256 hashes. Dry setup checks are not timing evidence. The new benchmark
identifiers are stable and the existing JSON identifiers are unchanged.

## Validation

- Release Web.Serialization suite: **61 passed**, zero failed or skipped.
- Eleven new compatibility cases also pass against the original implementation:
  live custom media types; missing/empty Vary; existing Accept with casing/whitespace;
  Accept-Encoding as a distinct token; wildcard; empty segments; Unicode whitespace.
- Strict package API validation succeeds, including parameter names and attributes,
  with zero warnings/errors.
- A package-only Windows ARM64 NativeAOT consumer publishes and executes successfully,
  with trim/AOT warnings treated as errors. It exercises registry lookup, live custom
  advertisements, suffix negotiation, explicit rejection, source-generated JSON writes,
  Vary preservation and the 406 outcome.

Validation logs are ignored local artifacts: `.codex-temp/serialization-final-tests.log`,
`.codex-temp/serialization-baseline-tests.log`,
`.codex-temp/serialization-pack-serialization.after-Assimalign.Cohesion.Web.Serialization.log`
in cohesion, and `.artifacts/performance/serialization-native-smoke.log` in benchmarks.

## Next investigation

Return to a different in-scope library area, such as DependencyInjection runtime resolution
or Mapping sequence execution. Retain the execution-time gate and package-only comparisons.
This iteration does not change those areas or claim a server throughput improvement.
