# Assimalign.Cohesion.Content.Bmff — Design

## Design intent

An ISO base media file-format box model and reader/writer surface over `Content.Media`.
`BmffBox`, composite boxes, visitors, `BmffReader`, `BmffWriter`, and `BmffStream` separate
container structure from any codec or service host. Explicit box factories dispatch the
known box types; this keeps format handling local to the Content family.

## Error model and ownership

The implementation is incomplete: many boxes and visitor methods throw
`NotImplementedException`; the default reader throws a generic `Exception` for an unknown
box type. These are current limitations, not a promised malformed-input contract.
The intended family-level error model is described in the
[Content design](../../Assimalign.Cohesion.Content/docs/DESIGN.md).
The default reader's `Dispose` closes its underlying stream; callers must account for that ownership.

## Lifecycle and known constraints

H2 (`4592e3eb`) repaired compilation, without claiming parser completeness. The media test
is explicitly skipped because it needs an ISO-BMFF/MP4 file at a developer-local path.
There is no portable conformance corpus or active media acceptance test in this package.

## AOT posture and non-goals

The project inherits `IsAotCompatible=true`. Box construction uses explicit factories;
this documentation pass does not establish NativeAOT publication or full format conformance.
Codec decoding, playback, transcoding, complete writing support, and robust handling of
every ISO-BMFF box are not delivered contracts.
