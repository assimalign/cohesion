# CommonMark 0.31.2 corpus provenance

- Official distribution: https://spec.commonmark.org/0.31.2/spec.json
- Source: https://github.com/commonmark/commonmark-spec/tree/0.31.2, tag `0.31.2`.
  The repository stores `spec.txt`; `spec.json` is its published, generated example corpus.
- Downloaded once on 2026-10-16; all 652 examples, original bytes unchanged.
- SHA-256: `d431b29d97b6f73e69d547109cf5081578fac931e72afe95639ebe766c1b2a20`.
- Attribution: CommonMark specification by John MacFarlane and contributors.
- License: [CC-BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/), as in the existing
  `MarkdownSpecCorpusTests`. The upstream specification additionally offers a BSD license;
  this corpus uses the CC-BY-SA attribution path.

This fixture is the brief's explicit exception to the repository's no-download rule.
No tests access the network. `degradations.json` was authored from local renderer output,
reviewed against the exclusions in `docs/DESIGN.md`; it is not downloaded test data.

## Comparison and scope

Comparison is exact HTML, using the official `test/spec_tests.py --no-normalize` mode.
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
