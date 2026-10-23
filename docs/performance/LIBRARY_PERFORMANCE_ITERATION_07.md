# Library performance work: iteration 7

The owner's 2026-10-22 request extends the existing branch through the rest of the flagged
investigation queue before a consolidated handback. This batch investigates ObjectMapping,
ObjectValidation, Dns, IdentityModel.Token and Content.Bmff. Four package changes are retained;
both ObjectValidation candidates are rejected and its source restored. Earlier retained changes remain;
ApplicationModel, App, Amqp and OpenTelemetry remain excluded. Both repositories still use
`codex/library-performance`; public API signatures and committed version pins are unchanged.

## Investigated changes

| Package | Implementation | Compatibility boundary | Work item |
| --- | --- | --- | --- |
| ObjectMapping | Reserve the target List capacity for ICollection sources | Eager mapping, one source enumeration, independent targets, source order, unknown-count and null behavior preserved | #1691 |
| ObjectValidation (rejected) | Two span-based nested-source candidates reduced error allocations but failed the execution-time gate | Original implementation and DESIGN restored | #1692, Backlog |
| Dns | Reuse a 63-byte stack label buffer; encode only literal labels; join suffix spans before existing invariant lowercasing | Original wire case, ASCII replacement, root/trailing-dot behavior, comparer, pointer limits and decoder checks preserved | #1693 |
| IdentityModel.Token | Allocate the neutral diagnostics list on the first finding | Existing shared success result, complete ordered diagnostics and immutable failure snapshot preserved | #1694 |
| Content.Bmff | Write prefixes into exact arrays instead of growing a stream and copying it | Every prefix callback still executes, including zero-length and unsupported cases; bounded write and exact size checks remain | #1695 |

The touched mapping, DNS and token code adopts generated exception resources where required;
message text and formatting are unchanged. Each retained project's DESIGN records its implementation
decision. These are changes to implementation, not a new public API or a serialization policy.

All five work items were searched, dry-run, created and linked into Cohesion Project #13.
The four retained items remain In progress until merge. #1692 is Backlog and removed from the
branch closing manifest. BMFF work is a child of the existing #1549 performance
baseline feature; that feature's positional-read API work is not included in this pass.

## Measurement

Package versions are `10.0.0-preview.1.perf.batch.before` and
`10.0.0-preview.1.perf.batch.after`, derived with CohesionPatchVersion. Both include retained
iterations 1–6. Core and each package's dependency closure are packed into the local measurement
feed. Benchmarks consume packages only; no source reference or committed pin is changed.

Machine: Windows 11 ARM64, Snapdragon X X1E80100, SDK 10.0.402 / runtime 10.0.13,
BenchmarkDotNet 0.15.8. The repository's in-process harness runs candidate then baseline,
three warmups and eight measured iterations, with builds/tests/native compilation completed
before measurements. Medians are local per-operation timings, not server throughput.

The 26-case matrix includes four new BMFF write-plan cases and existing DNS compression,
mapping collection, nested validation and neutral-token validation cases. AutoMapper,
FluentValidation and the unchanged DNS decoder paths supply controls. Empty BMFF prefixes
and successful nested validation exercise paths with no allocation reduction. Every setup
checks its result. Dry setup checks are excluded from timing evidence.

The initial results below include the subsequently rejected validation candidate.

| Workload | Median before | Median after | Bytes before | Bytes after |
| --- | ---: | ---: | ---: | ---: |
| Map 1 item | 104.1 ns | 96.5 ns | 264 | 240 |
| Map 100 items | 8.02 us | 7.53 us | 16,624 | 15,288 |
| DNS 4 shared-suffix answers | 1.32 us | 1.07 us | 4,040 | 1,952 |
| DNS 32 shared-suffix answers | 9.48 us | 7.87 us | 28,952 | 13,424 |
| DNS 32 distinct-suffix answers | 16.07 us | 13.50 us | 49,904 | 24,304 |
| Neutral token success | 5.6 ns | 2.6 ns | 32 | 0 |
| Neutral token expired | 74.7 ns | 77.1 ns | 432 | 432 |
| Invalid nested object | 2.13 us | 2.25 us | 17,200 | 16,528 |
| Invalid nested collection | 15.89 us | 16.03 us | 137,304 | 132,184 |
| One prefixed BMFF container | 358.5 ns | 293.4 ns | 2,424 | 1,864 |
| Sixteen prefixed BMFF containers | 4.92 us | 4.21 us | 31,784 | 22,824 |

