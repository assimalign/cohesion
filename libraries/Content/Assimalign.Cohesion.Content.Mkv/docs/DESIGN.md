# Assimalign.Cohesion.Content.Mkv — Design

## Design intent

Reserve the Content family's Matroska format surface. The landed source is only an empty
internal `MkvSegmentElement`; the project currently references `Content.Bmff`.
Neither that reference nor the class constitutes a Matroska parser. The eventual format
model should follow the [Content design](../../Assimalign.Cohesion.Content/docs/DESIGN.md)
without introducing media playback or host ownership into a content package.

## Design choices and error model

There is no public parser, writer, disposal contract, or defined malformed-input behavior.
The dependency on BMFF is inherited scaffolding, not a claim that Matroska uses BMFF;
the appropriate EBML integration and format-error translation remain future design work.

## Lifecycle and known constraints

H2 (`4592e3eb`) restored compilation. The only fixture test is explicitly skipped because
it requires the `matroska_test_w1_1.zip` archive and developer-local paths. Its download
helper is not a shipped runtime feature. No media suite currently verifies this scaffold.

## AOT posture and non-goals

The project inherits `IsAotCompatible=true`; the empty implementation introduces no dynamic
code, but no NativeAOT media-processing capability has been demonstrated. Matroska parsing,
writing, codec support, playback, and conformance claims are outside the landed surface.
