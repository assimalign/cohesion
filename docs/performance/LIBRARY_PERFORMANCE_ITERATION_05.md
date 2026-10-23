# Library performance work: iteration 5

Continues `codex/library-performance` in both repositories on 2026-10-21. This iteration
investigates `HttpMediaType` parsing, rendering and a discovered repeated-name equality
defect. Earlier iterations remain present. ApplicationModel, App, Amqp and OpenTelemetry
remain excluded. Public signatures, dependencies and committed version pins are unchanged.

## Allocation findings

`ParseParameters` previously allocated a temporary list and its backing array for the
first valid parameter, then copied that value into its final array. A single charset or
boundary parameter now stays in a local value until parsing finishes. A second valid
parameter creates the same list as before, seeded with that first value. Malformed segments
do not create storage, and every valid occurrence retains its name, value and position.
The null/array representation and struct layout are unchanged. Parsing remains linear in
the input and parameter count; the one-parameter path removes temporary allocations.

`ToString` previously created a builder and buffer even for a media type without parameters.
That case now concatenates type, slash and subtype directly into the final string. Default
still renders empty; parameterized values retain the existing builder and escaping logic.
Rendering remains linear in output length, with only the result string allocated on the
no-parameter path. There is no rendering cache or retained request state. Per-call impact
is low, but repeats for callers parsing or rendering typed Content-Type and media ranges.

## Discovered equality defect and owner decision

The existing parser accepts and retains repeated parameter names. Equality looked up only
the first matching name on the other value. Separately parsed `text/plain; x=a; x=b` values
therefore compared unequal, and comparison with `text/plain; x=a; x=a` was asymmetric.
This was reproduced against the original implementation before the equality change.

The owner approved this policy in chat: **ignore ordering between distinct names, preserving
value order within each repeated name**. Names and values keep their existing case-insensitive
comparison. Thus `x=a; y=c; x=b` equals `y=c; x=a; x=b`, but differs from `x=b; x=a; y=c`.
First-occurrence lookup, parser storage and hashing stay unchanged. This is a public behavior
correction explicitly reviewed by the owner, with no public signature change.

Equality now consumes the next unmatched occurrence of each name. Identical sequences
return in O(n); other matches use a stack bitmap through 4,096 parameters, at most 512 bytes.
Larger inputs use a heap bitmap, preserving the same algorithm and bounded stack use.
The owner explicitly disallowed execution slowdowns in exchange for memory savings, so
the initial occurrence-counting fallback and slower matching layouts were rejected.
Worst-case complexity remains O(n²). Shared parameter arrays return immediately,
and one-/two-parameter cases use direct parameter comparisons outside the stack-allocating
private matcher. The package DESIGN records
the approved policy and allocation strategy.

## Tracking

