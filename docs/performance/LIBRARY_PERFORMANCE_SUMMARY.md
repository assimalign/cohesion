# Consolidated library performance investigation

This records the queued performance investigations on `codex/library-performance` in cohesion
and cohesion-benchmarks, completed across 2026-10-21 and 2026-10-22. The implementation
baseline is cohesion `c2b25e4ac`; the benchmark baseline is `7a6aa1a`. Existing iteration changes
are preserved. Work stays on those branches; work items remain In progress until merge.

## Scope and acceptance

The queue covers the eighteen in-scope `libraries/` areas and the flagged Web request paths.
`libraries/ApplicationModel`, `App`, `Amqp` and `OpenTelemetry` are excluded. No excluded
implementation change is retained. Public signatures, attributes, parameter names, package
boundaries and committed package pins are preserved. The repeated-media-parameter equality
policy was explicitly approved by the owner in iteration 5.

Execution time is the acceptance gate. Lower allocation alone does not justify a repeatable
slowdown. Measurements use isolated package versions and the companion repository's harness;
suspect timing deltas receive longer runs with reversed order. Local microbenchmarks do not
establish an end-to-end HTTP throughput gain.

For the ObjectMapping follow-up in iteration 9, the owner explicitly accepts configuration-only
startup cost. Its warm mapping paths retain the execution-time gate; other areas keep their prior gates.

## Retained changes from iterations 1–6

| Area | Change | Recorded result |
| --- | --- | --- |
| Cache | Lazily create token/callback storage; snapshot callbacks exactly; bind token invalidation to its original stored entry | Ordinary writes: 400 → 304 bytes; stale callbacks cannot remove replacement entries |
| Configuration | Validate keys before creating diagnostics; retain immutable string inputs; correct nullable key inequality | String key: 312 → 0 bytes; span key: 312 → 56 bytes; path parse: 1,000 → 232 bytes |
| Core | Cache glob token-length bounds in an internal pattern object | Fixed-pattern 64-path match: 6.66 → 2.47 microseconds, still allocation-free |
| Resilience.Timeout | Consume completed ValueTasks directly; use time-provider-aware WaitAsync for incomplete work; retain rentals until late completion | Synchronous case: 1.17 microseconds → 201.1 ns; 560 → 240 bytes |
| Connections | Traverse built-in named-feature layers directly while retaining custom/subclass dispatch and tombstones | Sixteen-layer absent lookup: 39.3 → 28.7 ns; zero allocations |
| Http | Stable insertion sorting for short Accept preference lists; avoid temporary single-parameter lists and builders for plain media types | Browser Accept: 2,384 → 1,536 bytes; plain media-type formatting: 47% faster, 160 → 56 bytes; charset parsing: 11% faster, 296 → 176 bytes |
| Http correctness | Repair equality for repeated media-type parameter names under the approved per-name occurrence-order policy | Equality laws and dictionary/set behavior covered; no public signature change |
| Web.Routing | Join catch-all segments directly over an array span | 528 → 480 bytes per catch-all match |
| Web.Serialization | Indexed registry/negotiation traversal; scan Vary as spans; keep custom advertisements live | Lookup: 43–47% faster, 32–256 → 0 bytes; negotiation saves 64–344 bytes; negotiated writes save 96–376 bytes |

The exact inputs, controls, rejected variants and timing limitations are in
[iteration 1](LIBRARY_PERFORMANCE_ITERATION_01.md),
[iteration 2](LIBRARY_PERFORMANCE_ITERATION_02.md),
[iteration 3](LIBRARY_PERFORMANCE_ITERATION_03.md),
[iteration 4](LIBRARY_PERFORMANCE_ITERATION_04.md),
[iteration 5](LIBRARY_PERFORMANCE_ITERATION_05.md) and
[iteration 6](LIBRARY_PERFORMANCE_ITERATION_06.md).

## Final queued investigation decisions

