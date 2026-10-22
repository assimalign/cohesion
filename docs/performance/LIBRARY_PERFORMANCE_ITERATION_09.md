# Library performance work: iteration 9 — ObjectMapping

This follow-up targets warm mapping execution on 2026-10-22, comparing the existing
AutoMapper 13.0.1 pin on Windows ARM64. Both repositories remain on
`codex/library-performance`; prior iterations and the owner-requested internal `Glob.GlobPattern`
nesting are preserved. The change is tracked by [#1700](https://github.com/assimalign/cohesion/issues/1700).

## Result and remaining gap

The retained mapper is faster than the original implementation in all eight Cohesion warm workloads
in the final normal-affinity check. It beats AutoMapper on existing-target and runtime-Type dispatch.
Flat mapping is near parity across runs and faster in the last run. Collections and nested graphs
still trail AutoMapper; allocation parity is not execution-time parity.

The following medians compare the original package with the retained `.inline` package. AutoMapper
is the unchanged 13.0.1 counterpart measured in the final run. The baseline is the matching subset
of the 32-case normal-affinity confirmation, not the pristine repository head.

| Workload | Original Cohesion | Retained Cohesion | AutoMapper in final run | Cohesion allocation before → after | AutoMapper allocation |
| --- | ---: | ---: | ---: | ---: | ---: |
| Existing flat target | 58.0 ns | 20.6 ns | 47.7 ns | 88 → 0 B | 0 B |
| Runtime-Type new target | 77.1 ns | 33.7 ns | 49.8 ns | 144 → 56 B | 56 B |
| Generic new flat target | 91.8 ns | 47.7 ns | 54.3 ns | 144 → 56 B | 56 B |
| Collection, 1 item | 90.9 ns | 82.8 ns | 73.9 ns | 240 → 152 B | 144 B |
| Collection, 100 items | 6.73 us | 5.02 us | 1.10 us | 15,288 → 6,488 B | 7,792 B |
| Nested graph, 1 item | 410.0 ns | 226.2 ns | 81.6 ns | 752 → 376 B | 280 B |
| Nested graph, 10 items | 961.8 ns | 555.2 ns | 193.8 ns | 2,288 → 952 B | 952 B |
| Nested graph, 100 items | 7.59 us | 4.08 us | 970.2 ns | 17,112 → 6,712 B | 7,136 B |

The latest flat counterpart is about 13% slower than its earlier control, while Cohesion's earlier
confirmation was 50.1 ns against AutoMapper's 46.0 ns. Therefore the result supports near parity
across runs and a latest-run win, not an unconditional claim that Cohesion always wins flat mapping.
The larger remaining gaps are roughly 4.6x for a 100-item flat collection, 2.9x for the ten-item
graph, and 4.2x for the 100-item graph in the final run.

These are microbenchmarks for the existing delegate/member profile shape, not a claim about
all mapping features, AutoMapper versions, architectures, or application throughput. The unchanged
AutoMapper controls vary between runs; small percentage differences do not establish a reliable win.

## Retained execution changes

- `MapperExecution` carries invocation state as a local value. Built-in reference-type actions
  execute without a public heap context. A custom action/profile receives one real `MapperContext`,
  created lazily and shared across matching profiles for that invocation. Nested objects and collection
  elements receive independent contexts; nothing is pooled or reused after a callback can retain it.
- Internal generic and runtime profile executors apply typed built-in actions. Source/target checks
  move from every member to the profile boundary, and the concrete action List enumerator avoids
  boxing while retaining its version checks.
- Generic helpers create `new TTarget()` and use a typed internal path for the sealed `Mapper` and
  reference-type targets. Custom `IMapper` implementations still receive the public primitive call
  and control its returned object. Runtime-Type dispatch keeps assignability checks.
- The mapper reads its concrete profile list directly, preserving live membership and declaration
  order. Handling flags are still captured once per invocation. No type-pair cache hides later
  profile changes.
- Nested collection execution snapshots matching profiles without LINQ, reserves counted source
  capacity, and directly enumerates exact built-in Lists and arrays. Derived Lists and other
  enumerables keep interface dispatch. Element mappings use local state; collection merge order,
  null behavior, conversion and the final supplied setter remain unchanged.
- Typed `MapAction` callbacks execute without a context or closure. Context callbacks and arbitrary
  public `IMapperAction` implementations keep their public contract.
- Exception literals in touched files move unchanged into the existing resource file. No new public
  signature, attribute, parameter name, package boundary or dependency is introduced.

Value-type profiles retain the context path. A callback can mutate a boxed source through
`Unsafe.Unbox<T>` and later actions must read that same box. Likewise, a generic value target must
return the mutated boxed result. Derived profiles can reimplement the public interface action list;
that list remains authoritative. Public action-list replacements and mutation checks are preserved.

## Compatibility and native validation

The full library suite passes **124 tests**, with **3 source-generator tests** passing separately. All
twelve new compatibility cases also pass against the unchanged package baseline. Strict package API
validation succeeds with parameter-name and attribute checks enabled. The final package publishes and
executes as Windows ARM64 NativeAOT with trim/AOT diagnostics treated as errors.

The native smoke covers class-based and inline generated profiles, nested paths, generic creation,
runtime dispatch, sequences with independent targets, and callback contexts retained across calls.
Existing expression-fallback warnings remain in tests; no broad warning suppression is added.

The source review found that the old DESIGN described an immutable options snapshot, while the
implementation retains the supplied options and mutable profile/action lists. This pass preserves that
observable behavior and corrects the documentation. Configuration must remain unchanged during
concurrent use; invocation state is independent.

## Measurement method and evidence

The runner consumes isolated local packages, never producer project references. Versions derive
through CohesionPatchVersion: `10.0.0-preview.1.perf.mapping.before`, `.candidate1`, `.final`, `.final2`,
`.final3`, and `.inline`. Core is packed at the matching version; the committed benchmark package pin and AutoMapper
pin remain unchanged. Final3 retains the exact-List guard for custom subclass enumeration and drops
the extra top-level array-loop experiment. The retained `.inline` version adds inlining hints to
small private/internal helpers.

Machine: Windows 11 ARM64, Snapdragon X X1E80100, SDK 10.0.402/runtime 10.0.13,
BenchmarkDotNet 0.15.8 in-process harness. No tests, producer builds or native compilation overlap
measurement samples. Warm execution time remains the acceptance gate. The owner explicitly accepts
configuration-only startup cost in this ObjectMapping follow-up; its flags are reported separately.

- The original 24-case baseline uses five warmups and twenty measurements.
- The initial 32-case candidate uses three warmups and eight measurements.
- The complete confirmation runs candidate then baseline with five warmups and twelve measurements.
- Configuration-only setup receives a focused four-case repeat at five warmups and twenty measurements.
- A twelve-case sequence/graph follow-up tests the top-level array-loop experiment; that experiment is discarded.
- A sixteen-case `.final3` sensitivity check is pinned to CPU 8 (affinity 256), verified on the child process.
- The `.inline` refinement receives an isolated runtime probe and a matched two-case runtime repeat,
  five warmups and twenty measurements, on that core.
- The final `.inline` package receives sixteen normal-affinity member-mapping cases and eight
  separate callback/profile controls, each with five warmups and twelve measurements.
- A matched eight-case callback repeat runs baseline then `.inline` with five warmups and
  twelve measurements, also at normal affinity; the child process is verified at mask 4095.

The initial comparison has no flags. The complete confirmation has six: two Cohesion
configuration-only timing flags and four AutoMapper configuration allocation flags (one also has a
time flag). The focused configuration repeat has three: Cohesion graph setup and two unchanged
AutoMapper controls. Graph setup is 282.8 → 340.9 ns in confirmation and 326.9 → 363.9 ns in the
repeat; flat setup changes direction. Setup allocates the same 464/1,224 bytes. The owner explicitly
accepted configuration-only startup cost during this investigation. It is not silently counted as a
passed warm-execution check, and the varying controls limit causal attribution.

The extra top-level array/List loop saves another 32 bytes, but its benefit is uncertain amid a
slower collection measurement and substantial counterpart drift. It is discarded. The original
interface enumeration remains in the public sequence helper. The exact-List optimization retained
inside nested collection actions preserves subclass interface enumeration and has compatibility tests.

The sixteen-case single-core sensitivity run shows one runtime-dispatch flag: 74.3 → 126.4 ns.
Small internal dispatch/scalar helpers receive explicit inlining hints. A matched isolated two-case
runtime repeat then measures 434.7 → 334.8 ns, with AutoMapper 291.5 → 313.6 ns, and no flags.
Single-core absolute times differ sharply from the normal-mask results; they are kept separate.
The original sixteen-case single-core order was not repeated after inlining. The isolated repeat
and final normal-affinity matrix improve; they do not prove that every single-core/case-history
combination is unaffected, or isolate inlining from environmental effects.

The final normal-affinity sixteen-case matrix has two flags, both unchanged AutoMapper timing
controls (one-item collection and flat mapping). All eight Cohesion warm cases are faster than the
original package and allocate less. No comparison output or sensitivity flag is suppressed. The
configuration and callback rows omitted from this focused repeat remain in the complete 32-case
confirmation; they were not removed from the benchmark suite.

The first final callback check has two flags: an unchanged AutoMapper context-callback control
and Cohesion's arbitrary custom-profile callback, 41.3 → 51.3 ns. A matched repeat of all eight
callback cases has no flags. Custom-profile dispatch measures 39.6 → 31.3 ns with unchanged 80-byte
allocation; context callbacks measure 30.1 → 17.1 ns (88 → 48 B), typed callbacks 27.3 → 7.9 ns
(88 → 0 B), and overridden profiles 36.8 → 36.0 ns (80 B). The custom-profile slowdown does not
repeat. Both results are preserved; the repeat does not establish that every application workload
or machine condition is unaffected. No repeatable warm slowdown is accepted.

Forty-six unedited full JSON reports, their markdown tables, eight comparison reports, package hashes
and metadata for sixteen runs are preserved. The final native/API checks use the retained `.inline` package.

Raw full JSON reports and markdown tables are preserved in the benchmark companion at
`benchmarks/libraries/ObjectMapping/results/2026-10-22-performance-iteration-09/`.
`SHA256.csv` verifies the copies against the original reports; `PACKAGES.csv` identifies the tested
package binaries. Initial and confirmation measurements remain separately visible.

Eight new callback/custom-profile benchmark cases supplement the original 24 without renaming them.
They copy the same four values through explicitly supplied user code and compare against the same
AutoMapper map. They measure the extension-point cost, not automatic member/convention mapping;
their faster results are reported separately and do not prove a general AutoMapper win.

## Next lever to investigate

The current source generator registers individual getter/setter actions. Every member still pays
its own getter, setter, dispatch and null-catching costs. Collection elements repeat that work, and
caller-supplied `items.ToList()` setters can copy a buffer the mapper has already materialized.

A whole-object generated executor is the next candidate for reducing these calls. The typed callback
control shows a low dispatch floor when the caller supplies one complete copy operation; it does
not prove a generated executor will reach that timing with all null/default policies and custom
behavior preserved. A protected execution hook would expand the public API and needs owner review.
No such hook or generated-profile representation change is introduced here. Any proposal must
explicitly address action-list observability, fallback profiles, null getters, declaration order and
collection ownership before it replaces the existing public profile behavior.
