# Library performance work: iteration 4

Continues `codex/library-performance` in both repositories on 2026-10-21. The retained
change is short-list preference ordering in `libraries/Http/Assimalign.Cohesion.Http/`.
Earlier iterations remain present. ApplicationModel, App, Amqp and OpenTelemetry remain
excluded; public APIs, package dependencies and committed version pins are unchanged.

## Finding and implementation

`HttpAcceptParser` collected parsed values in a list, then constructed two LINQ ordering
layers and their sorting state before producing its result array. This affected even a
single valid entry. The impact is small per request, but repeats wherever callers parse
Accept-family headers; Web serialization's content negotiator calls `ParseAccept` directly.

For at most eight valid media ranges or sixteen valid tokens, ordering now uses stable
insertion sorting on the result array. Quality still sorts descending, media specificity
breaks quality ties, and explicit tokens precede wildcards at equal quality. An equal-ranked
entry never crosses an earlier one. Malformed input handling, quoted delimiters, parameter
preservation, quality-zero entries and the returned array representation are unchanged.

The quadratic insertion work is bounded (at most 28 media moves or 120 token moves). Larger
lists retain the original stable O(n log n) algorithm, avoiding unbounded quadratic work
from client-controlled input. Sorting needs no auxiliary arrays for the short path; there
is no request cache, retained state, new type or new public member. The package DESIGN
records this bound and the stable-order requirement.

## Tracking and measurements

[#1684](https://github.com/assimalign/cohesion/issues/1684) is planned performance work under
the delivered negotiation feature #771. The Http epic has reached GitHub's 100-child limit.
This adds optimization criteria rather than restoring violated delivered behavior; no
product defect was identified in this investigation. The issue remains In progress until
merge, and #771 stays closed.

Measurements use isolated NuGet packages: `10.0.0-preview.1.perf.accept.before` and
`10.0.0-preview.1.perf.accept.final`, derived through `CohesionPatchVersion`. Both include
the earlier Core improvements; only the HTTP parser implementation differs. No package
source or committed version pin is changed. The baseline parser is restored from the
branch's base commit when packing, independently of the working candidate.

The final matrix includes twelve new media/token cases at 1, 8, 9, 16, 17 and 64 entries, the existing
browser Accept workload and its ASP.NET Core counterpart, and six unchanged Content-Type
and Range parser controls. Increasing weights require short-list moves, and equal-ranked
pairs exercise stable ordering. The 9-/16-entry media and 17-/64-entry cases check the
unchanged large-list paths.

Machine: Windows 11 ARM64, Snapdragon X X1E80100; SDK 10.0.402, runtime 10.0.13,
BenchmarkDotNet 0.15.8 with the repository's in-process harness. The first pair uses one
launch, five warmups and ten measured iterations, baseline then an initial candidate.
That candidate (`10.0.0-preview.1.perf.accept.after`) used sixteen as both cutoffs; the
reverse-ranked sixteen-range media workload regressed and that cutoff was rejected.
The final pair adds nine-entry boundary cases and uses three warmups and eight measured
iterations, final candidate then baseline, with all twenty cases in both runs. No build, test
or NativeAOT compilation runs concurrently with timing samples.

### Final reverse-order results

| Workload | Median before | Median after | Bytes before | Bytes after |
| --- | ---: | ---: | ---: | ---: |
| Browser Accept | 1,113.2 ns | 903.3 ns | 2,384 | 1,536 |
| One media range | 176.1 ns | 133.3 ns | 544 | 320 |
| Eight media ranges, increasing quality | 1,388.7 ns | 1,201.8 ns | 2,232 | 1,384 |
| One token | 105.1 ns | 64.3 ns | 392 | 200 |
| Eight tokens, increasing quality | 640.8 ns | 484.0 ns | 1,408 | 744 |
| Sixteen tokens, increasing quality | 1,403.5 ns | 1,047.9 ns | 2,336 | 1,472 |
| Sixteen media ranges, original sort | 2,433.7 ns | 2,399.1 ns | 4,064 | 4,064 |

Browser Accept allocates **848 fewer bytes (35.6%)** per parse. The repeat median is
18.9% lower; the initial pair showed only about 9% lower time. Eight-range increasing-quality
media sorting was nearly neutral in the first pair and about 13% lower in the repeat, so its
timing gain is treated cautiously. Token-list time and allocation reductions persist in both
pairs. These are local parser measurements, not end-to-end HTTP throughput claims.

Unedited full JSON, comparisons and reproduction instructions are saved in
`cohesion-benchmarks/benchmarks/libraries/Http/results/2026-10-21-performance-iteration-04/`.
The `initial/` comparison retains the rejected sixteen-range media regression and two
over-10% changes in unchanged controls. The final comparison has all twenty matched cases,
zero failed measurements and **zero regressions** at 10% timing / exact allocation thresholds.
Large-list cases remain within about 4% with identical allocations. Unchanged Cohesion
Content-Type and Range controls also become about 11% and 19% faster in the final pair,
showing environmental variation; no speedup is attributed to this change for those controls,
and the precise browser timing gain cannot be isolated from that variation. Allocation
reduction is the strongest conclusion.

## Validation

All fourteen final compatibility cases pass with the original parser and the candidate.
They cover empty/one/8/9/16/17/64 valid entries for media and all three token-list entry points,
mixed quality, specificity, wildcards, stable ties, quoted commas and
semicolons, ignored Accept extensions and skipped malformed entries.

Final Release tests: HTTP **1,809 passed** and Web.Serialization **50 passed**, with zero skips.
The first HTTP run passed 1,806 tests but failed the existing feature-lookup allocation test
with 5,368 unexpected bytes in its measurement region. All eleven feature-lookup cases then
passed in isolation, and the complete 1,807-test rerun passed. Both the first result and
recheck logs/TRX files remain under ignored `.codex-temp/`; the unrelated test was not changed.

The candidate package builds with zero warnings/errors and passes strict SDK API validation,
including parameter names and attributes, against the baseline package. No suppression is
added. A package-only `win-arm64` NativeAOT consumer publishes and executes successfully with
trim/AOT diagnostics treated as errors. It verifies stable ordering on both sides of the
size boundary, quoted parameters, malformed entries, specificity and token wildcard ordering.

Repository checklist: applicable dependency, AOT, placement, using, naming, test and resource
rules pass; no MSBuild, exception, extension-member, EventSource, public API or diagram change
is introduced. Existing diagrams remain unchanged. Consumer fixtures and package feeds are
temporary ignored files in the companion benchmark repository.

## Next investigation

Investigate HTTP media-type parameter parsing and formatting, or a Web consumer with measured
allocation pressure. Keep these sorting measurements separate from subsequent candidates.