The queued pass is complete. The final sweep retains seven additional implementation changes
across seven packages. Two ObjectValidation candidates are rejected and fully reverted.

| Package | Additional retained change | Recorded result |
| --- | --- | --- |
| ObjectMapping | Reserve target-list capacity for counted sources; keep unknown-count mapping eager and single-pass | 100 items: 8.02 → 7.53 us; 16,624 → 15,288 bytes |
| Dns | Bounded label buffer; encode only literal labels; join compression suffixes without a builder | Writes 16–19% faster, 51–54% lower allocation |
| Content.Bmff | Materialize prefixes directly into exact buffers, preserving every callback and bound check | Prefixed saves 14–18% faster; 560 bytes saved per container in the fixtures |
| IdentityModel.Token | Allocate neutral validation diagnostics on the first finding | Success: 32 → 0 bytes; 5.6 → 2.6 ns; failure timing increase does not repeat |
| IdentityModel.Token.JsonWebToken | Scan compact delimiters before creating owned strings; fix malformed TryParse outcome | 48 bytes saved per valid parse; approximately unchanged parse time across pairs |
| Http.Forms | Scan type/disposition/boundary parameters as spans; materialize only returned values | Multipart 13–15% faster in the longer run, about 9–12% lower allocation; URL decoding saves 176 bytes |
| OpenApi.Versioning | Concatenate single-token diagnostic pointers directly | Pointer-heavy transforms 12–26% faster and 26–48% lower allocation in the longer run |

Evidence and rejected candidates are recorded in
[iteration 7](LIBRARY_PERFORMANCE_ITERATION_07.md) and
[iteration 8](LIBRARY_PERFORMANCE_ITERATION_08.md). Exact small timing deltas vary with controls.

This is a bounded review of the previously flagged paths, not a claim that every conceivable
optimization in each family has been exhausted. A review with no retained change is explicit.