All 26 cases succeed. The full comparison has zero flags at the script's 10% timing and exact
allocation thresholds, but that alone does not satisfy the owner's execution-time gate.
Token failure increases about 3%, and invalid nested-object validation about 5%; unchanged
validation controls also shift. These cases are rechecked rather than accepted for their
memory savings. The longer repeat runs all ten token/nested-validation cases baseline then
candidate, five warmups and twelve measurements. Token failure measures 77.5 → 76.9 ns in
that repeat, so its original increase does not repeat. The canonical retained comparison contains
the eighteen non-validation cases and has zero regression flags. Unchanged DNS decoder controls
have small timing increases; the write improvements are 16–19% faster and 51–54% lower allocation.

## Rejected validation candidates

The first candidate parsed selectors as spans and filled a string.Create result through a
callback. Invalid nested-object validation was about 5% slower in the full run and 6% slower
in the longer run. Valid collection timing also increased. That implementation is discarded.

A simpler second candidate reused the validated parent string and concatenated only its
separator and nested path. It used the isolated version
`10.0.0-preview.1.perf.batch.validation2` against the same original validation package,
with five warmups and twelve measured iterations, baseline then candidate. Invalid nested
validation improved from 1.97 to 1.84 us and collection failures from 14.45 to 14.40 us,
but valid collection execution was 12.33 → 14.13 us. Across the three comparisons, valid
collection execution increased about 5%, 14% and 15%.

Some unchanged FluentValidation controls also moved, so timing causation is uncertain. The
owner's execution-time requirement does not allow retaining the allocation saving with that
repeatable observed slowdown. Both candidates are rejected; no ObjectValidation code or
DESIGN change remains. The rejected comparisons preserve their flags: three in the first
longer repeat and two in the second, including unchanged controls. These are not hidden or
reported as successful comparison gates.

## Validation

The six full Release test suites pass **1,021 tests**, zero failures/skips:
ObjectMapping 112, ObjectValidation 291, Dns 74, IdentityModel.Token 18,
IdentityModel.Token.JsonWebToken 89, Content.Bmff 437. The JWT suite here is the downstream
consumer check before the later compact-parser investigation.

Nineteen selected compatibility cases pass against the original implementation: seven
sequence cases, four existing nested-source cases, six literal DNS wire cases (including
Unicode fallback and the maximum label length), and two exact sync/async BMFF prefix cases.

All five candidate packages pass strict SDK baseline API validation with attributes and
parameter names checked. A package-only ARM64 NativeAOT consumer publishes and executes
successfully with trim/AOT warnings treated as errors; it exercises eager mapping, nested
validation sources, mixed-case DNS compression, success/failure token validation and
sync/async BMFF round trips. The second validation candidate separately passes 291 tests,
strict API checks and an isolated NativeAOT consumer before its timing rejection. Those
functional checks do not override the execution-time gate. Existing nullable warnings in
untouched validation code remain.

Raw reports and comparisons are in the benchmark repository under
`benchmarks/libraries/Content/results/2026-10-22-performance-iteration-07/`.
The directory is shared evidence for this multi-area batch; it contains each benchmark's
original namespace in its report filename. Canonical before/after folders contain the eighteen
retained cases. `rejected-validation1-initial/` holds the original validation reports and full
26-case comparison; `rejected-validation1-repeat/` holds the first longer ten-case repeat,
including the accepted token recheck; `rejected-validation2/` holds the isolated eight-case
second candidate. Reports are copied without editing and verified
against source SHA-256 hashes. Test/package logs stay in cohesion's ignored `.codex-temp/`,
and the native fixture/output in the benchmark repository's ignored `.artifacts/performance/`.
