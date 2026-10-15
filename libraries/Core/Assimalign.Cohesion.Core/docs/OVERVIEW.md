# Assimalign.Cohesion.Core

## Summary

Provides the foundational primitives, frozen `COHESION_*` runtime contract, typed `System.Uri`
endpoints, environment helpers, glob support, and low-level extensions shared across Cohesion.

## Current Evaluation

- Status: Partial
- Production source files: 59; key type candidates discovered: 10; test files discovered: 13.
- Project references: None
- Package references: None
- NotImplementedException markers: 8

## Primary Responsibilities

- Core value types such as Size and Glob capture reusable concepts that show up across multiple libraries.
- Exception and system extension helpers centralize small cross-cutting behaviors.
- The library is dependency-light by design so other Core packages can reference it safely.

## Key Types

- AdaptiveMemoryPool
- AdaptiveMemoryPoolOptions
- AdaptiveMemoryPoolPressurePolicy
- AdaptiveMemoryPoolSnapshot
- AIMetadataAttribute
- AppEnvironment
- AppEnvironment.Variables
- UriExtensions
- Ulid
- AsyncExtensions
- CertificateManager

## Adaptive memory-pool configuration

Set `AdaptiveMemoryPoolPressurePolicy.PeakRetentionRatio` to a finite fraction from zero to one,
inclusive. Invalid assignments throw `ArgumentOutOfRangeException` and preserve the previous
value. NaN is rejected starting with the #1490 fix; preview.1 accepted it.

## ULID parsing

`System.Ulid.Parse` and `TryParse` accept exactly 26 characters from
`0123456789ABCDEFGHJKMNPQRSTVWXYZ`, case-insensitively, with a first character from `0` through `7`.
The rule applies to strings, character spans and UTF-8 spans, including the string and character-span
overloads that take a format provider.
Invalid text returns false from `TryParse` and throws `FormatException` from `Parse`. Generated
ULID value objects use the same validation, including JSON values and dictionary keys. Valid
lowercase input remains accepted and encoding remains canonical uppercase.

This intentionally breaks compatibility with 10.0.0-preview.1 for malformed and overflowing text,
which previously decoded silently. Wrong-length input now throws `FormatException` instead of
`ArgumentException`. See [the design](DESIGN.md#generated-value-type-conformance)
for the parsing and generated JSON contract.

## Source Layout

- src/Exceptions
- src/Internal
- src/Properties
- src/Shared
- src/System
- src/Utilities
