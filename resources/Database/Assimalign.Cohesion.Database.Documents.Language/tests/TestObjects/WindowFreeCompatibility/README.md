# Window-free compatibility (#1805)

- `Statements.jsonl.gz`: one JSON string per source, in ordinal order. These are the
  review's 13,902 short records (original JSON line length at most 1,000), plus every
  500th long MERGE record from the 26,068-record population. The initial every-50th
  sample and subsequent 1% sample exceeded the runtime limit, so it was reduced to 0.2%. Long non-MERGE stress
  inputs remain covered by their dedicated tests.
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

For new sources or a changed matrix, copy the generators and recorder into an
archive extraction of the recorded base's test project, then invoke `Record` from
a temporary Fact. It returns the exact counts for the corresponding JSON manifest.
No window AST or engine reference is needed. Keep the generator order stable.

For an approved intentional refusal, locate its source and candidate index using
the same generator, clear only that acceptance bit, recompress, and decrement the
accepted count. Record the deciding issue, commit and reason here in that change;
do not silently rerecord against head. A generator change requires regenerating or
reslicing the whole bitmap, not reusing shifted bits. Renderer changes need no
baseline update: the test checks no window nodes and render/reparse/render stability.
Six isolated parser workers process disjoint source/bitmap ranges; the corpus collection
runs apart from the other tests. The generator precomputes rename spans while preserving
the original candidate strings and order.
