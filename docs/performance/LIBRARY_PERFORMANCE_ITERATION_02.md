# Library performance work: iteration 2

Continues `codex/library-performance` in both `cohesion` and `cohesion-benchmarks` on 2026-10-21.
Iteration 1's changes remain present. This pass investigates timeout completion in
`libraries/Resilience/Assimalign.Cohesion.Resilience.Timeout/`; the remaining area inventory is
in [iteration 1](LIBRARY_PERFORMANCE_ITERATION_01.md). The excluded ApplicationModel, App and Amqp
areas remain outside scope. No public API change is included or needed.

## Finding and retained change

`TimeoutResilienceStrategyBase.InvokeAsync` converted every callback outcome to a `Task` and
created a separate delay/race, even when its `ValueTask` had already completed. It also left
the losing `Task.Delay` timer armed after a callback finished. Timer retention is proportional
to call rate multiplied by the timeout budget, rather than the number of active operations.
The existing timeout benchmark documented this accumulation and used a one-second budget to
keep the baseline executable.

Impact is high for high-rate synchronous operations and long timeout budgets: the unnecessary
race allocated per call, and completed operations kept deadline timers alive. The change keeps
the same O(1) execution complexity but removes those allocations from synchronous outcomes and
limits active deadline timers to incomplete operations.

- Completed `ValueTask` outcomes are consumed directly and exactly once.
- Incomplete callbacks use `Task.WaitAsync(timeout, TimeProvider)`, whose deadline timer is
  disposed when either side completes.
- The cancellation source remains leased until the callback finishes. Deadline rejection still
  cancels the execution token and observes late completion without returning its source early.
- Timeout cancellation classification and `OnTimeout` invocation also run on the synchronous
  path. Caller cancellation and callback-thrown `TimeoutException` remain distinct from deadlines.
- The touched exception message now comes from the build-generated resource, preserving its text.

The lifecycle is documented in the timeout package's `docs/DESIGN.md`. There are no new
dependencies, public members, version pins, diagnostic suppressions or EventSource changes.

## Work item

[#1681](https://github.com/assimalign/cohesion/issues/1681) tracks the timer retention correction
and synchronous completion optimization. Search found no duplicate and no delivered leaf whose
acceptance criteria specifically owned timeout timer cleanup. It is therefore an ordinary feature
under Resilience #318 with the `bug` label, following policy B4, rather than a bug attached to an
unrelated delivered item. Project #13 status is In progress; it remains open until the fix merges.

## Measured results

Windows 11 ARM64, Snapdragon X X1E80100; SDK 10.0.402, runtime 10.0.13, BenchmarkDotNet 0.15.8.
Both packages contain iteration 1's preceding Core changes. Isolated package versions are
`10.0.0-preview.1.perf.timeout.before` and `10.0.0-preview.1.perf.timeout.after`, derived through
`CohesionPatchVersion`; neither repository's committed version changes.

| Workload | Median before | Median after | Allocated before | Allocated after |
| --- | ---: | ---: | ---: | ---: |
| Synchronous value | 1.17 us | 201.1 ns | 560 B | 240 B |
| Synchronous void | 969.9 ns | 244.5 ns | 552 B | 216 B |
| Value callback yielding once | 3.87 us | 2.39 us | 1,557 B | 1,469 B |
| Unchanged Polly control | 179.8 ns | 166.4 ns | 0 B | 0 B |

The synchronous value path allocates 57.1% less and the void path 60.9% less. Remaining allocations
are not eliminated by this pass. The async workload includes scheduler cost and completion races;
its allocation figures are averages rather than deterministic path counts.

The expanded comparison used one launch, five warmups and 15 measured iterations. Its unchanged
control improved about 21%, so the synchronous pair was repeated in reverse order. The repeated
control varied about 7.5%, while the large Cohesion improvement and exact allocation counts persisted.
Baseline timer accumulation also adds GC pressure; do not interpret all timing reduction as reduced
instruction count or extrapolate these measurements to application throughput.

Unedited JSON reports, the comparison and reproduction command are saved in the companion repository
at `benchmarks/libraries/Resilience/results/2026-10-21-performance-iteration-02/`. The comparison
reports zero regressions at its 10% timing / exact allocation thresholds. Existing benchmark names
and the one-second budget are preserved; a new class covers void and yielding callbacks.

## Validation

Six of the initial seven deterministic completion tests failed against the old implementation,
reproducing synchronous race allocation and retained timers after success, failure and caller
cancellation. All pass after the change. Additional cases cover inline timeout cancellation,
void async completion and an asynchronously generated infinite budget. Manual timer firing avoids
wall-clock timing assertions; per-await watchdogs only guard against a hung test.

All seven Resilience suites pass: Timeout 12, Retry 3, RateLimiting 2, CircuitBreaker 1, Hedging 2,
Resilience core 5 and Fallback 2, for **27 passed, zero failures or skips**. Release builds and
TRX results are retained in the ignored `.codex-temp/` directory.

Strict SDK package validation compares the timeout package against its baseline with strict mode,
parameter-name and attribute checks. The packaged managed smoke passes synchronous value, yielding
value and synchronous void execution with AOT analysis retained.

NativeAOT publication was attempted again for `win-arm64`. Managed compilation succeeded, then
the SDK reported `Platform linker not found`; native publication remains unverified. The local
ILCompiler `findvcvarsall.bat` explicitly requires `Microsoft.VisualStudio.Component.VC.Tools.ARM64`.
Install Desktop development with C++ with default components, the MSVC ARM64/ARM64EC build tools
component and its Windows 11 SDK, then rerun the native consumer.

Repository checks: ✅ internal-only production change; ✅ existing project/version/dependency
configuration preserved; ✅ generated exception resource; ✅ file layout, explicit usings and field
naming reviewed; ✅ lifecycle design updated; ✅ package-based measurements and unedited evidence;
✅ affected suites and strict API comparison; ✅ clean `git diff --check` in both repositories.
No new project, shared-source link, extension member, public API or diagnostic provider requires
the corresponding checklist sections. Native linking is the outstanding environment check.

## Follow-up in iteration 3

After the owner installed the Desktop C++ ARM64 tools, the existing timeout smoke consumer
published successfully for `win-arm64` and its native executable passed synchronous value,
yielding value and synchronous void checks. The earlier native-linking environment check is resolved.
The first iteration's native Key/Glob/MemoryCache/Route consumer also published and passed.