- [#1686](https://github.com/assimalign/cohesion/issues/1686): planned parser/formatter
  allocation work under #771. The Http epic has reached its 100-child limit.
- [#1687](https://github.com/assimalign/cohesion/issues/1687): discovered equality defect,
  filed as an ordinary bug-labelled item under the owning feature per policy B4 because no
  delivered leaf explicitly defines duplicate-name identity. The owner approval is recorded
  in the issue. It is included in this branch's closing manifest after approval.

Both items remain In progress until merge; the delivered parent remains closed.

## Measurement design

Package-only benchmarks use `10.0.0-preview.1.perf.media.before` and
`10.0.0-preview.1.perf.media.final4`. Both include all earlier iteration improvements; the baseline
`HttpMediaType` is the original implementation, and only this type differs in the candidate.
Versions are derived through `CohesionPatchVersion`. The intermediate `media.after` package
was used only for API/native validation before the equality fix; it is not final timing evidence.

The initial baseline matrix has sixteen new parsing/rendering cases over plain, charset,
multiple-parameter and escaped-value inputs, plus six existing Content-Type/Range parser
controls. Eight additional unique-name equality controls cover original/reversed parameter
order at 1, 2, 65 and 4,097 parameters. They use valid inputs that compare equal in both versions;
incorrect old duplicate-name comparisons are regression tests rather than timing comparisons.

ASP.NET Core counterparts parse and format the same canonical text. Formatting starts from
parsed values, and a counterpart may retain/reuse its original string. It is not a fresh-string
allocation requirement. Existing benchmark identifiers and inputs remain unchanged.

Machine: Windows 11 ARM64, Snapdragon X X1E80100, SDK 10.0.402 / runtime 10.0.13,
BenchmarkDotNet 0.15.8 with the repository's in-process harness. Runs use one launch, three
warmups and eight measured iterations. Builds, tests and native compilation are serialized
away from timing samples. Results use medians and exact allocation bytes; unchanged controls
help expose environmental variation. Local parser/formatter gains do not establish HTTP
request throughput gains.

The first 28-case run and two subsequent equality variants exposed execution regressions
and were rejected. Their raw results remain under `initial/`, `rejected-equality2/` and
`rejected-equality3/`. The retained matcher separates name search from bitmap state and
keeps stack setup outside small comparisons. The final matrix combines 22
parsing/rendering/control cases and eight equality cases, with candidate-before-baseline
order. A longer one-case recheck uses five warmups and twelve measurements: the two-parameter
reversed case's roughly 5% increase in the full matrix does not repeat (20.1 ns baseline,
17.4 ns candidate). Those raw results remain under `small-repeat/`.

### Retained results

| Workload | Median before | Median after | Bytes before | Bytes after |
| --- | ---: | ---: | ---: | ---: |
| Render plain media type | 18.2 ns | 9.7 ns | 160 | 56 |
| Parse a charset parameter | 121.2 ns | 108.3 ns | 296 | 176 |
| Parse multiple parameters | 203.4 ns | 208.7 ns | 424 | 424 |
| Parse escaped parameters | 239.4 ns | 226.2 ns | 424 | 424 |
| Compare 65 ordered unique parameters | 6.27 us | 324.3 ns | 0 | 0 |
| Compare 65 reversed unique parameters | 6.54 us | 4.83 us | 0 | 0 |
| Compare 4,097 ordered unique parameters | 19.10 ms | 28.20 us | background noise | 0 |
| Compare 4,097 reversed unique parameters | 18.86 ms | 16.94 ms | background noise | one bitmap |

The two memory-saving paths also improve measured execution time: plain rendering is
about **47% faster with 65% fewer bytes**, and charset parsing is about **11% faster with
40.5% fewer bytes**. No allocation saving was accepted in exchange for a repeatable CPU
slowdown. Multiple-parameter parsing is about 3% higher in this pair but was about 4% lower
initially; escaped-parameter parsing also changes direction. Unparameterized parsing varies
about 2% across the pairs. Those small differences are not treated as established code gains
or losses. The longer two-parameter equality repeat is linked above for the same reason.

The final comparison has all thirty matched cases and no failed measurements. Its automatic
gate flags two unchanged ASP.NET Core formatting controls (11%/13% timing movement) and the
large reordered equality allocation. These are reported rather than hidden: the counterpart
timing direction reverses from the initial pair, and its code is unchanged. The large bitmap
is an intentional correctness cost that keeps execution faster and bounds stack use. Its
4,097-entry array is 544 bytes; BenchmarkDotNet reports 618 bytes/op versus 42 bytes/op
background noise in the old long-running case. Ordinary comparisons remain allocation-free.
Large-list counts are stress cases, not expected ordinary Content-Type headers.

Unedited full JSON, rejected candidates, comparisons and reproduction instructions are in
`cohesion-benchmarks/benchmarks/libraries/Http/results/2026-10-21-performance-iteration-05/`.

## Validation

All fourteen new storage/rendering compatibility cases pass against the original implementation.
They cover zero/one/multiple and duplicate parameters, malformed segments, canonical casing,
quoted delimiters, escaping, default values and long single values. Fourteen equality-law cases
cover reflexivity, symmetry, transitivity, dictionary/hash-set use and 63/64/65/4,096/4,097-entry
boundaries. The original implementation fails twelve of these fourteen equality cases, while
the corrected implementation passes all of them.

Final Release suites: HTTP **1,837 passed** and Web.Serialization **50 passed**: **1,887 passed,
zero failures/skips**. The final package builds with zero warnings/errors and passes strict
SDK API validation, including parameter names and attributes, against the baseline package.
No suppression is added. The package-only `win-arm64` NativeAOT consumer publishes and executes
successfully with trim/AOT diagnostics treated as errors. It checks rendering/parameter
storage, dictionary lookup and both stack and 4,097-entry heap bitmap matching.

Repository checklist: no new shipped type, field, dependency, global using, exception text,
extension member, EventSource or MSBuild change; naming, placement, resource and AOT rules
remain intact. No diagram was added or changed. The public behavior correction was explicitly
approved before implementation. Temporary runners, package feeds and consumers remain ignored
in the companion benchmark repository.

## Next investigation

Continue into a Web consumer or another library area with a measurable allocation or algorithm
cost. Keep subsequent candidates separate from these media-type measurements.
