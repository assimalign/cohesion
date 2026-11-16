# Window-free compatibility (#1805)

- `Statements.jsonl.gz`: one JSON string per source, in ordinal order. These are the
  review's 13,902 short records (original JSON line length at most 1,000), plus every
  500th long MERGE record from the 26,068-record population. The initial every-50th
  sample and subsequent 1% sample exceeded the runtime limit, so it was reduced to 0.2%. Long non-MERGE stress
  inputs remain covered by their dedicated tests.
  Sources were captured from the `b5602544d` and `e72265197` test trees, including the
  #1805 test additions then in progress; that capture was first committed at `e962993c4`.
- `Acceptance.bin.gz`: low-bit-first acceptance bitmap for `Candidates(source)` in
  source order, excluding only individual candidates with a reserved function call.
  The base is `b5602544d`; the original recording was committed at `e962993c4`.
- `Expectations.json`: source, candidate, acceptance and excluded-candidate counts.
- `ReservedCalls.json`: candidate ordinals containing reserved calls, before filtering.
  Recording this small exclusion list avoids lexing every rename twice in CI.
- `RejectedRenames.jsonl.gz`: the 441 base-accepted renames of rejected original
  inputs, recorded at `b5602544d` and first committed at `e962993c4`.
- `Matrix.bin.gz` and `Matrix.json`: base acceptance and counts for the complete
  `OqlWindowCompatibilityMatrix.Statements()` cross product, recorded at `b5602544d`.
  The matrix runs in Debug and Release. The larger corpus runs only in Release.

`OqlWindowAcceptanceRecorder.Slice` is the slicing tool. In a temporary test Fact,
read the old bitmap, iterate the old compressed JSON lines, and call `Candidates`
from `e962993c4` without changing its order. Advance the old bit index for every
candidate; copy its bit only when its source is selected and it has no reserved
call. Count long MERGE sources in ordinal order and select each 500th. The final
slice was obtained by selecting every tenth record of the initial 2% slice, with
`inputExcludesReserved: true`. No base rerun was needed for these bits. Original
inputs containing an actual reserved call cannot lose that call through this
name-only generator, so their variants remain excluded; other names are still
generated and filtered individually. Header names before lists are not calls.

To record new sources or a changed matrix:

1. Extract `git archive b5602544d` into a temporary directory (or reuse an intact
   extraction). Copy `OqlWindowFreeCorpus.cs`, `OqlWindowCompatibilityMatrix.cs` and
   `OqlWindowAcceptanceRecorder.cs` into its Documents.Language `tests/TestObjects/`.
   Keep the generator order stable. These helpers need no window AST or engine reference.
2. Build that extraction's `build/Tasks/Assimalign.Cohesion.Build.Tasks.csproj` in
   Release with `MSBUILDDISABLENODEREUSE=1`. Add a temporary Fact to its language tests
   and run that csproj in Release, filtering to the recording Fact.
3. For the matrix, call
   `Record(OqlWindowCompatibilityMatrix.Statements(), matrixBitmapPath)`.
   Write `Matrix.json` with Base `b5602544d`, Statements from `Candidates` and
   Accepted from `Accepted`. The full 21 × 24 × 35 matrix includes call arguments,
   implicit NEST collection variables, implicit NEST/JOIN aliases and ARRAY bindings.
4. For a corpus, write the selected source strings as compressed JSON lines in
   ordinal order. Call `Record(sources.SelectMany(OqlWindowFreeCorpus.Candidates),
   acceptanceBitmapPath, reservedCallsPath)`. The third argument produces
   `ReservedCalls.json` from `HasReservedCall` and excludes those candidates from
   the bitmap. Write `Expectations.json` with Sources, Candidates, Accepted and
   ExcludedReservedCandidates (`Excluded`), retaining the source-selection metadata.
   Do not use the unfiltered two-argument recording for this bitmap.

Reserved ordinals count **every** generated candidate, including excluded calls.
Readers advance the candidate ordinal for a skipped reserved call, but leave the
bitmap bit index unchanged, because no bit was written for it. This keeps every
following bit aligned. `Slice` writes the same exclusion list and bitmap convention.
The expanded matrix was rerecorded by this recipe against `b5602544d` for fix pass 3;
every previously recorded site retains its base bit.

For an approved intentional refusal, locate its source and candidate index using
the same generator, clear only that acceptance bit, recompress, and decrement the
accepted count. Record the deciding issue, commit and reason here in that change;
do not silently rerecord against head. A generator change requires regenerating or
reslicing the whole bitmap, not reusing shifted bits. Renderer changes need no
baseline update: the test checks no window nodes and render/reparse/render stability.
The Release corpus theory has 28 contiguous source ranges of at most 512 sources,
with six isolated parser workers per case and disjoint bitmap ranges. The supplemental
441 renames have their own Fact. The corpus collection runs apart from other tests.
Candidate counts are computed arithmetically from rename groups; candidate strings
and their order remain unchanged. Every accepted tree is checked through EXPLAIN
and DML children as well as query/scalar children.