| Area | Investigation and decision |
| --- | --- |
| Cache | `CreateEntry` already passes the cache owner directly, without a per-entry commit closure. Keep priority/LRU staging in `EnforceSizeLimit`: replacing it with a maintained ordering structure would add work and contention to ordinary accesses. Retain iteration 1 changes. |
| Configuration | Provider lookup already walks providers in reverse by index and uses dictionary alternate span lookup. Keep provider precedence and load ownership; retain key/path gains. Binder AOT work remains separately tracked by #1220. |
| Connections | Stream/pipe adapters already copy directly into caller memory, then advance exactly the consumed range. Keep the copy required by the Stream contract and existing flush/cancellation behavior. Retain iteration 3 lookup improvements. |
| Content | BinaryContentReader already returns borrowed spans; its ReadExactly copy fills caller-owned memory. BinarySegment materialization owns its result. Retain exact BMFF prefix buffers under #1695 / #1549; positional-read API work in #1549 is not part of this API-preserving pass. |
| Core | Retain cached glob bounds. The owner-requested review move nests the metadata as internal `Glob.GlobPattern`; matching behavior and public APIs stay unchanged. |
| DependencyInjection | Warm resolution already caches accessors and singleton call-site values. The scope dictionary also supplies synchronization and creation reservations. Keep that protocol and its reentrancy/disposal guarantees; no lazy-cache rewrite retained. |
| Dns | Retain bounded ASCII label encoding and cheaper compression suffix construction under #1693. Preserve invariant case folding, original emitted case, fallback bytes, pointer limits and decoder checks. |
| FileSystem | Recursive deletion snapshots precede child disposal, which can mutate membership. Watcher snapshots support callbacks outside locks; returned/materialized data owns its copy. Retain these snapshots and existing routing/normalization behavior. |
| Hosting | ResourceMount.FromBytes owns a defensive copy and OpenRead borrows that immutable owned buffer through a read-only stream. Lifecycle work is infrequent; retain ownership and startup/shutdown ordering. |
| Http | Retain Accept/media-type changes and form header span scanning (#1696). HTTP/1 line reading already scans buffered PipeReader sequences, with bounds and pooled continuation storage. Percent-decoding semantics remain unchanged. |
| IdentityModel | Retain lazy neutral diagnostic lists (#1694), compact segment delimiter scanning (#1698) and its malformed-input correction (#1699). Cryptographic verification, received signing bytes, result snapshots and trust policy stay intact. |
| Logging | Fan-out already uses indexed arrays, resolves filters at logger construction, and bypasses enrichment allocation when no enrichers exist. Keep sink failure isolation and event-forwarding recursion/disposal guards. No additional implementation change retained. |
| ObjectMapping | Retain initial sequence capacity (#1691) and typed built-in execution with lazy custom contexts, concrete action enumeration, nested collection improvements and small helper inlining (#1700). Preserve custom profiles, context identity, boxed values, live configuration and enumeration order. |
| ObjectPool | Both pool forms already use an allocation-free fast slot, interlocked retention accounting and ConcurrentQueue fallback. Keep the proven retention/concurrency algorithm; no queue replacement retained. |
| ObjectValidation | Keep the earlier #1692 source-composition candidates rejected. Retain deferred context queues, internal empty-error views and direct string checks under #1703 (iteration 10); preserve live collections, context identity, concurrency, timed invocations, declaration order, stop/cascade behavior, explicit sources and collection indices. |
| OpenApi | Keep superset-version deep copying and the common node model used by JSON/YAML; they preserve input independence and version fidelity. Retain the single-segment pointer append used during traversal (#1697). Attribute materialization remains construction work. |
| Resilience | Retain the completed timeout work and cancellation/late-completion ownership tests. |
| Security | PurposeChain already sizes its final UTF-8 info buffer exactly. AesGcmDataProtector caches that immutable info per protector, derives the subkey into stack memory and erases it after use. Keep this key lifetime and authenticated-encryption behavior; no longer-lived subkey cache retained. |
| Web | Retain routing and serialization request-path changes. Application feature snapshots, middleware ordering, cache completion ownership and transport framing are preserved. |

## ObjectMapping follow-up

[Iteration 9](LIBRARY_PERFORMANCE_ITERATION_09.md) targets AutoMapper 13.0.1 on Windows ARM64.
The final normal-affinity run improves all eight Cohesion member-mapping workloads against the
original package. Median existing-target mapping is 58.0 → 20.6 ns (88 → 0 bytes); runtime-Type
mapping is 77.1 → 33.7 ns (144 → 56 bytes). Flat mapping is near parity across runs and wins the
latest run, 47.7 ns versus AutoMapper's 54.3 ns, with both allocating 56 bytes.

Collections and graphs still trail AutoMapper: 100 flat items take 5.02 us versus 1.10 us, and a
100-item graph takes 4.08 us versus 970 ns. Their allocation improves to 6,488 and 6,712 bytes.
The additional top-level array loop is discarded because a 32-byte saving does not justify uncertain
CPU behavior. A single-core runtime-dispatch flag and the isolated recheck remain visible in the
report; normal-mask and single-core absolute times are kept separate.

A custom-profile callback flag also receives a matched eight-case repeat: 39.6 → 31.3 ns,
unchanged 80-byte allocation, with no flags in the repeat. Both measurements remain visible.
Iteration 9 preserves 46 full JSON reports, matching markdown tables and eight comparisons.

The follow-up passes 124 library tests, 3 generator tests, strict public API checks and a package-only
ARM64 NativeAOT consumer. Twelve new compatibility cases also pass the original package. No public
execution hook is added; a whole-object generated executor is a separate design investigation.

## ObjectValidation follow-up

[Iteration 10](LIBRARY_PERFORMANCE_ITERATION_10.md) improves all eleven Cohesion warm cases
against the original package. Successful registration is 3.05 -> 1.41 us (23,992 -> 3,528 B);
32 successful tags are 17.83 -> 7.06 us (157,424 -> 10,977 B). Deferred concurrent queues
preserve retained live collections and callback contexts. Six string rules avoid character
boxing/enumeration while retaining whitespace, null and UTF-16 behavior.

Failing registration and numeric/date comparisons beat FluentValidation 11.11.0 in the final
run, and failing nested cases are near parity. Successful validation still trails it; no universal
parity claim is made. Per-rule diagnostics remain enabled. The five final comparison flags all
belong to unchanged FluentValidation controls. The intermediate Cohesion nested flag and its
zero-flag repeat remain visible alongside all earlier rejected source-composition candidates.

The final validation suite passes 306 tests; all fifteen new compatibility cases also pass the
original package. Strict API and ARM64 NativeAOT checks pass. The companion preserves 26 full
JSON reports, matching markdown, four comparisons, eight-run metadata and verified report/package
hashes. Fresh delivery API checks cover all sixteen changed packages; the final mapping suite
also passes its 124 tests after the fixture layout cleanup.

The owner-requested Foundation ObjectMapping epic [#1701](https://github.com/assimalign/cohesion/issues/1701)
and DTO-to-entity expression-translation feature [#1702](https://github.com/assimalign/cohesion/issues/1702)
are filed in Backlog. That additive API work is not implemented or closed by these performance changes.

## Correctness work

- [#1679](https://github.com/assimalign/cohesion/issues/1679): nullable key inequality.
- [#1680](https://github.com/assimalign/cohesion/issues/1680): stale cache invalidation removing a replacement.
- [#1681](https://github.com/assimalign/cohesion/issues/1681): timeout completion/timer ownership work.
- [#1687](https://github.com/assimalign/cohesion/issues/1687): repeated media-type parameter equality, with owner-approved semantics.
- [#1699](https://github.com/assimalign/cohesion/issues/1699): malformed compact JWT parts now return false through TryParse instead of throwing for a whitespace-only header or payload. Constructors and valid-token text retain their behavior.

The excluded OpenTelemetry experiment was removed and #1682 deferred; it is not included in the
branch's closing manifest. Existing delivered parent items are not reopened, and implementation
items are not closed before merge.

## Validation and limits

The queued-pass checks recorded earlier on 2026-10-22 pass **1,118 tests across eight suites**: Mapping 112, DNS 74,
Token 18, BMFF 437, Http.Forms 62, JWT 102, OpenApi.Versioning 22, and 291 on restored
ObjectValidation. Earlier iteration reports retain their affected-suite results; these
counts are not added together as if repeated tests were distinct.

Each retained changed package passes strict API baseline validation including parameter
names and attributes. Focused package-only ARM64 NativeAOT consumers publish and execute
successfully. This is not a claim that every API has an AOT smoke case: the existing
ConfigurationBinder IL2070/IL3050 work remains tracked by #1220. Existing nullable warnings
in untouched form/validation code are not suppressed or folded into performance work.

New evidence includes 28 unedited, hash-verified full JSON reports across iterations7/8.
Earlier reports retain their raw evidence as well. Rejected validation comparisons and
their flags remain visible. The final sweep comparison has one unchanged ASP.NET Core
control's one-byte allocation flag; no report claims that all comparison gates passed.
Initial timing increases are retained alongside their longer rechecks. No repeatable
warm execution slowdown is accepted for the retained measured Cohesion paths. ObjectMapping's
configuration-only startup cost has the explicit owner exception described above.

No public signature change is needed for the retained work. The reviewed media-type
equality correction and the nonthrowing malformed-JWT correction are explicit behavior
fixes. The separate positional-read capability in #1549 would add a public seam and remains
outside this pass. All changes and evidence use the existing `codex/library-performance` branches. Pull requests carry delivery and CI status; merge and package publication are outside this pass.
