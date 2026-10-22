# Library performance work: iteration 8

This is the final small-parser sweep of the previously flagged queue, on 2026-10-22, using
`codex/library-performance` in both repositories. It covers Http.Forms, compact JWT parts
and OpenApi.Versioning pointer construction. Earlier accepted changes remain present;
ApplicationModel, App, Amqp and OpenTelemetry remain excluded.

## Changes under measurement

- **Http.Forms (#1696):** scan Content-Type and Content-Disposition parameters as spans;
  trim and unquote spans before creating the returned value string. The multipart boundary
  helper also materializes only its final value. Existing name matching, semicolon grammar,
  duplicate precedence, charset selection and limits remain unchanged.
- **JWT parts (#1698):** locate the two separators and reject extra separators before
  allocating the three owned strings. Segment text and the exact received signing input
  remain unchanged. Direct constructors keep their existing validation and exceptions.
- **OpenApi.Versioning (#1697):** concatenate single-token pointer appends directly;
  keep the builder for other shapes. RFC 6901 escaping and diagnostic strings stay identical.

The form reader's percent-decoding is deliberately unchanged. During compatibility testing,
an initial test assumed that `%E9` was decoded using the Content-Type charset. The original
reader instead leaves it literal through its existing Uri.UnescapeDataString path. Corrected
charset tests use raw Latin-1 bytes and prove charset selection independently of that
pre-existing decoding behavior. This pass does not silently broaden that behavior.

## Confirmed defect

[#1699](https://github.com/assimalign/cohesion/issues/1699) records a native bug under delivered
JWT document story #609: `JsonWebTokenParts.TryParse(" .b.c", out _)`, `"a. .c"` and
`"\t.b."` throw ArgumentException through the three-argument constructor. Original-source
tests reproduce all three failures; ten valid/delimiter compatibility cases pass.

TryParse now returns false with null parts for whitespace-only encoded headers or payloads.
This is a malformed-input failure-behavior correction, with no public signature change.
Valid inputs, constructor behavior, empty signatures, signing bytes, signature verification
and trust policy are unchanged. All thirteen parsing cases and the complete JWT suite pass.

## Validation

Full Release suites pass **186 tests**, zero failed/skipped: Http.Forms 62,
IdentityModel.Token.JsonWebToken 102 and OpenApi.Versioning 22. Eight new form/pointer
compatibility cases also pass the original implementation; the JWT original-source
run has the expected ten passes and three reproduced defect failures.

All three packages pass strict SDK API validation against their original baseline,
including attributes and parameter names. Existing nullable warnings in untouched form
code remain; no suppression or unrelated warning cleanup is added.

A package-only Windows ARM64 NativeAOT consumer publishes and executes successfully with
trim/AOT warnings treated as errors. It exercises valid/refused compact parts, charset and
multipart parameter handling, exact escaped pointers and transform source independence.

## Measurement design

Package versions are `10.0.0-preview.1.perf.sweep.before` and
`10.0.0-preview.1.perf.sweep.after`, derived through CohesionPatchVersion with all dependencies
packed locally. Both contain the retained neutral-token improvement from iteration 7.
The committed CI pin is unchanged; the runner consumes packages, not producer sources.

The sixteen-case matrix contains eight existing form decoding cases, four existing signed
JWT parsing cases, and four new public-transform pointer cases. ASP.NET Core and Microsoft
IdentityModel counterparts are unchanged controls. Same-version OpenAPI deep-copy cases
avoid the changed walker and supply a second control. Setup checks results before timing.
JWT parsing benchmarks do not verify signatures on either side.

Machine: Windows 11 ARM64, Snapdragon X X1E80100, SDK 10.0.402 / runtime 10.0.13,
BenchmarkDotNet 0.15.8 in-process harness. Candidate runs before baseline, three warmups
and eight measured iterations. No tests/builds/native compilation overlap timing samples.
Execution time is the acceptance gate; allocation improvements alone are insufficient.

## Initial timing flags and recheck

The initial sixteen-case matrix completes successfully but has five comparison flags:
unchanged ASP.NET Core multipart at 32 fields (time and a one-byte allocation-report
difference), Cohesion URL-encoded decoding at 4 fields, both unchanged same-version
OpenAPI copy controls, and Cohesion RS256 parsing. Several additional changed cases have
smaller positive timing deltas. The ASP.NET Core multipart control is 32% slower, showing
that small deltas cannot be attributed confidently from this run alone. All flags and raw
reports remain under `initial/`; no failed comparison is presented as a successful gate.

The complete matrix is rechecked baseline then candidate with five warmups and twelve
measurements. In that repeat all four form cases are faster. At four fields, multipart
is 5.31 → 4.51 us with 19,105 → 17,313 bytes; at 32 fields, 37.78 → 32.86 us with
117,889 → 103,192 bytes. URL-encoded cases are 1.91 → 1.74 us and 12.09 → 12.00 us,
each saving 176 bytes. The small URL case's ASP.NET Core control also improves about 9%,
so the precise small timing gain is not uniquely attributable to the implementation.
The original form increases do not repeat; their allocation reductions remain.

The pointer-walking transform improves from 7.07 to 6.24 us at depth 1 (32,776 → 24,320
bytes), and 37.98 to 28.16 us at depth 16 (242,545 → 125,128 bytes). Same-version copy
controls are within about 1% in this repeat, so their initial 11–12% shifts do not persist
at that magnitude. Pointer-heavy traversal, not a different deep-copy policy, produces
the retained allocation saving.

RS256 parsing's initial 10% increase also does not repeat: 1.74 → 1.72 us in the longer
run. HS256 is 1.73 → 1.78 us (+2.7%) after an initially faster result; its direction changes.
Both remove 48 bytes per parse. The report claims this saving with approximately unchanged
parse time, not a reliable JWT throughput improvement. Signature verification remains outside
the benchmark and unchanged in production.

All sixteen cases succeed. The longer comparison retains **one flag**, the unchanged
32-field ASP.NET Core multipart control's allocation value of 374,465 → 374,466 bytes.
Its time is 56.64 → 56.39 us, and no counterpart code or input changed. The raw one-byte
asynchronous allocation-report difference is preserved; the report does not claim every
comparison check passed. No repeatable Cohesion execution slowdown is observed in these
measured modified paths. Timing variation limits claims about small deltas.

The three implementations are retained. The benchmark repository stores twelve unedited,
SHA-256-verified full JSON reports at
`benchmarks/libraries/OpenApi/results/2026-10-22-performance-iteration-08/`:
canonical `before/` and `after/` contain the longer run, and `initial/` contains the first
matrix and its five original flags. Each comparison is generated from its corresponding
reports. The producer's ignored `.codex-temp/sweep-*` logs hold tests/API validation;
the benchmark repository's ignored `.artifacts/performance/sweep-*` logs/fixtures hold
measurement and native consumer execution details.

The complete queue disposition and retained changes are consolidated in
[LIBRARY_PERFORMANCE_SUMMARY.md](LIBRARY_PERFORMANCE_SUMMARY.md).
