# ObjectValidation execution performance — iteration 10

This follow-up compares the existing FluentValidation 11.11.0 pin with the performance
branch's original ObjectValidation package on Windows ARM64. It is tracked by
[#1703](https://github.com/assimalign/cohesion/issues/1703). The two earlier nested-source
composition candidates under #1692 remain rejected; their string algorithms are unchanged.

## Retained execution design

ValidationContext now creates its concurrent error and invocation queues on the first write
or public collection read. LazyInitializer publishes one winning queue under concurrent first
access. Public Errors/Invocations still return stable live queues: retained empty collections,
results created before later writes and custom callbacks observe the existing behavior. No
context or queue is pooled, and errors still clone their supplied error objects.

Internal scalar, collection and nested-profile evaluation can enumerate a built-in context
that has no errors without creating its error queue. This internal empty view is consumed
immediately; it is never returned to public callers. Arbitrary public context implementations
retain their own Errors enumeration. Per-rule Stopwatch timing, invocation records, evaluation
order, stop/cascade policies, source composition, collection indices and exception behavior remain.
The null-instance diagnostic moves unchanged to generated ErrorMessages resources.

Configuration and validation complexity remain linear in the profile/rule/element counts.
The change removes allocation of unused queue segments. Six string rules now return their direct whitespace/length checks for every string, avoiding fallback to character enumeration and boxing. Length checks become constant-time with the same UTF-16 counts. Non-string collection/enumerable behavior stays unchanged. No validation work or invocation record is omitted.

## Results

True/False denotes the valid/invalid fixture; all table values are medians.

| Workload | Original Cohesion | Retained Cohesion | FluentValidation in final run | Cohesion bytes before → after |
| --- | ---: | ---: | ---: | ---: |
| Tags, 32 items (False) | 19.79 us | 7.15 us | 8.38 us | 157240 → 21538 |
| Tags, 32 items (True) | 17.83 us | 7.06 us | 5.05 us | 157424 → 10977 |
| Tags, 4 items (False) | 2.81 us | 1.15 us | 960.4 ns | 21464 → 4552 |
| Tags, 4 items (True) | 2.56 us | 987.0 ns | 593.6 ns | 21472 → 3232 |
| Nested collection, 8 items (False) | 17.22 us | 14.35 us | 14.78 us | 137304 → 82777 |
| Nested collection, 8 items (True) | 16.83 us | 5.89 us | 3.82 us | 126040 → 26899 |
| Nested address (False) | 2.56 us | 2.05 us | 2.20 us | 17200 → 11761 |
| Nested address (True) | 1.85 us | 1.14 us | 564.7 ns | 16056 → 5041 |
| Profile build, collection=False | 2.26 us | 2.20 us | 1.91 us | 6112 → 6112 |
| Profile build, collection=True | 4.12 us | 4.50 us | 4.25 us | 10952 → 10952 |
| Successful registration | 3.05 us | 1.41 us | 950.3 ns | 23992 → 3528 |
| Failing numeric/date comparisons | 2.64 us | 2.26 us | 3.76 us | 21128 → 8553 |
| Failing registration | 2.72 us | 1.83 us | 3.69 us | 21672 → 6945 |


The final run improves all eleven Cohesion warm validation cases. Successful registration is
3.05 -> 1.41 us (23,992 -> 3,528 B); 32 successful tags are 17.83 -> 7.06 us
(157,424 -> 10,977 B). Failing registration is 2.72 -> 1.83 us against FluentValidation's
3.69 us, and numeric/date comparisons are 2.64 -> 2.26 us against 3.76 us. The invalid
32-tag case wins the final run, 7.15 us against 8.38 us. Invalid nested cases are near
parity; their small counterpart advantage does not establish a reliable win across runs.

Successful validation still trails FluentValidation: registration is 1.41 us against 950 ns,
32 tags 7.06 us against 5.05 us, and a nested address 1.14 us against 565 ns. Full
counterpart parity is not achieved. Timed invocation records and the existing reflection-only
member access remain; their presence does not isolate how much of the remaining gap each causes.
A source-generated getter path or an explicitly reviewed option for diagnostics are future
investigations, not public API changes introduced here.

The initial comparison has no flags. The intermediate queue confirmation has ten: nine
unchanged FluentValidation timing controls and Cohesion invalid nested validation at 2.13 ->
2.38 us. A matched eight-case nested repeat has no flags: invalid nested 1.85 -> 1.60 us,
invalid nested collection 17.80 -> 13.69 us, with all four Cohesion cases improving. The
original slowdown does not repeat. The subsequent string refinement receives its own complete
26-case original/final comparison. Its five flags are all unchanged FluentValidation timings;
all eleven Cohesion warm cases improve. All raw flags are preserved, including the intermediate
Cohesion flag, rather than presenting every comparison as a successful gate.

Profile construction is separate: without the collection, 2.26 -> 2.20 us; with it,
4.12 -> 4.50 us. Allocation is unchanged at 6,112/10,952 B. The constructor path is unchanged,
and counterpart construction timings also vary (the collection counterpart is 16% slower).
No setup speed improvement is claimed. Small deltas and counterpart variation limit causal
attribution; these are local microbenchmarks, not application-throughput guarantees.

## Compatibility and native checks

The complete Release suite passes 306 tests (291 existing plus fifteen storage/string compatibility cases).
All fifteen new cases also pass the original package. Four cover retained empty collections, live
result collections, 128 concurrent first writes and callback contexts retained across validations.
Eleven more cover null/empty/Unicode whitespace, UTF-16 surrogate/combining strings and a
4,096-character string through all six rules. Public null handling, including five failures
across the six-rule null fixture, is verified against the original package. Repeat executions
are not counted as additional distinct tests.

Strict package API validation passes with attribute and parameter-name checks enabled. A
package-only Windows ARM64 NativeAOT consumer publishes and runs with trim/AOT diagnostics treated
as errors, checking long and surrogate-pair strings, nested sources, successful invocation records,
concurrent writes, retained public collections and captured custom contexts. Existing nullable warnings are unchanged and
are not broadly suppressed.

## Measurement method

Machine: Windows 11 ARM64, Snapdragon X X1E80100, SDK 10.0.402/runtime 10.0.13,
BenchmarkDotNet 0.15.8 in-process repository harness. Core and ObjectValidation use matching isolated
versions derived through CohesionPatchVersion: 10.0.0-preview.1.perf.validation10.before,
.candidate1, .final and .strings. The retained package is .strings. Committed producer/benchmark
pins and the FluentValidation version remain unchanged. Runners consume packages, not producer
project references.

The existing 26-case matrix includes 22 warm validation cases and four profile-build cases.
Both sides evaluate equivalent rule outcomes; setup checks all error counts. Registration chains
stop on their first failure; the dedicated collection/nested cases continue on both sides.
Cohesion retains full timed invocation diagnostics, which the FluentValidation result does not
provide. The comparison does not disable this work to claim parity.

The initial pair runs original then candidate1 with three warmups and eight measured iterations.
The refined .final package also avoids forcing empty queues in nested-profile evaluation. The
intermediate confirmation reverses package order, final then original, with five warmups and twelve
measured iterations. A matched eight-case nested repeat uses five warmups and twenty measurements.
The retained .strings refinement then receives a complete 26-case pair, strings then original,
with five warmups and twelve measurements. The runs use normal affinity; the sampled child
processes have mask 4095. Producer builds, tests and native compilation do not overlap samples.

The companion preserves eight runs with 26 unedited full JSON reports, 26 markdown tables,
four comparison reports, 52 verified report hashes and eight Core/Validation package hashes.
The metadata records every version, case count, order and sample count. The normal-affinity
command is used throughout; child masks of 4095 are verified in the intermediate confirmation,
nested baseline and final-string baseline. The final-string candidate's mask is not separately
sampled. No code changes affinity between those two commands.

Raw evidence lives at `benchmarks/libraries/ObjectValidation/results/2026-10-22-performance-iteration-10/`.
Git attributes preserve raw report bytes despite line-ending conversion, so their SHA-256 records
remain verifiable after checkout. The final source also receives fresh strict API validation;
the other fifteen changed packages have fresh delivery API checks as well.

## Delivery boundaries

No public signature, attribute, parameter name, package boundary or dependency changes. The
original per-member contexts and public ConcurrentQueue behavior remain. ApplicationModel, App,
Amqp and OpenTelemetry stay excluded. The earlier configuration-cost exception for ObjectMapping
does not waive ObjectValidation's warm execution gate.

Separately, the owner-requested Foundation ObjectMapping epic #1701 and its expression-translation
feature #1702 are filed in Backlog. DTO-to-entity predicates/orderings require a reviewed additive
contract; that feature is not implemented or closed by these performance changes.
