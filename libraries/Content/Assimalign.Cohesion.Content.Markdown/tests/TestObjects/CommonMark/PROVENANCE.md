# CommonMark 0.31.2 corpus provenance

- Official distribution: https://spec.commonmark.org/0.31.2/spec.json
- Source: https://github.com/commonmark/commonmark-spec/tree/0.31.2, tag `0.31.2`.
  The repository stores `spec.txt`; `spec.json` is its published, generated example corpus.
- Downloaded once on 2026-10-16; all 652 examples, original bytes unchanged.
- SHA-256: `d431b29d97b6f73e69d547109cf5081578fac931e72afe95639ebe766c1b2a20`.
- Attribution: CommonMark specification by John MacFarlane and contributors.
- License: [CC-BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/), as in the existing
  `MarkdownSpecCorpusTests`. The specification text and its examples carry that license; the
  upstream repository's BSD license covers its test scripts, not this file. This fixture keeps
  the CC-BY-SA attribution and license, and the repository's MIT license does not apply to it.

This fixture is the brief's explicit exception to the repository's no-download rule.
No tests access the network. `degradations.json` was authored from local renderer output,
reviewed against the exclusions in `docs/DESIGN.md`; it is not downloaded test data.

## Comparison and scope

Comparison is exact HTML, using the official [`test/spec_tests.py --no-normalize`](https://github.com/commonmark/commonmark-spec/blob/0.31.2/test/spec_tests.py) mode.
No whitespace, entities, attributes, URI escapes, tag spelling, or line endings are normalized.
The upstream runner also offers HTML normalization by default; this harness deliberately uses
its stricter supported mode rather than an approximate HTML normalizer.

The five non-retained sections are Setext headings, Indented code blocks, HTML blocks,
Link reference definitions, and Raw HTML. Every example there has a pinned degradation record,
including negative examples that already equal the spec. No non-retained section passes in full.
The remaining sections cover retained constructs, but mix in those excluded constructs, reference
links, unsupported named entities and email autolinks. Their explicit example-number exceptions
also live in `degradations.json`; they do not waive unrelated retained syntax defects.

Every row records its reason, exact HTML and whether it already matched the spec when introduced.
A newly matching degraded example fails with an instruction to move the section into the retained
set or explicitly revise the mixed-section exclusion. An inventory test reports every section's
counts and rejects omitted examples, stale exclusions and unknown sections.

The existing 38 construct examples (39 theory rows, including both hard-break spellings) remain:
they are small, useful specifications and are not all identical to the official examples.

## Verified coverage

All 652 HTML contracts and all 652 structural round trips pass. Of the HTML contracts, 406 are
retained examples and 246 pin degradation; 32 degradation examples already matched the spec at
baseline (mostly negative syntax examples). In total 438 outputs equal the specification.
No non-retained section passes in full; no newly conforming degradation example was suppressed.
The harness emits the following counts through xUnit test output (also present in TRX logs).

| Section | Total | Retained | Degradation | Spec-equal |
| --- | ---: | ---: | ---: | ---: |
| Tabs | 11 | 4 | 7 | 4 |
| Backslash escapes | 13 | 10 | 3 | 10 |
| Entity and numeric character references | 17 | 11 | 6 | 11 |
| Precedence | 1 | 1 | 0 | 1 |
| Thematic breaks | 19 | 17 | 2 | 17 |
| ATX headings | 18 | 17 | 1 | 17 |
| Setext headings | 27 | 0 | 27 | 12 |
| Indented code blocks | 12 | 0 | 12 | 3 |
| Fenced code blocks | 29 | 27 | 2 | 27 |
| HTML blocks | 44 | 0 | 44 | 0 |
| Link reference definitions | 27 | 0 | 27 | 5 |
| Paragraphs | 8 | 7 | 1 | 7 |
| Blank lines | 1 | 1 | 0 | 1 |
| Block quotes | 25 | 22 | 3 | 22 |
| List items | 48 | 32 | 16 | 32 |
| Lists | 26 | 22 | 4 | 22 |
| Inlines | 1 | 1 | 0 | 1 |
| Code spans | 22 | 21 | 1 | 21 |
| Emphasis and strong emphasis | 132 | 129 | 3 | 129 |
| Links | 90 | 41 | 49 | 46 |
| Images | 22 | 8 | 14 | 8 |
| Autolinks | 19 | 17 | 2 | 17 |
| Raw HTML | 20 | 0 | 20 | 7 |
| Hard line breaks | 15 | 13 | 2 | 13 |
| Soft line breaks | 2 | 2 | 0 | 2 |
| Textual content | 3 | 3 | 0 | 3 |
