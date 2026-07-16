# Assimalign.Cohesion.Database.Types — Design

The shared scalar type system (area architecture:
[resources/Database/DESIGN.md](../../DESIGN.md) §3.2). One rule drives everything
here: **all type intelligence is spent at encoding time so that comparison time is a
raw unsigned byte compare.** That is the design center `Database.Indexing` documents
for `IndexKey` (the memcmp-key approach of InnoDB and FoundationDB tuples) — this
project owns the encodings; Indexing owns the physical structures that compare them.

## Design intent

Five model engines store and index values. If each defined its own ordering, a key
written by the SQL engine and read by an index cursor — or replicated to another
node — could disagree about order, which corrupts range scans silently. Centralizing
identity (`DatabaseType`), collation, and encoding in one leaf project every model
references makes cross-model ordering a compile-time fact rather than a convention.

## Why-this-not-that decisions

- **Self-describing components (a `DatabaseType` tag byte per component)** rather
  than schema-required decoding. Costs one byte per component; buys: nulls with
  defined order (tag 0 sorts first), deterministic cross-type ordering (by tag),
  decodability without a schema in hand (debugging, generic tooling, replication),
  and loud failures on type mismatch. Schema-directed tagless encoding is a
  measured-need optimization that can arrive later behind the same writer surface.
- **Sealed `Collation` set (binary + invariant) instead of pluggable collations.**
  A collation is a *persistence contract* — its id is inside every encoded key on
  disk. Runtime-registered collations would make file portability depend on process
  configuration. Adding one is deliberately a kernel change here. `Binary` compares
  by Unicode code point (equals UTF-8 byte order), *not* `string.CompareOrdinal` —
  UTF-16 code-unit order disagrees with code-point order for supplementary-plane
  characters, and the byte encoding is the ground truth.
- **Linguistic string keys carry sort key + original bytes.** Culture sort keys are
  not reversible, and the acceptance bar requires round-trips. Layout: escaped sort
  key (defines order), then length-prefixed original UTF-8 (restores the value,
  breaks collation-equal ties deterministically).
- **Decimal as normalized scientific notation** (sign byte, biased base-10 exponent,
  significant digits `+1`, terminator; negatives stored complemented) rather than
  scaled-integer bit tricks. `System.Decimal`'s 96-bit mantissa + scale
  representation is not order-preserving under any fixed-width bit fold without
  rescaling overflow hazards; digit-string scientific form is trivially correct,
  round-trips exactly (trailing zeros normalize away), and its cost is acceptable
  for key encoding.
- **IEEE-754 total-order fold for floats**, with NaN canonicalized to the *positive*
  quiet NaN — .NET's `double.NaN` constant carries the sign bit and would otherwise
  order below negative infinity. Resulting order: −∞ < finite < −0.0 < +0.0 < +∞ < NaN.
- **Zero-escaping for variable-length payloads** (`0x00` → `0x00 0xFF`, terminator
  `0x00 0x00`): keeps escaped-byte order identical to raw-byte order while making
  component boundaries unambiguous — the standard tuple-encoding scheme.
- **Time semantics:** `DateTime` orders by ticks with the kind preserved but
  non-ordering (store UTC when kinds could mix — documented on the API);
  `DateTimeOffset` orders by UTC instant with the offset preserved and tie-breaking;
  `Guid` orders by RFC 4122 big-endian bytes (not SQL Server's segment order).
- **JSON kinds are not key components.** `DatabaseType.Json`/`JsonBinary` exist as
  identities for storage/coercion, but ordering JSON is a model-level semantic; the
  writer exposes no append for them.
- **`DatabaseValueCodec` — one boxed-value bridge, not per-consumer switches.** The
  wire protocol (#852) moves *untyped* values in both directions: a client encodes
  boxed parameter values, the server decodes them, and result rows make the same
  trip in reverse. Both ends need the identical runtime-type→component mapping;
  duplicating the switch in the server runtime and `Database.Client` would let the
  two drift (a wire-corruption class of bug). The codec dispatches on runtime type
  (`Append`), reads self-describing components back boxed (`Read`), and offers
  single-component helpers (`EncodeComponent`/`DecodeComponent`) as the parameter
  payload format. Strings use `Collation.Binary` — these boundaries need exact
  round-trips, never ordering. This is *not* the `DatabaseValue` union (still a
  non-goal below): no value type is introduced, only a mapping.

## Error model

`DatabaseTypeException` is the project root: unknown collation ids, malformed or
truncated key bytes, and component-type mismatches. Encoding never throws for valid
values; decoding is strict.

## AOT posture

No reflection, no culture lookups beyond the pinned invariant `CompareInfo`,
span-based codecs, `string`/digit work confined to the decimal codec and linguistic
sort keys. `DatabaseKeyReader` is a `ref struct`; `DatabaseKeyWriter` is reusable via
`Reset()` so steady-state key building does not allocate.

## Non-goals

- No boxed value union (`DatabaseValue`) yet — planners and catalogs (#173, #175)
  will motivate its exact shape; encoding does not need it.
- No case-insensitive or per-language collations in the MVP (the id space and this
  file are the extension point).
- No compression or tagless schema-directed encoding (see decision above).
